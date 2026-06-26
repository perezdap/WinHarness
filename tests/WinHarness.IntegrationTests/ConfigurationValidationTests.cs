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

    [TestMethod]
    public void RejectsMissingDefaultModel()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "missing"
        };
        options.Providers.Add(new ProviderOptions
        {
            Id = "local",
            Kind = "openai-compatible"
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
        StringAssert.Contains(exception!.Message, "Default model");
    }

    [TestMethod]
    public void RejectsDuplicateMcpServers()
    {
        WinHarnessOptions options = new();
        options.McpServers.Add(new McpServerOptions { Id = "server", Command = "server.exe" });
        options.McpServers.Add(new McpServerOptions { Id = "server", Command = "server.exe" });

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
        StringAssert.Contains(exception!.Message, "Duplicate MCP");
    }
}
