using Microsoft.Extensions.Logging;

namespace WinHarness.Runtime;

internal static partial class LoggerExtensions
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Provider request starting for {ProviderId}/{ModelId}.")]
    public static partial void ProviderRequestStarting(this ILogger logger, string providerId, string modelId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Provider request completed for {ProviderId}/{ModelId}.")]
    public static partial void ProviderRequestCompleted(this ILogger logger, string providerId, string modelId);
}
