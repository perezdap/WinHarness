using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

internal sealed record SlashCommandContext(
    IServiceProvider Services,
    SessionManagerFactory SessionFactory,
    IAgentRuntime AgentRuntime,
    CancellationToken CancellationToken);