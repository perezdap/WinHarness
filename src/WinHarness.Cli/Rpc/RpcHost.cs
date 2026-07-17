using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Serialization;
using WinHarness.Sessions;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Rpc;

/// <summary>
/// Long-lived RPC mode: LF-delimited JSON requests on stdin, responses and
/// turn events on stdout (see docs/design/rpc.md). One session at a time;
/// prompts run serially. Steering and abort act on the running turn.
/// </summary>
internal sealed class RpcHost
{
    private readonly IServiceProvider _services;
    private readonly WinHarnessOptions _options;
    private ChatSession? _session;
    private CancellationTokenSource? _turnCts;
    private Task? _turnTask;

    public RpcHost(IServiceProvider services)
    {
        _services = services;
        _options = services.GetRequiredService<WinHarnessOptions>();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RpcRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize(line, RpcJsonContext.Default.RpcRequest);
            }
            catch (JsonException)
            {
            }

            if (request is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method))
            {
                Emit(RpcResponse.Failure("", "Malformed request: expected single-line JSON with id and method."));
                continue;
            }

            bool shutdown = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (shutdown)
            {
                break;
            }
        }

        await AbortRunningTurnAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> DispatchAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method.ToLowerInvariant())
            {
                case "prompt":
                    await HandlePromptAsync(request, cancellationToken).ConfigureAwait(false);
                    return false;
                case "steer":
                    HandleSteer(request);
                    return false;
                case "abort":
                    await AbortRunningTurnAsync().ConfigureAwait(false);
                    Emit(RpcResponse.Success(request.Id));
                    return false;
                case "new_session":
                    await OpenSessionAsync(request, cancellationToken).ConfigureAwait(false);
                    Emit(RpcResponse.Success(request.Id, SessionInfoResult()));
                    return false;
                case "get_session":
                    Emit(RpcResponse.Success(request.Id, SessionInfoResult()));
                    return false;
                case "set_model":
                    HandleSetModel(request);
                    return false;
                case "shutdown":
                    Emit(RpcResponse.Success(request.Id));
                    return true;
                default:
                    Emit(RpcResponse.Failure(request.Id, $"Unknown method '{request.Method}'."));
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit(RpcResponse.Failure(request.Id, ex.Message));
            return false;
        }
    }

    private static void Emit(RpcResponse response) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(response, RpcJsonContext.Default.RpcResponse));

    private static void EmitEvent(string requestId, JsonChatEvent chatEvent) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(RpcEvent.For(requestId, chatEvent), RpcJsonContext.Default.RpcEvent));
    private async ValueTask HandlePromptAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            Emit(RpcResponse.Failure(request.Id, "prompt requires a non-empty 'prompt' field."));
            return;
        }

        if (_turnTask is { IsCompleted: false })
        {
            Emit(RpcResponse.Failure(request.Id, "A turn is already running; steer, abort, or wait for turn_end."));
            return;
        }

        if (_session is null)
        {
            await OpenSessionAsync(request, cancellationToken).ConfigureAwait(false);
        }

        ChatSession session = _session!;
        IAgentRuntime runtime = _services.GetRequiredService<IAgentRuntime>();
        ConversationState runConversation = session.CreateRunConversation(request.Prompt);
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken turnToken = _turnCts.Token;

        Emit(RpcResponse.Success(request.Id));
        EmitEvent(request.Id, JsonChatEvent.TurnStart(session.ProviderId, session.ModelId));

        _turnTask = Task.Run(() => RunTurnAsync(request.Id, session, runtime, runConversation, turnToken), CancellationToken.None);
        await _turnTask.ConfigureAwait(false);
    }

    private async Task RunTurnAsync(
        string requestId,
        ChatSession session,
        IAgentRuntime runtime,
        ConversationState runConversation,
        CancellationToken turnToken)
    {
        AgentRunRequest request = new(
            session.ProviderId,
            session.ModelId,
            runConversation,
            session.WorkspaceRoot,
            session.ProjectContext,
            session.ReasoningEffort,
            session.ToolFilter,
            session.Steering);

        // Presentation adapter: wraps each event as an RpcEvent at the point
        // the pump reads it. Artifact appends and the failure flag are the
        // pump's.
        ValueTask Present(AgentEvent agentEvent)
        {
            switch (agentEvent.Kind)
            {
                case AgentEventKind.AssistantDelta:
                    EmitEvent(requestId, JsonChatEvent.AssistantDelta(agentEvent.Message));
                    break;
                case AgentEventKind.ToolActivity when agentEvent.ToolActivity is { } info:
                    EmitEvent(requestId, JsonChatEvent.Tool(info));
                    break;
                case AgentEventKind.Failed:
                    EmitEvent(requestId, JsonChatEvent.FromError(agentEvent.Message));
                    break;
                case AgentEventKind.Completed when agentEvent.TurnArtifacts is { } artifacts:
                    ConversationMessage? assistant = artifacts.Messages
                        .LastOrDefault(static message => message.Role == ConversationRole.Assistant);
                    EmitEvent(requestId, JsonChatEvent.AssistantMessage(assistant?.Text ?? string.Empty));
                    if (assistant?.Usage is { } usage)
                    {
                        EmitEvent(requestId, JsonChatEvent.Usage(usage.InputTokens, usage.OutputTokens));
                    }

                    break;
            }

            return ValueTask.CompletedTask;
        }

        TurnOutcome outcome;
        try
        {
            outcome = await new TurnPump(session)
                .RunAsync(request, runtime, Present, turnToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitEvent(requestId, JsonChatEvent.FromError("Turn aborted."));
            return;
        }
        catch (Exception ex)
        {
            EmitEvent(requestId, JsonChatEvent.FromError(ex.Message));
            return;
        }

        if (outcome.FailureMessage is null)
        {
            EmitEvent(requestId, JsonChatEvent.TurnEnd());
        }
    }

    private void HandleSteer(RpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            Emit(RpcResponse.Failure(request.Id, "steer requires a non-empty 'text' field."));
            return;
        }

        if (_session is null || _turnTask is not { IsCompleted: false })
        {
            Emit(RpcResponse.Failure(request.Id, "No turn is running; send a prompt instead."));
            return;
        }

        _session.Steering.Enqueue(request.Text);
        Emit(RpcResponse.Success(request.Id));
    }

    private void HandleSetModel(RpcRequest request)
    {
        if (_session is null)
        {
            Emit(RpcResponse.Failure(request.Id, "No session open; send new_session or prompt first."));
            return;
        }

        if (_turnTask is { IsCompleted: false })
        {
            Emit(RpcResponse.Failure(request.Id, "Cannot switch models while a turn is running."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.ProviderId))
        {
            _session.ProviderId = request.ProviderId;
        }

        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            _session.ModelId = request.ModelId;
        }

        Emit(RpcResponse.Success(request.Id, SessionInfoResult()));
    }

    private async ValueTask OpenSessionAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        await AbortRunningTurnAsync().ConfigureAwait(false);

        string providerId = request.ProviderId ?? _options.DefaultProvider;
        string modelId = request.ModelId ?? _options.DefaultModel;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("No provider/model configured; pass providerId and modelId or set defaults.");
        }

        ChatSessionBootstrapRequest bootstrapRequest = new(
            IsOneShot: false,
            NoSession: false,
            ContinueSession: request.Session is null,
            Resume: false,
            Session: request.Session,
            Name: request.Name,
            ApproveOverride: null);

        SessionManagerFactory factory = _services.GetRequiredService<SessionManagerFactory>();
        IContextFileLoader contextFileLoader = _services.GetRequiredService<IContextFileLoader>();
        ISessionManager sessionManager = await ChatSessionBootstrap.ResolveAsync(
            factory,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);

        // RPC is non-interactive: trusted only via a saved decision or
        // defaultProjectTrust=always (or when nothing project-local exists).
        bool trusted = !TrustStore.HasProjectLocalResources(Environment.CurrentDirectory) ||
            new TrustStore().GetDecision(Environment.CurrentDirectory) == true ||
            string.Equals(_options.DefaultProjectTrust, "always", StringComparison.OrdinalIgnoreCase);

        _session = ChatSessionBootstrap.CreateChatSession(
            sessionManager,
            contextFileLoader,
            providerId,
            modelId,
            renderMarkdown: false,
            trusted);
    }

    private JsonElement SessionInfoResult()
    {
        Dictionary<string, string> info = new(StringComparer.Ordinal)
        {
            ["sessionId"] = _session?.SessionManager.Header.Id ?? "",
            ["file"] = _session?.SessionManager.SessionFilePath ?? "",
            ["providerId"] = _session?.ProviderId ?? "",
            ["modelId"] = _session?.ModelId ?? "",
        };
        return JsonSerializer.SerializeToElement(info, WinHarnessJsonSerializerContext.Default.DictionaryStringString);
    }

    private async ValueTask AbortRunningTurnAsync()
    {
        if (_turnCts is { } cts && _turnTask is { IsCompleted: false } task)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _turnCts?.Dispose();
        _turnCts = null;
        _turnTask = null;
    }
}
