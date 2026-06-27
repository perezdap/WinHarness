namespace WinHarness.Infrastructure.Sessions;

/// <summary>
/// Normalizes a cwd into a session directory key.
/// </summary>
internal static class WorkspaceKeyNormalizer
{
    /// <summary>
    /// Converts an absolute cwd to a lowercase dash-separated workspace key
    /// with the drive colon stripped.
    /// </summary>
    public static string FromCwd(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        string absolute = Path.GetFullPath(cwd);
        string normalized = absolute.Replace('\\', '-');
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = string.Concat(normalized.AsSpan(0, 1), normalized.AsSpan(2));
        }

        return normalized.ToLowerInvariant();
    }
}