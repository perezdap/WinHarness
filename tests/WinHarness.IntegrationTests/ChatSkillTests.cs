using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Conversation;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ChatSkillTests
{
    [TestMethod]
    public void ChatSessionDiscoversAndSelectsSkill()
    {
        string originalDirectory = Environment.CurrentDirectory;
        string root = CreateTempDirectory();
        try
        {
            string skillDirectory = Path.Combine(root, ".winharness", "skills", "tdd");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(
                Path.Combine(skillDirectory, "SKILL.md"),
                """
---
name: tdd
description: Test-driven development workflow
---
# TDD

Write a failing test first.
""");

            Environment.CurrentDirectory = root;
            ChatSession session = new("provider", "model", renderMarkdown: false);

            SkillDefinition? discovered = session.Skills.FirstOrDefault(skill =>
                string.Equals(skill.Name, "tdd", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(discovered);
            StringAssert.Contains(discovered!.Description, "Test-driven");

            SlashCommandResult result = SlashCommandProcessor.Execute(new WinHarnessOptions(), session, "/skill tdd");

            Assert.IsFalse(result.ShouldExit);
            Assert.IsNotNull(session.SelectedSkill);
            Assert.AreEqual("tdd", session.SelectedSkill!.Name);

            WinHarness.Conversation.Conversation runConversation = session.CreateRunConversation("implement feature");
            CollectionAssert.AreEqual(
                new[] { ConversationRole.System, ConversationRole.User },
                runConversation.Messages.Select(static message => message.Role).ToArray());
            StringAssert.Contains(runConversation.Messages[0].Text, "Skill selected: tdd");
            StringAssert.Contains(runConversation.Messages[0].Text, "Write a failing test first.");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public void SkillOffClearsSelectedSkill()
    {
        ChatSession session = new("provider", "model", renderMarkdown: false)
        {
            SelectedSkill = new SkillDefinition("x", "desc", "x", "content")
        };

        SlashCommandResult result = SlashCommandProcessor.Execute(new WinHarnessOptions(), session, "/skill off");

        Assert.IsFalse(result.ShouldExit);
        Assert.IsNull(session.SelectedSkill);
        CollectionAssert.Contains(result.Messages.ToArray(), "Skill cleared.");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinHarnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
