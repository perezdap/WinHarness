using System.Diagnostics;

namespace WinHarness.Platform;

/// <summary>
/// Captured stdout/stderr command executor.
/// </summary>
public sealed class CapturedCommandExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public async ValueTask<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == CommandExecutionMode.Interactive)
        {
            return await ConPtyCommandExecutor.ExecuteInteractiveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{request.FileName}'.");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        string stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        return new CommandResult(process.ExitCode, stdout, stderr, CommandExecutionMode.Captured);
    }
}
