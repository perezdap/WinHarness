using System.ComponentModel;
using System.Diagnostics;

namespace WinHarness.Platform;

/// <summary>
/// Captured stdout/stderr command executor. Delegates interactive mode to
/// <see cref="ConPtyCommandExecutor"/>; all captured execution is handled by
/// <see cref="CapturedCommandExecutorBase"/>.
/// </summary>
public sealed class CapturedCommandExecutor : CapturedCommandExecutorBase
{
    /// <inheritdoc />
    protected override ValueTask<CommandResult> ExecuteInteractiveAsync(CommandRequest request, CancellationToken cancellationToken)
        => ConPtyCommandExecutor.ExecuteInteractiveAsync(request, cancellationToken);
}
