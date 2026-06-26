using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [TestMethod]
    public void RejectsNonWinHarnessCredentialName()
    {
        WinHarnessOptions options = new();
        options.Providers.Add(new ProviderOptions
        {
            Id = "local",
            Kind = "openai-compatible",
            CredentialName = "OtherApp:token"
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
        StringAssert.Contains(exception!.Message, "WinHarness:");
    }

    [TestMethod]
    public void RejectsInlineSecretConfigurationKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:0:id"] = "openai-main",
                ["providers:0:kind"] = "openai-compatible",
                ["providers:0:apiKey"] = "sk-test"
            })
            .Build();
        ServiceCollection services = new();

        InvalidOperationException? exception = null;
        try
        {
            services.AddWinHarnessOptions(configuration);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "Windows Credential Manager");
    }

    [TestMethod]
    public void AllowsCredentialNameSecretReference()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:0:id"] = "openai-main",
                ["providers:0:kind"] = "openai-compatible",
                ["providers:0:credentialName"] = "WinHarness:openai-main"
            })
            .Build();
        ServiceCollection services = new();

        services.AddWinHarnessOptions(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        WinHarnessOptions options = provider.GetRequiredService<WinHarnessOptions>();
        Assert.AreEqual("WinHarness:openai-main", options.Providers[0].CredentialName);
    }
}
