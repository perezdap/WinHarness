using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class SlashCommandProcessorTests
{
    [TestMethod]
    public void ToggleMarkdownUpdatesSession()
    {
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/markdown");

        Assert.IsFalse(result.ShouldExit);
        Assert.IsTrue(session.RenderMarkdown);
        CollectionAssert.Contains(result.Messages.ToList(), "Markdown rendering on.");
    }

    [TestMethod]
    public void SwitchProviderPromotesFirstModelWhenCurrentModelIsUnavailable()
    {
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/provider hosted");

        Assert.IsFalse(result.ShouldExit);
        Assert.AreEqual("hosted", session.ProviderId);
        Assert.AreEqual("gpt-primary", session.ModelId);
    }

    [TestMethod]
    public void ClearResetsConversationView()
    {
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);
        session.Conversation.Add(ConversationMessage.FromText(ConversationRole.User, "hello"));

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/clear");

        Assert.IsFalse(result.ShouldExit);
        Assert.AreEqual(0, session.Conversation.Messages.Count);
        CollectionAssert.Contains(result.Messages.ToList(), "Conversation view cleared.");
    }

    [TestMethod]
    public async Task NewCreatesPersistedSessionFile()
    {
        string originalDirectory = Environment.CurrentDirectory;
        string workspace = Path.Combine(Path.GetTempPath(), "WinHarnessSlash", Guid.NewGuid().ToString("N"));
        string sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessSlash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            Environment.CurrentDirectory = workspace;
            WinHarnessOptions options = CreateOptions();
            ChatSession session = new(SessionManager.InMemory(workspace), null, workspace, "local", "coder", renderMarkdown: false);
            JsonlSessionStore store = new(sessionsRoot);
            SessionManagerFactory factory = new(store);
            SlashCommandContext context = new(null!, factory, null!, CancellationToken.None);

            SlashCommandResult result = await SlashCommandProcessor.ExecuteAsync(options, session, "/new", context);

            Assert.IsFalse(result.ShouldExit);
            Assert.IsFalse(session.IsEphemeral);
            Assert.IsNotNull(session.SessionManager.SessionFilePath);
            Assert.IsTrue(File.Exists(session.SessionManager.SessionFilePath));
            StringAssert.Contains(result.Messages[0], "New session file created.");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(sessionsRoot))
            {
                Directory.Delete(sessionsRoot, recursive: true);
            }

            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProviderSwitchAppendsModelChangeWhenPersisted()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "WinHarnessSlash", Guid.NewGuid().ToString("N"));
        string sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessSlash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            JsonlSessionStore store = new(sessionsRoot);
            SessionManagerFactory factory = new(store);
            ISessionManager persisted = await factory.CreateAsync(workspace, CancellationToken.None);
            ChatSession session = new(persisted, null, workspace, "local", "coder", renderMarkdown: false);
            WinHarnessOptions options = CreateOptions();
            SlashCommandContext context = new(null!, factory, null!, CancellationToken.None);

            SlashCommandResult result = await SlashCommandProcessor.ExecuteAsync(options, session, "/provider hosted", context);

            Assert.IsFalse(result.ShouldExit);
            Assert.AreEqual("hosted", session.ProviderId);
            Assert.IsTrue(session.SessionManager.GetActiveBranch().Any(static entry => entry is ModelChangeSessionEntry));
        }
        finally
        {
            if (Directory.Exists(sessionsRoot))
            {
                Directory.Delete(sessionsRoot, recursive: true);
            }

            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ExitRequestsSessionEnd()
    {
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/exit");

        Assert.IsTrue(result.ShouldExit);
        Assert.AreEqual(0, result.Messages.Count);
    }

    private static WinHarnessOptions CreateOptions()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "coder"
        };

        ProviderOptions local = new()
        {
            Id = "local",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1"
        };
        local.Models.Add(new ModelOptions
        {
            Id = "coder",
            ProviderModelId = "qwen2.5-coder:latest"
        });

        ProviderOptions hosted = new()
        {
            Id = "hosted",
            Kind = "openai-compatible",
            BaseUrl = "https://api.openai.com/v1"
        };
        hosted.Models.Add(new ModelOptions
        {
            Id = "gpt-primary",
            ProviderModelId = "gpt-4.1"
        });

        options.Providers.Add(local);
        options.Providers.Add(hosted);
        return options;
    }
}
