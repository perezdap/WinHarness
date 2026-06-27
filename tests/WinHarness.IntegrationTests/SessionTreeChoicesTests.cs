using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class SessionTreeChoicesTests
{
    [TestMethod]
    public void BuildChoices_IncludesActiveBranchAndLeafChildren()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);
        session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "root")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        string childId = session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "child")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "grandchild")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        session.BranchTo(childId);

        IReadOnlyList<SessionTreeChoices.Choice> choices = SessionTreeChoices.BuildChoices(session);

        Assert.AreEqual(3, choices.Count);
        Assert.AreEqual(2, choices.Count(static choice => choice.IsOnActiveBranch));
        Assert.IsTrue(choices[0].IsOnActiveBranch);
        Assert.IsTrue(choices[1].IsOnActiveBranch);
        Assert.IsFalse(choices[2].IsOnActiveBranch);
        Assert.AreEqual("root", ((MessageSessionEntry)choices[0].Entry).Message.Text);
        Assert.AreEqual("child", ((MessageSessionEntry)choices[1].Entry).Message.Text);
        Assert.AreEqual("grandchild", ((MessageSessionEntry)choices[2].Entry).Message.Text);
    }

    [TestMethod]
    public void BuildChoices_EmptySessionReturnsNoChoices()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);

        IReadOnlyList<SessionTreeChoices.Choice> choices = SessionTreeChoices.BuildChoices(session);

        Assert.AreEqual(0, choices.Count);
    }

    [TestMethod]
    public void FormatListLabel_MarksActiveBranchWithStar()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);
        session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "hello")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        SessionTreeChoices.Choice choice = SessionTreeChoices.BuildChoices(session).Single();

        string label = SessionTreeChoices.FormatListLabel(choice);

        Assert.StartsWith("*", label, StringComparison.Ordinal);
        StringAssert.Contains(label, "[User]");
        StringAssert.Contains(label, "hello");
    }
}