using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using WinHarness;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Mcp;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Tools;

const string Version = "0.1.0";

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Configuration.AddWinHarnessConfiguration();
hostBuilder.Services.AddWinHarnessOptions(hostBuilder.Configuration);
hostBuilder.Services
    .AddWinHarnessPlatform()
    .AddWinHarnessCore()
    .AddWinHarnessProviders()
    .AddWinHarnessMcp()
    .AddWinHarnessTools();

using IHost host = hostBuilder.Build();
host.Services.GetRequiredService<IAnsiConsoleConfigurator>().EnableAnsi();

var app = ConsoleApp.Create();

app.Add("", () =>
{
    AnsiConsole.MarkupLine("[bold]WinHarness[/] v" + Version);
    AnsiConsole.MarkupLine("OpenAI-compatible, Windows-first AI coding harness.");
});

app.Add("version", () =>
{
    Console.WriteLine(Version);
});

app.Add("diagnostics aot", () =>
{
    Table table = new Table()
        .Title("WinHarness Diagnostics")
        .AddColumn("Check")
        .AddColumn("Status");

    table.AddRow(".NET", "10");
    table.AddRow("Native AOT", "[green]configured[/]");
    table.AddRow("Provider scope", "OpenAI-compatible");
    table.AddRow("Configuration", Markup.Escape(WinHarnessConfiguration.GetConfigurationDirectory()));

    AnsiConsole.Write(table);
});

app.Add("tools list", async (CancellationToken cancellationToken) =>
{
    IEnumerable<IToolProvider> providers = host.Services.GetServices<IToolProvider>();
    foreach (IToolProvider provider in providers)
    {
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        foreach (ITool tool in tools)
        {
            Console.WriteLine($"{tool.Name}\t{tool.Description}");
        }
    }
});

app.Add("providers list", () =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    foreach (ProviderOptions provider in options.Providers)
    {
        Console.WriteLine($"{provider.Id}\t{provider.Kind}\t{provider.BaseUrl}");
    }
});

app.Add("models list", (string providerId) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));

    if (provider is null)
    {
        throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

    foreach (ModelOptions model in provider.Models)
    {
        Console.WriteLine($"{model.Id}\t{model.ProviderModelId}");
    }
});

app.Add("credentials set", async (string targetName, string secret, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.SetSecretAsync(targetName, secret, cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Credential stored.");
});

app.Add("credentials delete", async (string targetName, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.DeleteSecretAsync(targetName, cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Credential deleted.");
});

await app.RunAsync(args).ConfigureAwait(false);
