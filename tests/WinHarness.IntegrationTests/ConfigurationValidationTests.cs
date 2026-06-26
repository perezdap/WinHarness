using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ConfigurationValidationTests
{
    [TestMethod]
    public void RejectsUnsupportedProviderKind()
    {
        WinHarnessOptions options = new();
        options.Providers.Add(new ProviderOptions
        {
            Id = "anthropic-main",
            Kind = "anthropic"
        });

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            WinHarnessOptionsValidator.Validate(options));

        StringAssert.Contains(exception.Message, "openai-compatible");
    }
}
