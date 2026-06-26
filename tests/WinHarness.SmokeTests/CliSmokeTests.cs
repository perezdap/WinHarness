using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.SmokeTests;

[TestClass]
public sealed class CliSmokeTests
{
    [TestMethod]
    public void ConfigurationDirectoryUsesWinHarnessFolder()
    {
        string directory = WinHarnessConfiguration.GetConfigurationDirectory();

        StringAssert.EndsWith(directory, "WinHarness");
    }
}
