using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure.Diagnostics;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class JsonlDiagnosticSinkTests
{
    [TestMethod]
    public async Task WritesJsonlRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinHarnessDiagnostics", Guid.NewGuid().ToString("N"));
        JsonlDiagnosticSink sink = new(directory);

        await sink.WriteAsync(
            new DiagnosticRecord(
                DateTimeOffset.UnixEpoch,
                "test",
                "event",
                "message",
                new Dictionary<string, string> { ["key"] = "value" }),
            CancellationToken.None);

        string content = await File.ReadAllTextAsync(Path.Combine(directory, "winharness.jsonl"));

        StringAssert.Contains(content, "\"category\":\"test\"");
        StringAssert.Contains(content, "\"eventName\":\"event\"");
    }
}
