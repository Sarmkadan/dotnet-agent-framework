using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class ModelsGuardTests
{
    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { type = "object" });

    [Fact]
    public void ChatMessageSystem_NullContent_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.System(null!));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public void ChatMessageUser_NullContent_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.User(null!));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public void ChatMessageAssistant_NullContent_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.Assistant(null!));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public void ChatMessageToolResult_NullToolCallId_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.ToolResult(null!, "ok"));

        Assert.Equal("toolCallId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatMessageToolResult_BlankToolCallId_ThrowsArgumentException(string toolCallId)
    {
        var ex = Assert.Throws<ArgumentException>(() => ChatMessage.ToolResult(toolCallId, "ok"));

        Assert.Equal("toolCallId", ex.ParamName);
    }

    [Fact]
    public void ChatMessageToolResult_NullContent_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.ToolResult("call-1", null!));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public void ChatMessageFactories_ValidInput_CreateExpectedRole()
    {
        Assert.Equal(MessageRole.System, ChatMessage.System("s").Role);
        Assert.Equal(MessageRole.User, ChatMessage.User("u").Role);
        Assert.Equal(MessageRole.Assistant, ChatMessage.Assistant("a").Role);

        var tool = ChatMessage.ToolResult("call-1", "result");
        Assert.Equal(MessageRole.Tool, tool.Role);
        Assert.Equal("call-1", tool.ToolCallId);
    }

    [Fact]
    public void ChatMessageValidate_WellFormedMessages_DoesNotThrow()
    {
        var call = ToolCall.Create("call-1", "echo", EmptyObject());

        ChatMessage.Validate(ChatMessage.System("s"));
        ChatMessage.Validate(ChatMessage.User("u"));
        ChatMessage.Validate(ChatMessage.Assistant("", [call]));
        ChatMessage.Validate(ChatMessage.ToolResult("call-1", "ok"));
        ChatMessage.Validate(new ChatMessage(MessageRole.User, "u", []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatMessageValidate_ToolMessageWithBlankToolCallId_ThrowsArgumentException(string? toolCallId)
    {
        var message = new ChatMessage(MessageRole.Tool, "ok", ToolCallId: toolCallId);

        var ex = Assert.Throws<ArgumentException>(() => ChatMessage.Validate(message));

        Assert.Equal("message", ex.ParamName);
        Assert.Contains("Tool messages must have a non-whitespace ToolCallId.", ex.Message);
    }

    [Theory]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.User)]
    [InlineData(MessageRole.Assistant)]
    public void ChatMessageValidate_NonToolMessageWithToolCallId_ThrowsArgumentException(MessageRole role)
    {
        var message = new ChatMessage(role, "x", ToolCallId: "call-1");

        var ex = Assert.Throws<ArgumentException>(() => ChatMessage.Validate(message));

        Assert.Equal("message", ex.ParamName);
        Assert.Contains($"Only Tool messages may set ToolCallId, but a {role} message did.", ex.Message);
    }

    [Fact]
    public void ChatMessageValidate_NonToolMessageWithEmptyToolCallId_ThrowsArgumentException()
    {
        // Rule is "non-null", so an empty string still counts as set.
        var message = new ChatMessage(MessageRole.User, "x", ToolCallId: "");

        Assert.Throws<ArgumentException>(() => ChatMessage.Validate(message));
    }

    [Theory]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.User)]
    [InlineData(MessageRole.Tool)]
    public void ChatMessageValidate_NonAssistantWithToolCalls_ThrowsArgumentException(MessageRole role)
    {
        var call = ToolCall.Create("call-1", "echo", EmptyObject());
        var message = role == MessageRole.Tool
            ? new ChatMessage(role, "x", [call], ToolCallId: "call-1")
            : new ChatMessage(role, "x", [call]);

        var ex = Assert.Throws<ArgumentException>(() => ChatMessage.Validate(message));

        Assert.Equal("message", ex.ParamName);
        Assert.Contains($"Only Assistant messages may carry ToolCalls, but a {role} message did.", ex.Message);
    }

    [Fact]
    public void ChatMessageValidate_NullMessage_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ChatMessage.Validate(null!));

        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public void ToolCallCreate_ValidInput_ReturnsCall()
    {
        var call = ToolCall.Create("call-1", "echo", EmptyObject());

        Assert.Equal("call-1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
    }

    [Fact]
    public void ToolCallCreate_NullId_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ToolCall.Create(null!, "echo", EmptyObject()));

        Assert.Equal("id", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ToolCallCreate_BlankId_ThrowsArgumentException(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => ToolCall.Create(id, "echo", EmptyObject()));

        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void ToolCallCreate_NullName_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ToolCall.Create("call-1", null!, EmptyObject()));

        Assert.Equal("name", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    public void ToolCallCreate_BlankName_ThrowsArgumentException(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => ToolCall.Create("call-1", name, EmptyObject()));

        Assert.Equal("name", ex.ParamName);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void ToolCallCreate_NonObjectArguments_ThrowsArgumentException(string json)
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(json);

        var ex = Assert.Throws<ArgumentException>(() => ToolCall.Create("call-1", "echo", arguments));

        Assert.Equal("arguments", ex.ParamName);
    }

    [Fact]
    public void ToolCallCreate_UndefinedArguments_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ToolCall.Create("call-1", "echo", default));

        Assert.Equal("arguments", ex.ParamName);
    }

    [Fact]
    public void ToolDefinitionCreate_ValidInput_ReturnsDefinition()
    {
        var definition = ToolDefinition.Create("echo", "Echoes text.", EmptyObject());

        Assert.Equal("echo", definition.Name);
        Assert.Equal("Echoes text.", definition.Description);
        Assert.Equal(JsonValueKind.Object, definition.ParametersSchema.ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToolDefinitionCreate_BlankName_ThrowsArgumentException(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => ToolDefinition.Create(name, "d", EmptyObject()));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void ToolDefinitionCreate_NullDescription_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ToolDefinition.Create("echo", null!, EmptyObject()));

        Assert.Equal("description", ex.ParamName);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"object\"")]
    public void ToolDefinitionCreate_NonObjectSchema_ThrowsArgumentException(string json)
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(json);

        var ex = Assert.Throws<ArgumentException>(() => ToolDefinition.Create("echo", "d", schema));

        Assert.Equal("schema", ex.ParamName);
    }

    [Fact]
    public async Task ToolInvokeAsync_NonObjectArguments_ReturnsErrorTextWithoutInvoking()
    {
        var tool = new EchoTool();

        var result = await tool.InvokeAsync(JsonSerializer.SerializeToElement("hi"));

        Assert.Equal("Error: tool 'echo' expected a JSON object but received String.", result);
    }

    [Fact]
    public async Task ToolInvokeAsync_UndefinedArguments_ReturnsErrorText()
    {
        var tool = new EchoTool();

        var result = await tool.InvokeAsync(default);

        Assert.Equal("Error: tool 'echo' expected a JSON object but received Undefined.", result);
    }

    [Fact]
    public async Task ToolInvokeAsync_WrongFieldType_ReturnsErrorTextWithExceptionMessage()
    {
        var tool = new EchoTool();
        var arguments = JsonSerializer.SerializeToElement(new { text = 5 });

        var result = await tool.InvokeAsync(arguments);

        Assert.StartsWith(
            "Error: tool 'echo' received arguments that could not be deserialized: ",
            result,
            StringComparison.Ordinal);
        Assert.Contains("could not be converted", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolInvokeAsync_ValidObject_InvokesTool()
    {
        var tool = new EchoTool();
        var arguments = JsonSerializer.SerializeToElement(new { text = "hi" });

        var result = await tool.InvokeAsync(arguments);

        Assert.Equal("echo:hi", result);
    }
}
