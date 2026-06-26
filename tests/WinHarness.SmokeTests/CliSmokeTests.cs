using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinHarness.SmokeTests;

[TestClass]
public sealed class CliSmokeTests
{
    [TestMethod]
    public void SmokeProjectLoads()
    {
        Assert.IsTrue(true);
    }
}
