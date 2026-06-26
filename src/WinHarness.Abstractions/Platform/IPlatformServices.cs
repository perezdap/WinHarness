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
    TimeSpan Timeout);

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
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, CommandExecutionMode Mode);
