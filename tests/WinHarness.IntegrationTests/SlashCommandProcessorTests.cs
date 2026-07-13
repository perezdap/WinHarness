using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using Spectre.Console.Testing;
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
    public void BareSlashFallsBackToHelpWhenNonInteractive()
    {
        // Under test the stdin is redirected, so the palette degrades to the help listing.
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/");

        Assert.IsFalse(result.ShouldExit);
        CollectionAssert.AreEqual(
            SlashCommandCatalog.ToHelpLines().ToList(),
            result.Messages.ToList());
    }

    [TestMethod]
    public void CatalogCoversHelpAndIsNonEmpty()
    {
        Assert.IsTrue(SlashCommandCatalog.Commands.Count > 0);
        foreach (SlashCommandInfo command in SlashCommandCatalog.Commands)
        {
            StringAssert.StartsWith(command.Name, "/");
            Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description));
        }

        // Every catalog entry should render into the help listing.
        var helpText = string.Join("\n", SlashCommandCatalog.ToHelpLines());
        foreach (SlashCommandInfo command in SlashCommandCatalog.Commands)
        {
            StringAssert.Contains(helpText, command.Name);
        }
    }

    [TestMethod]
    public async Task PaletteSelectedEffortPromptsForValue()
    {
        TestConsole console = NewInteractiveConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // default -> none
        console.Input.PushKey(ConsoleKey.DownArrow); // none -> low
        console.Input.PushKey(ConsoleKey.Enter);
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            WinHarnessOptions options = CreateOptions();
            ChatSession session = new("local", "coder", renderMarkdown: false);
            MethodInfo executor = typeof(SlashCommandProcessor).GetMethod(
                "ExecutePaletteSelectionAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            ValueTask<SlashCommandResult> task = (ValueTask<SlashCommandResult>)executor.Invoke(
                null,
                [options, session, new SlashCommandInfo("/effort", "[level]", "test"), null])!;

            SlashCommandResult result = await task.ConfigureAwait(false);

            Assert.IsFalse(result.ShouldExit);
            Assert.AreEqual("low", session.ReasoningEffort);
            CollectionAssert.Contains(result.Messages.ToList(), "Reasoning effort set to 'low'.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    [TestMethod]
    public void PaletteChoiceFormatterEscapesSpectreMarkup()
    {
        MethodInfo formatter = typeof(SlashCommandProcessor).GetMethod(
            "FormatSlashCommandChoice",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        TestConsole console = new();

        foreach (SlashCommandInfo command in SlashCommandCatalog.Commands)
        {
            string formatted = (string)formatter.Invoke(null, [command])!;

            // SelectionPrompt renders converter output as Spectre markup. Optional
            // argument hints such as "[id-or-path]" must therefore be escaped.
            console.Write(new Markup(formatted));
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

    private static TestConsole NewInteractiveConsole()
    {
        TestConsole console = new TestConsole().Width(80);
        typeof(Capabilities).GetProperty(nameof(Capabilities.Interactive))!.SetValue(
            console.Profile.Capabilities, true);
        return console;
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
