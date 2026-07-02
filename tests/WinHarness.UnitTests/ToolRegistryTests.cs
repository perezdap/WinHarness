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
    public async Task RegistryRejectsToolsThatSanitizeToSameModelFacingName()
    {
        // 'a.b' and 'a_b' differ raw but both collapse to the sanitized name
        // 'a_b'; the registry must reject them up front, matching the runtime.
        ToolRegistry registry = new([
            new FakeProvider([new FakeTool("a.b")]),
            new FakeProvider([new FakeTool("a_b")])
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

    [TestMethod]
    public void AdapterSanitizesModelFacingToolName()
    {
        // Anthropic/Bedrock rejects dots in tool names; MCP tools are often named
        // server.tool, so the model-facing function name must be provider-safe.
        ToolAIFunctionAdapter adapter = new(new FakeTool("concierge.context7__query-docs"));

        Assert.AreEqual("concierge_context7__query-docs", adapter.Name);
    }

    [TestMethod]
    public void AdapterReplacesLeadingInvalidCharacterWithUnderscore()
    {
        // A leading dot becomes an underscore, which is a valid leading character,
        // so no extra prefix is needed.
        ToolAIFunctionAdapter adapter = new(new FakeTool(".hidden"));

        Assert.AreEqual("_hidden", adapter.Name);
    }

    [TestMethod]
    public void AdapterAddsTypeToUntypedSchemaProperty()
    {
        // Some MCP tools (e.g. GitHub issue_write) expose a property with only a
        // description and no type. Gemini's OpenAI-compatible bridge rejects this,
        // sometimes with an empty 200 stream, so the adapter must default it to string.
        ITool tool = new FakeTool(
            "untyped",
            """{"type":"object","properties":{"value":{"description":"anything"}}}""");

        ToolAIFunctionAdapter adapter = new(tool);

        JsonElement value = adapter.JsonSchema
            .GetProperty("properties")
            .GetProperty("value");
        Assert.IsTrue(value.TryGetProperty("type", out JsonElement type));
        Assert.AreEqual("string", type.GetString());
    }

    [TestMethod]
    public void AdapterDropsNonStringEnum()
    {
        // Gemini rejects boolean/number enums such as `enum: [true]`.
        ITool tool = new FakeTool(
            "boolenum",
            """{"type":"object","properties":{"delete":{"type":"boolean","enum":[true]}}}""");

        ToolAIFunctionAdapter adapter = new(tool);

        JsonElement delete = adapter.JsonSchema
            .GetProperty("properties")
            .GetProperty("delete");
        Assert.IsFalse(delete.TryGetProperty("enum", out _));
        Assert.AreEqual("boolean", delete.GetProperty("type").GetString());
    }

    [TestMethod]
    public void AdapterPreservesStringEnum()
    {
        ITool tool = new FakeTool(
            "strenum",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["a","b"]}}}""");

        ToolAIFunctionAdapter adapter = new(tool);

        JsonElement mode = adapter.JsonSchema
            .GetProperty("properties")
            .GetProperty("mode");
        Assert.IsTrue(mode.TryGetProperty("enum", out JsonElement enumValue));
        Assert.AreEqual(2, enumValue.GetArrayLength());
    }

    [TestMethod]
    public void AdapterSanitizesNestedArrayItemProperties()
    {
        // Mirrors github__issue_write: an untyped `value` nested inside array items.
        ITool tool = new FakeTool(
            "nested",
            """{"type":"object","properties":{"fields":{"type":"array","items":{"type":"object","properties":{"value":{"description":"anything"},"delete":{"type":"boolean","enum":[true]}}}}}}""");

        ToolAIFunctionAdapter adapter = new(tool);

        JsonElement itemProps = adapter.JsonSchema
            .GetProperty("properties")
            .GetProperty("fields")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.AreEqual("string", itemProps.GetProperty("value").GetProperty("type").GetString());
        Assert.IsFalse(itemProps.GetProperty("delete").TryGetProperty("enum", out _));
    }

    [TestMethod]
    public async Task BuiltinToolsExposeValidSchemas()
    {
        BuiltinToolProvider provider = new(Path.GetTempPath());

        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "read_file", "write_file", "edit_file", "run_command", "glob", "grep" },
            tools.Select(static tool => tool.Name).ToArray());
        foreach (ITool tool in tools)
        {
            Assert.AreEqual(JsonValueKind.Object, tool.InputSchema.ValueKind, tool.Name);
            Assert.IsTrue(tool.InputSchema.TryGetProperty("type", out JsonElement type), tool.Name);
            Assert.AreEqual("object", type.GetString(), tool.Name);
        }
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
        private readonly JsonElement _schema;

        public FakeTool(string name)
            : this(name, """{"type":"object"}""")
        {
        }

        public FakeTool(string name, string schema)
        {
            Name = name;
            using JsonDocument document = JsonDocument.Parse(schema);
            _schema = document.RootElement.Clone();
        }

        public string Name { get; }

        public string Description => "fake";

        public JsonElement InputSchema => _schema;

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string message = invocation.Arguments.GetProperty("message").GetString() ?? string.Empty;
            return ValueTask.FromResult(new ToolResult(true, message));
        }
    }
}
