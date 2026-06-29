using WinHarness.Providers;

namespace WinHarness.Configuration;

/// <summary>
/// Root WinHarness configuration.
/// </summary>
public sealed class WinHarnessOptions
{
    /// <summary>
    /// Gets or sets the default provider id.
    /// </summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default model id.
    /// </summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets configured provider endpoints.
    /// </summary>
    public List<ProviderOptions> Providers { get; set; } = [];

    /// <summary>
    /// Gets or sets configured MCP servers.
    /// </summary>
    public List<McpServerOptions> McpServers { get; set; } = [];
}

/// <summary>
/// Provider endpoint configuration.
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// Gets or sets the stable provider id used by WinHarness.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider kind. v0.1 supports "openai-compatible".
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenAI-compatible base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Windows Credential Manager target name.
    /// </summary>
    public string? CredentialName { get; set; }

    /// <summary>
    /// Gets or sets configured models for this provider.
    /// </summary>
    public List<ModelOptions> Models { get; set; } = [];
}

/// <summary>
/// Model configuration.
/// </summary>
public sealed class ModelOptions
{
    /// <summary>
    /// Gets or sets the WinHarness model alias.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider's model id.
    /// </summary>
    public string ProviderModelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets model-specific capabilities.
    /// </summary>
    public ProviderCapabilities Capabilities { get; set; } = ProviderCapabilities.None;

    /// <summary>
    /// Gets or sets the context window limit (max tokens) for this model.
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// Gets or sets the reasoning effort levels supported by this model (e.g. "low", "medium", "high").
    /// </summary>
    public System.Collections.Generic.List<string>? SupportedReasoningEfforts { get; set; }
}

/// <summary>
/// MCP server configuration.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>
    /// Gets or sets the MCP server id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MCP transport kind: "stdio", "http", or "sse".
    /// </summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>
    /// Gets or sets the command to execute for stdio transports.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets command arguments for stdio transports.
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// Gets or sets the optional working directory for stdio transports.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets environment overrides for stdio transports.
    /// </summary>
    public Dictionary<string, string?> Environment { get; set; } = [];

    /// <summary>
    /// Gets or sets the HTTP/SSE MCP endpoint.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets custom HTTP headers for HTTP/SSE MCP transports.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// Gets or sets whether this server is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the startup timeout in seconds.
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 30;
}
