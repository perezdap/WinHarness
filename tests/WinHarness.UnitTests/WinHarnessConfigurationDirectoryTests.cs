using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class WinHarnessConfigurationDirectoryTests
{
    private string? _saved;

    [TestInitialize]
    public void SetUp()
    {
        _saved = Environment.GetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable);
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable, _saved);
    }

    [TestMethod]
    public void OverrideRedirectsConfigurationDirectory()
    {
        string overrideDir = Path.Combine(Path.GetTempPath(), "WinHarnessConfigOverride", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable, overrideDir);

        string resolved = WinHarnessConfiguration.GetConfigurationDirectory();

        Assert.AreEqual(Path.GetFullPath(overrideDir), resolved);
    }

    [TestMethod]
    public void RelativeOverrideIsResolvedToFullPath()
    {
        Environment.SetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable, "relative-config-dir");

        string resolved = WinHarnessConfiguration.GetConfigurationDirectory();

        Assert.IsTrue(Path.IsPathRooted(resolved));
        StringAssert.EndsWith(resolved, "relative-config-dir");
    }

    [TestMethod]
    public void EmptyOverrideFallsBackToAppData()
    {
        Environment.SetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable, "  ");

        string resolved = WinHarnessConfiguration.GetConfigurationDirectory();

        StringAssert.EndsWith(resolved, "WinHarness");
    }

    [TestMethod]
    public void UnsetOverrideFallsBackToAppData()
    {
        Environment.SetEnvironmentVariable(WinHarnessConfiguration.ConfigDirEnvironmentVariable, null);

        string resolved = WinHarnessConfiguration.GetConfigurationDirectory();

        StringAssert.EndsWith(resolved, "WinHarness");
    }
}
