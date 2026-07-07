using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WinHarness.Platform;

/// <summary>
/// Shared base for captured stdout/stderr command execution. Derived classes
/// override <see cref="ExecuteInteractiveAsync"/> to delegate to a
/// platform-specific PTY (or reject interactive mode entirely).
/// </summary>
public abstract class CapturedCommandExecutorBase : ICommandExecutor
{
    /// <inheritdoc />
    public async ValueTask<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == CommandExecutionMode.Interactive)
        {
            return await ExecuteInteractiveAsync(request, cancellationToken).ConfigureAwait(false);
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

        Task<(string Content, bool Truncated)> stdoutTask = ReadOutputWithLimitAsync(process.StandardOutput, request.MaxOutputBytes, cancellationToken);
        Task<(string Content, bool Truncated)> stderrTask = ReadOutputWithLimitAsync(process.StandardError, request.MaxOutputBytes, cancellationToken);
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

            using CancellationTokenSource killTimeout = new(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            (string timedOutStdout, bool timedOutTruncated) = await stdoutTask.ConfigureAwait(false);
            (string timedOutStderr, _) = await stderrTask.ConfigureAwait(false);
            return new CommandResult(1, timedOutStdout, timedOutStderr + Environment.NewLine + "Process timed out.", CommandExecutionMode.Captured, timedOutTruncated);
        }

        (string stdout, bool stdoutTruncated) = await stdoutTask.ConfigureAwait(false);
        (string stderr, bool stderrTruncated) = await stderrTask.ConfigureAwait(false);

        return new CommandResult(process.ExitCode, stdout, stderr, CommandExecutionMode.Captured, stdoutTruncated || stderrTruncated);
    }

    /// <summary>
    /// Executes an interactive command. Derived classes either delegate to a
    /// platform-specific PTY or reject the request.
    /// </summary>
    protected abstract ValueTask<CommandResult> ExecuteInteractiveAsync(CommandRequest request, CancellationToken cancellationToken);

    private static async Task<(string Content, bool Truncated)> ReadOutputWithLimitAsync(
        StreamReader reader,
        int? maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes is not { } limit)
        {
            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return (content, false);
        }

        var buffer = new char[4096];
        var sb = new StringBuilder();
        int totalBytes = 0;

        while (true)
        {
            int charsRead = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (charsRead == 0)
            {
                break;
            }

            string chunk = new(buffer, 0, charsRead);
            int chunkBytes = Encoding.UTF8.GetByteCount(chunk);

            if (totalBytes + chunkBytes > limit)
            {
                // Fill remaining budget character by character to avoid
                // splitting a multi-byte UTF-8 code point.
                int remaining = limit - totalBytes;
                for (int i = 0; i < charsRead && remaining > 0; i++)
                {
                    int charBytes = Encoding.UTF8.GetByteCount(chunk, i, 1);
                    if (charBytes > remaining)
                    {
                        break;
                    }

                    sb.Append(chunk[i]);
                    remaining -= charBytes;
                }

                // Drain remaining output to prevent pipe blocking.
                await DrainReaderAsync(reader, cancellationToken).ConfigureAwait(false);
                return (sb.ToString(), true);
            }

            sb.Append(chunk);
            totalBytes += chunkBytes;
        }

        return (sb.ToString(), false);
    }

    private static async Task DrainReaderAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var discard = new char[4096];
        try
        {
            while (await reader.ReadAsync(discard, cancellationToken).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) { }
        catch (System.IO.IOException) { }
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
