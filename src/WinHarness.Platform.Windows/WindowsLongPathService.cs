namespace WinHarness.Platform;

/// <summary>
/// Windows long-path normalization.
/// </summary>
public sealed class WindowsLongPathService : ILongPathService
{
    /// <inheritdoc />
    public string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);

        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return @"\\?\" + fullPath;
    }
}
