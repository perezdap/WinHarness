using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class EditorInputTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinHarnessEditorInput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ClassifiesInputKinds()
    {
        Assert.AreEqual(EditorInputKind.Prompt, EditorInput.Classify("hello"));
        Assert.AreEqual(EditorInputKind.MultiLineStart, EditorInput.Classify("\"\"\""));
        Assert.AreEqual(EditorInputKind.MultiLineStart, EditorInput.Classify("\"\"\"first line"));
        Assert.AreEqual(EditorInputKind.CommandToModel, EditorInput.Classify("!git status"));
        Assert.AreEqual(EditorInputKind.CommandLocal, EditorInput.Classify("!!git status"));
    }

    [TestMethod]
    public void StripsCommandPrefixes()
    {
        Assert.AreEqual("git status", EditorInput.StripCommandPrefix("!git status"));
        Assert.AreEqual("git status", EditorInput.StripCommandPrefix("!!git status"));
        Assert.AreEqual("git status", EditorInput.StripCommandPrefix("! git status"));
    }

    [TestMethod]
    public void FormatsCommandOutputWithStderrAndExitCode()
    {
        string formatted = EditorInput.FormatCommandOutputForModel("build", "ok", "warn", 1);

        StringAssert.Contains(formatted, "`build` (exit code 1)");
        StringAssert.Contains(formatted, "ok");
        StringAssert.Contains(formatted, "--- stderr ---");
        StringAssert.Contains(formatted, "warn");
    }

    [TestMethod]
    public void ExpandsExistingFileReference()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "file body");

        (string prompt, IReadOnlyList<string> attached) =
            EditorInput.ExpandFileReferences("review @notes.md please", _root);

        CollectionAssert.AreEqual(new[] { "notes.md" }, attached.ToArray());
        StringAssert.Contains(prompt, "review notes.md please");
        StringAssert.Contains(prompt, "```notes.md");
        StringAssert.Contains(prompt, "file body");
    }

    [TestMethod]
    public void LeavesMissingFilesAndEmailsAlone()
    {
        (string prompt, IReadOnlyList<string> attached) =
            EditorInput.ExpandFileReferences("email user@example.com about @missing.txt", _root);

        Assert.AreEqual(0, attached.Count);
        Assert.AreEqual("email user@example.com about @missing.txt", prompt);
    }

    [TestMethod]
    public void SkipsFilesOverSizeLimit()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', 512));

        (string prompt, IReadOnlyList<string> attached) =
            EditorInput.ExpandFileReferences("see @big.txt", _root, maxFileBytes: 100);

        Assert.AreEqual(0, attached.Count);
        StringAssert.Contains(prompt, "skipped");
        StringAssert.Contains(prompt, "byte limit");
    }

    [TestMethod]
    public void ExpandsMultipleReferences()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class A;");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "class B;");

        (string prompt, IReadOnlyList<string> attached) =
            EditorInput.ExpandFileReferences("compare @a.cs and @b.cs", _root);

        CollectionAssert.AreEqual(new[] { "a.cs", "b.cs" }, attached.ToArray());
        StringAssert.Contains(prompt, "class A;");
        StringAssert.Contains(prompt, "class B;");
    }
}
