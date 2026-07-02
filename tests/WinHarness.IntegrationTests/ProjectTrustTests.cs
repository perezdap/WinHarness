using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Infrastructure.Context;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ProjectTrustTests
{
    private string _root = null!;
    private string _configDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "WinHarnessTrust", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "workspace");
        _configDir = Path.Combine(baseDir, "config");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_configDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        string baseDir = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [TestMethod]
    public void UndecidedWorkspaceReturnsNull()
    {
        TrustStore store = new(_configDir);

        Assert.IsNull(store.GetDecision(_root));
    }

    [TestMethod]
    public void SavedDecisionRoundTrips()
    {
        TrustStore store = new(_configDir);
        store.SaveDecision(_root, trusted: true);

        Assert.AreEqual(true, new TrustStore(_configDir).GetDecision(_root));
    }

    [TestMethod]
    public void AncestorDecisionCoversChildFolders()
    {
        TrustStore store = new(_configDir);
        store.SaveDecision(_root, trusted: false);
        string child = Path.Combine(_root, "src", "deep");
        Directory.CreateDirectory(child);

        Assert.AreEqual(false, store.GetDecision(child));
    }

    [TestMethod]
    public void DetectsProjectLocalResources()
    {
        Assert.IsFalse(TrustStore.HasProjectLocalResources(_root));

        Directory.CreateDirectory(Path.Combine(_root, ".winharness"));
        Assert.IsTrue(TrustStore.HasProjectLocalResources(_root));
    }

    [TestMethod]
    public void DetectsAgentsSkillsAsProjectLocal()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agents", "skills"));

        Assert.IsTrue(TrustStore.HasProjectLocalResources(_root));
    }

    [TestMethod]
    public void UntrustedLoadSkipsProjectSystemPrompts()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".winharness"));
        File.WriteAllText(Path.Combine(_root, ".winharness", "SYSTEM.md"), "project system");
        File.WriteAllText(Path.Combine(_root, ".winharness", "APPEND_SYSTEM.md"), "project append");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "agents info");
        ContextFileLoader loader = new(_configDir);

        var trusted = loader.Load(_root, includeProjectLocal: true);
        var untrusted = loader.Load(_root, includeProjectLocal: false);

        Assert.AreEqual("project system", trusted.SystemPromptReplacement);
        Assert.AreEqual("project append", trusted.SystemPromptAppend);
        Assert.IsNull(untrusted.SystemPromptReplacement);
        Assert.IsNull(untrusted.SystemPromptAppend);
        StringAssert.Contains(untrusted.AgentsInstructions, "agents info");
    }

    [TestMethod]
    public void UntrustedDiscoverySkipsProjectSkills()
    {
        string skillDir = Path.Combine(_root, ".winharness", "skills", "evil");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# evil\ninstructions");

        var trusted = SkillRegistry.Discover(_root, includeProjectLocal: true);
        var untrusted = SkillRegistry.Discover(_root, includeProjectLocal: false);

        Assert.IsTrue(trusted.Any(skill => skill.Name.Contains("evil", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(untrusted.Any(skill => skill.Name.Contains("evil", StringComparison.OrdinalIgnoreCase)));
    }
}
