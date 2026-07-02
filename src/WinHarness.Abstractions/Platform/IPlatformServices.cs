namespace WinHarness.Platform;

/// <summary>
/// Enables ANSI virtual terminal output when supported.
/// </summary>
public interface IAnsiConsoleConfigurator
{
    /// <summary>
    /// Enables ANSI processing for the current console.
    /// </summary>
    void EnableAnsi();
}

/// <summary>
/// Normalizes paths for Windows long-path-safe operations.
/// </summary>
public interface ILongPathService
{
    /// <summary>
    /// Normalizes a path.
    /// </summary>
    string Normalize(string path);
}

/// <summary>
/// Executes commands for tool usage.
/// </summary>
public interface ICommandExecutor
{
    /// <summary>
    /// Executes a command.
    /// </summary>
    ValueTask<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Command execution request.
/// </summary>
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    CommandExecutionMode Mode,
    TimeSpan Timeout,
    string? StandardInput = null,
    int? MaxOutputBytes = null);

/// <summary>
/// Command execution mode.
/// </summary>
public enum CommandExecutionMode
{
    /// <summary>
    /// Captured stdout/stderr process execution.
    /// </summary>
    Captured,

    /// <summary>
    /// Interactive terminal execution.
    /// </summary>
    Interactive
}

/// <summary>
/// Command execution result.
/// </summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, CommandExecutionMode Mode, bool OutputTruncated = false);

/// <summary>
/// Builds graceful <see cref="CommandResult"/> values when a process cannot be started,
/// so tool callers receive a clean error instead of an unhandled exception.
/// </summary>
public static class CommandStartFailure
{
    /// <summary>
    /// Exit code reported when the requested executable could not be launched.
    /// </summary>
    public const int ExecutableNotFoundExitCode = 127;

    /// <summary>
    /// Creates a failure result describing why the command could not start, including a
    /// hint when the request looks like a Unix shell builtin that has no Windows executable.
    /// </summary>
    public static CommandResult Create(CommandRequest request, Exception? exception)
    {
        string message = $"Failed to start '{request.FileName}': {exception?.Message ?? "executable not found."}";
        string? hint = GetHint(request.FileName);
        if (hint is not null)
        {
            message = message + Environment.NewLine + hint;
        }

        return new CommandResult(ExecutableNotFoundExitCode, string.Empty, message, request.Mode);
    }

    private static string? GetHint(string fileName)
    {
        string token = fileName.Trim();
        int firstSpace = token.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace > 0)
        {
            token = token[..firstSpace];
        }

        return token switch
        {
            "command" or "type" or "which" =>
                "'command -v'/'which' are Unix shell builtins. On Windows use 'where.exe <name>' or 'Get-Command <name>' (via pwsh -c).",
            "ls" => "'ls' is a Unix command. On Windows use 'dir' (via cmd /c) or 'Get-ChildItem' (via pwsh -c).",
            "cat" => "'cat' is a Unix command. On Windows use 'type' (via cmd /c) or 'Get-Content' (via pwsh -c).",
            "sh" or "bash" => "POSIX shells may be unavailable on Windows. Invoke the executable directly, or use 'pwsh -c' / 'cmd /c'.",
            _ => fileName.Contains(' ', StringComparison.Ordinal)
                ? "The 'command' field should be the executable name only; pass flags via the 'arguments' array (e.g. command='firecrawl', arguments=['--version'])."
                : null
        };
    }
}
