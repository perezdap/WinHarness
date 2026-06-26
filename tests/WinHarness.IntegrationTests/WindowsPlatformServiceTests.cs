using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Platform;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class WindowsPlatformServiceTests
{
    [TestMethod]
    public void AnsiConfiguratorDoesNotThrow()
    {
        WindowsAnsiConsoleConfigurator configurator = new();

        configurator.EnableAnsi();
    }

    [TestMethod]
    public void LongPathServiceNormalizesLocalPaths()
    {
        WindowsLongPathService service = new();

        string normalized = service.Normalize(Path.Combine(Environment.CurrentDirectory, "sample.txt"));

        if (OperatingSystem.IsWindows())
        {
            StringAssert.StartsWith(normalized, @"\\?\");
        }
        else
        {
            Assert.AreEqual(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "sample.txt")), normalized);
        }
    }

    [TestMethod]
    public void LongPathServicePreservesExtendedPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Extended-length path normalization is Windows-specific.");
        }

        WindowsLongPathService service = new();

        string normalized = service.Normalize(@"\\?\C:\WinHarness\sample.txt");

        Assert.AreEqual(@"\\?\C:\WinHarness\sample.txt", normalized);
    }
}
