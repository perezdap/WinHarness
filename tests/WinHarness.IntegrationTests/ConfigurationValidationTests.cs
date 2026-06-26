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

        InvalidOperationException? exception = null;
        try
        {
            WinHarnessOptionsValidator.Validate(options);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "openai-compatible");
    }
}
