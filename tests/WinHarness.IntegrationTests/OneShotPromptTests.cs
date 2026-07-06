using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class OneShotPromptTests
{
    private string _root = null!;
    private string _savedCwd = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinHarnessOneShot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _savedCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _root;
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.CurrentDirectory = _savedCwd;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FilesFlagAttachesContents()
    {
        File.WriteAllText(Path.Combine(_root, "code.cs"), "class Code;");

        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "review this", ["code.cs"], CancellationToken.None);

        StringAssert.Contains(prompt, "review this");
        StringAssert.Contains(prompt, "```code.cs");
        StringAssert.Contains(prompt, "class Code;");
    }

    [TestMethod]
    public async Task MissingFilesFlagPathThrows()
    {
        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await ChatRepl.AssembleOneShotPromptAsync(
                "review", ["absent.cs"], CancellationToken.None));

        StringAssert.Contains(exception.Message, "absent.cs");
    }

    [TestMethod]
    public async Task InlineAtTokensExpandInOneShotPrompts()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "note body");

        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "see @notes.md", files: null, CancellationToken.None);

        StringAssert.Contains(prompt, "note body");
    }

    [TestMethod]
    public async Task PromptWithoutFilesOrTokensPassesThrough()
    {
        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "plain prompt", files: null, CancellationToken.None);

        Assert.AreEqual("plain prompt", prompt);
    }

    [TestMethod]
    public async Task InjectedStdinIsPrependedAsFencedBlock()
    {
        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "summarize this",
            files: null,
            CancellationToken.None,
            stdin: new StringReader("piped body\nsecond line"),
            isInputRedirected: true);

        StringAssert.Contains(prompt, "```stdin");
        StringAssert.Contains(prompt, "piped body");
        StringAssert.Contains(prompt, "second line");
        // Original prompt preserved after the stdin block.
        Assert.IsTrue(prompt.EndsWith("summarize this", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmptyInjectedStdinProducesNoStdinBlock()
    {
        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "just a prompt",
            files: null,
            CancellationToken.None,
            stdin: new StringReader("   \n  "),
            isInputRedirected: true);

        Assert.AreEqual("just a prompt", prompt);
    }

    [TestMethod]
    public async Task NonRedirectedStdinIsNotRead()
    {
        // isInputRedirected: false must skip the stdin path entirely, even when
        // a reader is provided. Guards against accidentally consuming a reader
        // the caller did not intend to pipe.
        string prompt = await ChatRepl.AssembleOneShotPromptAsync(
            "only prompt",
            files: null,
            CancellationToken.None,
            stdin: new StringReader("should be ignored"),
            isInputRedirected: false);

        Assert.AreEqual("only prompt", prompt);
    }
}
