using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;

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
    public void ClearResetsConversation()
    {
        WinHarnessOptions options = CreateOptions();
        ChatSession session = new("local", "coder", renderMarkdown: false);
        session.Conversation.Add(new WinHarness.Conversation.ConversationMessage(
            WinHarness.Conversation.ConversationRole.User,
            "hello"));

        SlashCommandResult result = SlashCommandProcessor.Execute(options, session, "/clear");

        Assert.IsFalse(result.ShouldExit);
        Assert.AreEqual(0, session.Conversation.Messages.Count);
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
