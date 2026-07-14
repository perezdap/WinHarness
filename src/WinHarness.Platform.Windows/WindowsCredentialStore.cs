using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinHarness.Platform;

/// <summary>
/// Windows Credential Manager-backed credential store.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CredentialBlobMaxBytes = 5 * 512;
    private const int PayloadHeaderSize = 5;
    private const int ManifestPayloadSize = PayloadHeaderSize + sizeof(int) + 16;
    private const int MaxChunkCount = 4096;
    private const int MaxGenericTargetNameLength = 32767;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoCredentials = 1168;
    private const byte DirectPayloadKind = 1;
    private const byte ManifestPayloadKind = 2;
    private const byte ChunkPayloadKind = 3;
    private const string ChunkMarker = ":chunk:";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> PayloadMagic => "WHC1"u8;

    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        using IDisposable storeLock = AcquireStoreLock();

        if (!TryReadCredential(targetName, out byte[]? payload))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(DecodePayload(targetName, payload!));
    }

    /// <inheritdoc />
    public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        using IDisposable storeLock = AcquireStoreLock();

        byte[] secretBytes = StrictUtf8.GetBytes(secret);
        byte[]? existingPayload = TryReadCredential(targetName, out byte[]? currentPayload)
            ? currentPayload
            : null;

        if (existingPayload is not null && HasPayloadMagic(existingPayload) &&
            existingPayload.Length > PayloadHeaderSize - 1 &&
            existingPayload[PayloadHeaderSize - 1] == ManifestPayloadKind &&
            !TryReadManifest(existingPayload, out _))
        {
            throw new InvalidOperationException($"Credential '{targetName}' has an invalid chunk manifest.");
        }

        if (secretBytes.Length + PayloadHeaderSize <= CredentialBlobMaxBytes)
        {
            WriteCredential(targetName, CreatePayload(DirectPayloadKind, secretBytes));
            DeleteChunkSet(existingPayload, targetName);
            CleanupOrphanChunks();
            return ValueTask.CompletedTask;
        }

        int chunkCapacity = CredentialBlobMaxBytes - PayloadHeaderSize;
        int chunkCount = checked((secretBytes.Length + chunkCapacity - 1) / chunkCapacity);
        if (chunkCount > MaxChunkCount)
        {
            throw new InvalidOperationException(
                $"Credential '{targetName}' is too large for Windows Credential Manager ({secretBytes.Length:N0} UTF-8 bytes).");
        }

        Guid chunkSetId = Guid.NewGuid();
        string lastChunkTarget = GetChunkTargetName(targetName, chunkSetId, chunkCount - 1);
        if (lastChunkTarget.Length > MaxGenericTargetNameLength)
        {
            throw new InvalidOperationException(
                $"Credential target '{targetName}' is too long to store a chunked secret in Windows Credential Manager.");
        }

        List<string> writtenChunks = new(chunkCount);
        try
        {
            for (int index = 0; index < chunkCount; index++)
            {
                int offset = index * chunkCapacity;
                int length = Math.Min(chunkCapacity, secretBytes.Length - offset);
                string chunkTarget = GetChunkTargetName(targetName, chunkSetId, index);
                WriteCredential(chunkTarget, CreatePayload(ChunkPayloadKind, secretBytes.AsSpan(offset, length)));
                writtenChunks.Add(chunkTarget);
            }

            WriteCredential(targetName, CreateManifestPayload(chunkSetId, chunkCount));
        }
        catch
        {
            foreach (string chunkTarget in writtenChunks)
            {
                DeleteCredential(chunkTarget);
            }

            throw;
        }

        DeleteChunkSet(existingPayload, targetName);
        CleanupOrphanChunks();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        using IDisposable storeLock = AcquireStoreLock();

        if (!TryReadCredential(targetName, out byte[]? payload))
        {
            CleanupOrphanChunks();
            return ValueTask.CompletedTask;
        }

        DeleteCredential(targetName);
        DeleteChunkSet(payload, targetName);
        CleanupOrphanChunks();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        List<CredentialEntry> entries = ReadCredentialEntries();
        List<string> targetNames = new(entries.Count);
        foreach (CredentialEntry entry in entries)
        {
            if (!IsOwnedChunkEntry(entry))
            {
                targetNames.Add(entry.TargetName);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(targetNames);
    }

    private static List<CredentialEntry> ReadCredentialEntries()
    {
        if (!CredEnumerate("WinHarness:*", 0, out uint count, out IntPtr credentialsPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoCredentials)
            {
                return [];
            }

            throw new InvalidOperationException($"CredEnumerate failed with error {error}.");
        }

        try
        {
            List<CredentialEntry> entries = new(checked((int)count));
            for (int index = 0; index < count; index++)
            {
                IntPtr credentialPtr = Marshal.ReadIntPtr(credentialsPtr, index * IntPtr.Size);
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
                if (!string.IsNullOrWhiteSpace(credential.TargetName))
                {
                    entries.Add(new CredentialEntry(credential.TargetName, ReadCredentialBlob(credential)));
                }
            }

            return entries;
        }
        finally
        {
            CredFree(credentialsPtr);
        }
    }

    private static void CleanupOrphanChunks()
    {
        List<CredentialEntry> entries = ReadCredentialEntries();
        HashSet<string> referencedChunks = new(StringComparer.OrdinalIgnoreCase);
        foreach (CredentialEntry entry in entries)
        {
            if (TryReadManifest(entry.Payload, out ChunkManifest manifest))
            {
                for (int index = 0; index < manifest.Count; index++)
                {
                    referencedChunks.Add(GetChunkTargetName(entry.TargetName, manifest.SetId, index));
                }
            }
        }

        foreach (CredentialEntry entry in entries)
        {
            if (IsOwnedChunkEntry(entry) && !referencedChunks.Contains(entry.TargetName))
            {
                DeleteCredential(entry.TargetName);
            }
        }
    }

    private static bool TryReadCredential(string targetName, out byte[]? payload)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out IntPtr credentialPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                payload = null;
                return false;
            }

            throw new InvalidOperationException($"CredRead failed for '{targetName}' with error {error}.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            payload = ReadCredentialBlob(credential);
            return true;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static string DecodePayload(string targetName, byte[] payload)
    {
        if (!HasPayloadMagic(payload) || payload.Length < PayloadHeaderSize)
        {
            // Credentials written by older WinHarness versions used UTF-16.
            return Encoding.Unicode.GetString(payload);
        }

        return payload[PayloadHeaderSize - 1] switch
        {
            DirectPayloadKind => StrictUtf8.GetString(payload, PayloadHeaderSize, payload.Length - PayloadHeaderSize),
            ManifestPayloadKind when payload.Length == ManifestPayloadSize => DecodeChunkedPayload(targetName, payload),
            _ => Encoding.Unicode.GetString(payload)
        };
    }

    private static string DecodeChunkedPayload(string targetName, byte[] manifestPayload)
    {
        if (manifestPayload.Length != ManifestPayloadSize)
        {
            throw new InvalidOperationException($"Credential '{targetName}' has an invalid chunk manifest.");
        }

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(manifestPayload.AsSpan(PayloadHeaderSize, sizeof(int)));
        if (chunkCount is < 1 or > MaxChunkCount)
        {
            throw new InvalidOperationException($"Credential '{targetName}' has an invalid chunk count.");
        }

        Guid chunkSetId = new(manifestPayload.AsSpan(PayloadHeaderSize + sizeof(int), 16));
        List<byte[]> chunks = new(chunkCount);
        int totalLength = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            string chunkTarget = GetChunkTargetName(targetName, chunkSetId, index);
            if (!TryReadCredential(chunkTarget, out byte[]? chunkPayload) || chunkPayload is null)
            {
                throw new InvalidOperationException($"Credential '{targetName}' is missing chunk {index + 1} of {chunkCount}.");
            }

            if (!HasPayloadMagic(chunkPayload) || chunkPayload.Length < PayloadHeaderSize ||
                chunkPayload[PayloadHeaderSize - 1] != ChunkPayloadKind)
            {
                throw new InvalidOperationException($"Credential '{targetName}' has an invalid chunk {index + 1}.");
            }

            byte[] chunk = chunkPayload[PayloadHeaderSize..];
            totalLength = checked(totalLength + chunk.Length);
            chunks.Add(chunk);
        }

        byte[] combined = new byte[totalLength];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return StrictUtf8.GetString(combined);
    }

    private static bool TryReadManifest(byte[] payload, out ChunkManifest manifest)
    {
        if (!HasPayloadMagic(payload) || payload.Length != ManifestPayloadSize ||
            payload[PayloadHeaderSize - 1] != ManifestPayloadKind)
        {
            manifest = default;
            return false;
        }

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(PayloadHeaderSize, sizeof(int)));
        if (chunkCount is < 1 or > MaxChunkCount)
        {
            throw new InvalidOperationException("Credential chunk manifest has an invalid chunk count.");
        }

        manifest = new ChunkManifest(
            new Guid(payload.AsSpan(PayloadHeaderSize + sizeof(int), 16)),
            chunkCount);
        return true;
    }

    private static byte[] CreatePayload(byte kind, ReadOnlySpan<byte> content)
    {
        byte[] payload = new byte[PayloadHeaderSize + content.Length];
        PayloadMagic.CopyTo(payload);
        payload[PayloadHeaderSize - 1] = kind;
        content.CopyTo(payload.AsSpan(PayloadHeaderSize));
        return payload;
    }

    private static byte[] CreateManifestPayload(Guid chunkSetId, int chunkCount)
    {
        byte[] payload = new byte[ManifestPayloadSize];
        PayloadMagic.CopyTo(payload);
        payload[PayloadHeaderSize - 1] = ManifestPayloadKind;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(PayloadHeaderSize, sizeof(int)), chunkCount);
        chunkSetId.TryWriteBytes(payload.AsSpan(PayloadHeaderSize + sizeof(int), 16));
        return payload;
    }

    private static bool HasPayloadMagic(byte[] payload) =>
        payload.Length >= PayloadMagic.Length && payload.AsSpan(0, PayloadMagic.Length).SequenceEqual(PayloadMagic);

    private static string GetChunkTargetName(string targetName, Guid chunkSetId, int index) =>
        $"{targetName}{ChunkMarker}{chunkSetId:N}:{index}";

    private static bool IsGeneratedChunkTarget(string targetName)
    {
        int markerIndex = targetName.LastIndexOf(ChunkMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        int guidStart = markerIndex + ChunkMarker.Length;
        int indexSeparator = targetName.IndexOf(':', guidStart);
        return indexSeparator > guidStart &&
            Guid.TryParseExact(targetName[guidStart..indexSeparator], "N", out _) &&
            int.TryParse(
                targetName[(indexSeparator + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int index) &&
            index is >= 0 and < MaxChunkCount;
    }

    private static bool IsOwnedChunkEntry(CredentialEntry entry) =>
        IsGeneratedChunkTarget(entry.TargetName) &&
        HasPayloadMagic(entry.Payload) &&
        entry.Payload.Length >= PayloadHeaderSize &&
        entry.Payload[PayloadHeaderSize - 1] == ChunkPayloadKind;

    private static byte[] ReadCredentialBlob(NativeCredential credential)
    {
        int size = checked((int)credential.CredentialBlobSize);
        byte[] payload = new byte[size];
        if (size > 0)
        {
            Marshal.Copy(credential.CredentialBlob, payload, 0, size);
        }

        return payload;
    }

    private static void WriteCredential(string targetName, byte[] payload)
    {
        if (payload.Length > CredentialBlobMaxBytes)
        {
            throw new InvalidOperationException(
                $"Credential '{targetName}' exceeds the Windows Credential Manager limit of {CredentialBlobMaxBytes:N0} bytes.");
        }

        IntPtr secretPtr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            if (payload.Length > 0)
            {
                Marshal.Copy(payload, 0, secretPtr, payload.Length);
            }

            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)payload.Length,
                CredentialBlob = secretPtr,
                Persist = CredentialPersistLocalMachine,
                UserName = "WinHarness"
            };

            IntPtr credentialPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
            try
            {
                Marshal.StructureToPtr(credential, credentialPtr, fDeleteOld: false);
                if (!CredWrite(credentialPtr, 0))
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException($"CredWrite failed for '{targetName}' with error {error}.");
                }
            }
            finally
            {
                Marshal.DestroyStructure<NativeCredential>(credentialPtr);
                Marshal.FreeHGlobal(credentialPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(secretPtr);
        }
    }

    private static void DeleteChunkSet(byte[]? payload, string targetName)
    {
        if (payload is not null && TryReadManifest(payload, out ChunkManifest manifest))
        {
            for (int index = 0; index < manifest.Count; index++)
            {
                DeleteCredential(GetChunkTargetName(targetName, manifest.SetId, index));
            }
        }
    }

    private static void DeleteCredential(string targetName)
    {
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException($"CredDelete failed for '{targetName}' with error {error}.");
            }
        }
    }

    private static IDisposable AcquireStoreLock()
    {
        Mutex mutex = new(initiallyOwned: false, name: "WinHarness.CredentialStore");
        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // Ownership transfers to this call after an abandoned writer exits.
            }

            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private readonly record struct CredentialEntry(string TargetName, byte[] Payload);

    private readonly record struct ChunkManifest(Guid SetId, int Count);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(IntPtr userCredential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    [LibraryImport("advapi32.dll", SetLastError = false)]
    private static partial void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;

        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;

        public uint CredentialBlobSize;

        public IntPtr CredentialBlob;

        public uint Persist;

        public uint AttributeCount;

        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}
