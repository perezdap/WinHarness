using System.ComponentModel;
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
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            return CommandStartFailure.Create(request, ex);
        }

        if (startedProcess is null)
        {
            return CommandStartFailure.Create(request, null);
        }

        using Process process = startedProcess;
        await WriteStandardInputAsync(process, request.StandardInput).ConfigureAwait(false);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            string timedOutStdout = await stdoutTask.ConfigureAwait(false);
            string timedOutStderr = await stderrTask.ConfigureAwait(false);
            return new CommandResult(1, timedOutStdout, timedOutStderr + Environment.NewLine + "Process timed out.", CommandExecutionMode.Captured);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return new CommandResult(process.ExitCode, stdout, stderr, CommandExecutionMode.Captured);
    }

    private static async ValueTask WriteStandardInputAsync(Process process, string? standardInput)
    {
        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process already exited and its standard input stream is gone.
            }
            catch (System.IO.IOException)
            {
                // The process already exited and its standard input stream is gone.
            }
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // The process already exited and its standard input stream is gone.
        }
    }
}
