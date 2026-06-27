using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Context;
using WinHarness.Infrastructure.Context;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ContextFileLoaderTests
{
    private string _tempRoot = null!;
    private string _globalConfigDirectory = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "WinHarnessContext", Guid.NewGuid().ToString("N"));
        _globalConfigDirectory = Path.Combine(_tempRoot, "global-config");
        Directory.CreateDirectory(_globalConfigDirectory);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void ConcatenatesAgentsFilesFromDriveRootDownToWorkspace()
    {
        string workspaceRoot = Path.Combine(_tempRoot, "alpha", "beta", "gamma");
        Directory.CreateDirectory(workspaceRoot);

        Write(Path.Combine(_globalConfigDirectory, "AGENTS.md"), "global-agents");
        Write(Path.Combine(_tempRoot, "alpha", "AGENTS.md"), "alpha-agents");
        Write(Path.Combine(_tempRoot, "alpha", "beta", "CLAUDE.md"), "beta-claude");
        Write(Path.Combine(workspaceRoot, "AGENTS.md"), "gamma-agents");

        ContextFileLoader loader = new(_globalConfigDirectory);
        ProjectContext context = loader.Load(workspaceRoot);

        StringAssert.Contains(context.AgentsInstructions, "global-agents");
        StringAssert.Contains(context.AgentsInstructions, "alpha-agents");
        StringAssert.Contains(context.AgentsInstructions, "beta-claude");
        StringAssert.Contains(context.AgentsInstructions, "gamma-agents");
        Assert.IsTrue(context.AgentsInstructions.IndexOf("global-agents", StringComparison.Ordinal) <
                      context.AgentsInstructions.IndexOf("alpha-agents", StringComparison.Ordinal));
        Assert.IsTrue(context.AgentsInstructions.IndexOf("alpha-agents", StringComparison.Ordinal) <
                      context.AgentsInstructions.IndexOf("beta-claude", StringComparison.Ordinal));
        Assert.IsTrue(context.AgentsInstructions.IndexOf("beta-claude", StringComparison.Ordinal) <
                      context.AgentsInstructions.IndexOf("gamma-agents", StringComparison.Ordinal));
        StringAssert.Contains(context.AgentsInstructions, "\n\n---\n\n");
    }

    [TestMethod]
    public void ProjectSystemFileOverridesGlobalSystemFile()
    {
        string workspaceRoot = Path.Combine(_tempRoot, "project");
        string winHarnessDirectory = Path.Combine(workspaceRoot, ".winharness");
        Directory.CreateDirectory(winHarnessDirectory);

        Write(Path.Combine(_globalConfigDirectory, "SYSTEM.md"), "global-system");
        Write(Path.Combine(winHarnessDirectory, "SYSTEM.md"), "project-system");

        ContextFileLoader loader = new(_globalConfigDirectory);
        ProjectContext context = loader.Load(workspaceRoot);

        Assert.AreEqual("project-system", context.SystemPromptReplacement);
    }

    [TestMethod]
    public void ProjectAppendSystemFileOverridesGlobalAppendSystemFile()
    {
        string workspaceRoot = Path.Combine(_tempRoot, "project");
        string winHarnessDirectory = Path.Combine(workspaceRoot, ".winharness");
        Directory.CreateDirectory(winHarnessDirectory);

        Write(Path.Combine(_globalConfigDirectory, "APPEND_SYSTEM.md"), "global-append");
        Write(Path.Combine(winHarnessDirectory, "APPEND_SYSTEM.md"), "project-append");

        ContextFileLoader loader = new(_globalConfigDirectory);
        ProjectContext context = loader.Load(workspaceRoot);

        Assert.AreEqual("project-append", context.SystemPromptAppend);
    }

    [TestMethod]
    public void MissingFilesReturnEmptyProjectContextFields()
    {
        string workspaceRoot = Path.Combine(_tempRoot, "empty-project");
        Directory.CreateDirectory(workspaceRoot);

        ContextFileLoader loader = new(_globalConfigDirectory);
        ProjectContext context = loader.Load(workspaceRoot);

        Assert.IsNull(context.SystemPromptReplacement);
        Assert.IsNull(context.SystemPromptAppend);
        Assert.AreEqual(string.Empty, context.AgentsInstructions);
    }

    private static void Write(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }
}