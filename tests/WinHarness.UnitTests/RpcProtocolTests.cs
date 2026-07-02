using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Runtime;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class RpcProtocolTests
{
    [TestMethod]
    public void RequestRoundTrips()
    {
        string json = """{"id":"42","method":"prompt","prompt":"hello","providerId":"p","modelId":"m"}""";

        RpcRequest request = JsonSerializer.Deserialize(json, RpcJsonContext.Default.RpcRequest)!;

        Assert.AreEqual("42", request.Id);
        Assert.AreEqual("prompt", request.Method);
        Assert.AreEqual("hello", request.Prompt);
        Assert.AreEqual("p", request.ProviderId);
    }

    [TestMethod]
    public void SuccessResponseOmitsNullFields()
    {
        string json = JsonSerializer.Serialize(RpcResponse.Success("1"), RpcJsonContext.Default.RpcResponse);

        StringAssert.Contains(json, "\"kind\":\"response\"");
        StringAssert.Contains(json, "\"ok\":true");
        Assert.IsFalse(json.Contains("error", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("result", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailureResponseCarriesError()
    {
        string json = JsonSerializer.Serialize(RpcResponse.Failure("1", "boom"), RpcJsonContext.Default.RpcResponse);

        StringAssert.Contains(json, "\"ok\":false");
        StringAssert.Contains(json, "\"error\":\"boom\"");
    }

    [TestMethod]
    public void EventWrapsChatEventWithRequestId()
    {
        string json = JsonSerializer.Serialize(
            RpcEvent.For("p1", JsonChatEvent.AssistantDelta("hi")),
            RpcJsonContext.Default.RpcEvent);

        StringAssert.Contains(json, "\"kind\":\"event\"");
        StringAssert.Contains(json, "\"requestId\":\"p1\"");
        StringAssert.Contains(json, "\"type\":\"assistant_delta\"");
        Assert.IsFalse(json.TrimEnd().Contains('\n'), "RPC records must be single-line");
    }
}
