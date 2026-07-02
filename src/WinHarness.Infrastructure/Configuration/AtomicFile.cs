namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Atomic file write helpers that prevent corruption from crashes or power loss
/// mid-write by writing to a temp file and replacing the target atomically.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically.
    /// </summary>
    public static async ValueTask WriteAllBytesAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Path has no parent directory.");
        string tempPath = System.IO.Path.Combine(
            directory,
            "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}