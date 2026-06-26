using System.Runtime.InteropServices;
using System.Text;

namespace WinHarness.Platform;

/// <summary>
/// Windows Credential Manager-backed credential store.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoCredentials = 1168;

    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!CredRead(targetName, CredentialTypeGeneric, 0, out IntPtr credentialPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<string?>(null);
            }

            throw new InvalidOperationException($"CredRead failed for '{targetName}' with error {error}.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            byte[] secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            return ValueTask.FromResult<string?>(Encoding.Unicode.GetString(secretBytes));
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <inheritdoc />
    public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        byte[] secretBytes = Encoding.Unicode.GetBytes(secret);
        IntPtr secretPtr = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPtr, secretBytes.Length);
            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
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

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException($"CredDelete failed for '{targetName}' with error {error}.");
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!CredEnumerate("WinHarness:*", 0, out uint count, out IntPtr credentialsPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoCredentials)
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([]);
            }

            throw new InvalidOperationException($"CredEnumerate failed with error {error}.");
        }

        try
        {
            List<string> targetNames = new(checked((int)count));
            for (int index = 0; index < count; index++)
            {
                IntPtr credentialPtr = Marshal.ReadIntPtr(credentialsPtr, index * IntPtr.Size);
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
                if (!string.IsNullOrWhiteSpace(credential.TargetName))
                {
                    targetNames.Add(credential.TargetName);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<string>>(targetNames);
        }
        finally
        {
            CredFree(credentialsPtr);
        }
    }

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
