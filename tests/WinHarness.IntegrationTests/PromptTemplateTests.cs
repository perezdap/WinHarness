using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class PromptTemplateTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinHarnessTemplates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".winharness", "prompts"));
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PromptTemplate Write(string fileName, string content)
    {
        string path = Path.Combine(_root, ".winharness", "prompts", fileName);
        File.WriteAllText(path, content);
        return PromptTemplateRegistry.Discover(_root, includeProjectLocal: true)
            .Single(template => template.FilePath == path);
    }

    [TestMethod]
    public void DiscoversTemplateWithFrontmatter()
    {
        PromptTemplate template = Write("review.md", "---\nname: code-review\ndescription: Review code\n---\nReview: {{focus}}");

        Assert.AreEqual("code-review", template.Name);
        Assert.AreEqual("Review code", template.Description);
        Assert.AreEqual("Review: {{focus}}", template.Body);
    }

    [TestMethod]
    public void FileNameIsFallbackName()
    {
        PromptTemplate template = Write("summarize.md", "Summarize {{input}}");

        Assert.AreEqual("summarize", template.Name);
    }

    [TestMethod]
    public void UntrustedDiscoverySkipsProjectTemplates()
    {
        Write("evil.md", "do bad things");

        Assert.AreEqual(0, PromptTemplateRegistry.Discover(_root, includeProjectLocal: false)
            .Count(template => template.Name == "evil"));
    }

    [TestMethod]
    public void ExpandFillsNamedAndInputPlaceholders()
    {
        PromptTemplate template = Write("t.md", "Focus on {{focus}}. Input: {{input}}");

        (string prompt, IReadOnlyList<string> missing) = PromptTemplateRegistry.Expand(
            template,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["focus"] = "security" },
            "the login code");

        Assert.AreEqual(0, missing.Count);
        Assert.AreEqual("Focus on security. Input: the login code", prompt);
    }

    [TestMethod]
    public void ExpandReportsMissingPlaceholders()
    {
        PromptTemplate template = Write("t.md", "Focus on {{focus}} and {{depth}}");

        (_, IReadOnlyList<string> missing) = PromptTemplateRegistry.Expand(
            template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");

        CollectionAssert.AreEquivalent(new[] { "focus", "depth" }, missing.ToArray());
    }

    [TestMethod]
    public void FreeTextAppendsWhenNoInputSlot()
    {
        PromptTemplate template = Write("t.md", "Fixed body");

        (string prompt, _) = PromptTemplateRegistry.Expand(
            template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "extra context");

        StringAssert.StartsWith(prompt, "Fixed body");
        StringAssert.EndsWith(prompt, "extra context");
    }

    [TestMethod]
    public void ParsesQuotedAndBareArguments()
    {
        (Dictionary<string, string> named, string free) =
            PromptTemplateRegistry.ParseArguments("focus=\"error handling\" depth=deep review the parser");

        Assert.AreEqual("error handling", named["focus"]);
        Assert.AreEqual("deep", named["depth"]);
        Assert.AreEqual("review the parser", free);
    }
}
