using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class AgentGuardTests
{
    [Fact]
    public void Constructor_DuplicateToolName_ThrowsArgumentExceptionNamingTheTool()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new Agent(new FakeChatModel(), [new EchoTool(), new EchoTool()]));

        Assert.Equal("tools", ex.ParamName);
        Assert.Contains("Duplicate tool name 'echo'.", ex.Message);
    }

    [Fact]
    public void Constructor_NullToolEntry_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new Agent(new FakeChatModel(), [new EchoTool(), null!]));

        Assert.Equal("tools", ex.ParamName);
        Assert.Equal("Tools collection contains a null entry. (Parameter 'tools')", ex.Message);
    }

    [Fact]
    public void Constructor_ToolWithWhitespaceName_ThrowsArgumentExceptionNamingToolType()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new Agent(new FakeChatModel(), [new RecordingTool("   ")]));

        Assert.Equal("tools", ex.ParamName);
        Assert.Contains(typeof(RecordingTool).FullName!, ex.Message);
        Assert.Contains("null or whitespace name", ex.Message);
    }

    [Fact]
    public void Constructor_MaxStepsZero_ThrowsArgumentOutOfRangeException()
    {
        var options = new AgentOptions { MaxSteps = 0 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Agent(new FakeChatModel(), [], options: options));

        Assert.Equal("options", ex.ParamName);
        Assert.Equal(0, ex.ActualValue);
        Assert.Contains("MaxSteps must be at least 1.", ex.Message);
    }

    [Fact]
    public void Constructor_NegativeToolTimeout_ThrowsArgumentOutOfRangeException()
    {
        var options = new AgentOptions { ToolTimeout = TimeSpan.FromSeconds(-1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Agent(new FakeChatModel(), [], options: options));

        Assert.Equal("options", ex.ParamName);
        Assert.Equal(TimeSpan.FromSeconds(-1), ex.ActualValue);
    }

    [Fact]
    public void Constructor_ZeroToolTimeout_ThrowsArgumentOutOfRangeException()
    {
        var options = new AgentOptions { ToolTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Agent(new FakeChatModel(), [], options: options));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullToolTimeout_DisablesTimeoutWithoutThrowing()
    {
        var options = new AgentOptions { ToolTimeout = null };

        var agent = new Agent(new FakeChatModel(), [], options: options);

        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task RunAsync_EmptyInput_ThrowsArgumentExceptionBeforeCallingModel()
    {
        var model = new FakeChatModel();
        var agent = new Agent(model, []);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync(""));

        Assert.Equal("input", ex.ParamName);
        Assert.Empty(model.ReceivedPrompts);
    }

    [Fact]
    public void StreamAsync_WhitespaceInput_ThrowsArgumentExceptionBeforeEnumeration()
    {
        var model = new FakeChatModel();
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [], memory);

        // No enumeration happens here: the throw must come from the call itself.
        var ex = Assert.Throws<ArgumentException>(() => agent.StreamAsync("   "));

        Assert.Equal("input", ex.ParamName);
        Assert.Equal(0, model.StreamCalls);
        Assert.Empty(model.ReceivedPrompts);
    }

    [Fact]
    public async Task RunAsync_ModelReturnsInvalidMessage_ThrowsInvalidOperationExceptionNamingModel()
    {
        var model = new FakeChatModel();
        // A user-role message carrying tool calls is malformed: only assistant messages may.
        model.Responses.Enqueue(new ChatCompletion(
            new ChatMessage(MessageRole.User, "", [ToolCall.Create("c1", "echo", JsonSerializer.SerializeToElement(new { text = "x" }))])));
        var agent = new Agent(model, [new EchoTool()]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("hi"));

        Assert.Equal(
            "Model 'fake' returned an invalid message: Only Assistant messages may carry ToolCalls, but a User message did. (Parameter 'message')",
            ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Single(model.ReceivedPrompts);
    }

    [Fact]
    public async Task StreamAsync_ModelReturnsInvalidMessage_ThrowsInvalidOperationExceptionOnEnumeration()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(new ChatCompletion(new ChatMessage(MessageRole.Tool, "orphan")));
        var agent = new Agent(model, []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in agent.StreamAsync("hi"))
            {
            }
        });

        Assert.StartsWith("Model 'fake' returned an invalid message: Tool messages must have a non-whitespace ToolCallId.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void AgentStepLimitExceededException_MaxStepsZero_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentStepLimitExceededException(0));

        Assert.Equal("maxSteps", ex.ParamName);
    }
}
