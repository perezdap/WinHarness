using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Tools;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ToolRegistryTests
{
    [TestMethod]
    public async Task RegistryRejectsDuplicateToolNames()
    {
        ITool duplicate = new FakeTool("dup");
        ToolRegistry registry = new([
            new FakeProvider([duplicate]),
            new FakeProvider([duplicate])
        ]);

        InvalidOperationException? exception = null;
        try
        {
            _ = await registry.ListToolsAsync(CancellationToken.None);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "Duplicate tool name");
    }

    [TestMethod]
    public async Task AiFunctionAdapterInvokesTool()
    {
        ToolAIFunctionAdapter adapter = new(new FakeTool("echo"));
        AIFunctionArguments arguments = new()
        {
            ["message"] = "hello"
        };

        object? result = await adapter.InvokeAsync(arguments, CancellationToken.None);

        Assert.AreEqual("hello", result);
    }

    private sealed class FakeProvider : IToolProvider
    {
        private readonly IReadOnlyList<ITool> _tools;

        public FakeProvider(IReadOnlyList<ITool> tools)
        {
            _tools = tools;
        }

        public ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(_tools);
        }
    }

    private sealed class FakeTool : ITool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public FakeTool(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Description => "fake";

        public JsonElement InputSchema => Schema;

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string message = invocation.Arguments.GetProperty("message").GetString() ?? string.Empty;
            return ValueTask.FromResult(new ToolResult(true, message));
        }
    }
}
