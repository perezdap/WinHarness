using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ProviderCapabilitiesTests
{
    [TestMethod]
    public void NoneDisablesAllCapabilities()
    {
        ProviderCapabilities capabilities = ProviderCapabilities.None;

        Assert.IsFalse(capabilities.Streaming);
        Assert.IsFalse(capabilities.ToolCalling);
        Assert.IsFalse(capabilities.Vision);
        Assert.IsFalse(capabilities.PromptCaching);
        Assert.IsFalse(capabilities.StructuredOutput);
        Assert.IsFalse(capabilities.Reasoning);
    }
}
