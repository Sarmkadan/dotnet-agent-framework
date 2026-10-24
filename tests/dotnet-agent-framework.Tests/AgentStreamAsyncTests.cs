using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class AgentStreamAsyncTests
{
    [Fact]
    public async Task StreamAsync_NoTools_YieldsAllDeltasInOrder()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Answer("ignored"));
        model.StreamChunks.Enqueue(["Hel", "lo", " world"]);
        var agent = new Agent(model, []);

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Equal("Hello world", string.Concat(deltas));
        Assert.Equal(["Hel", "lo", " world"], deltas);
    }

    [Fact]
    public async Task StreamAsync_NoTools_AppendsFinalAssistantMessageToMemory()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Answer("ignored"));
        model.StreamChunks.Enqueue(["Hel", "lo", " world"]);
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [], memory);

        await CollectAsync(agent.StreamAsync("go"));

        var history = await memory.RecallAsync("go");
        Assert.Equal(MessageRole.Assistant, history[^1].Role);
        Assert.Equal("Hello world", history[^1].Content);
    }

    [Fact]
    public async Task StreamAsync_ToolStepThenAnswer_ExecutesToolBeforeStreaming()
    {
        var tool = new RecordingTool("lookup");
        tool.Gate.SetResult("found");

        var model = new FakeChatModel();
        model.Responses.Enqueue(ToolCallTurn(new ToolCall("c1", "lookup", default)));
        model.Responses.Enqueue(Answer("draft"));
        model.StreamChunks.Enqueue(["final"]);
        var agent = new Agent(model, [tool]);

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Single(tool.Invocations);
        Assert.Equal(["final"], deltas);
        Assert.Equal(1, model.StreamCalls);

        var streamedPrompt = Assert.Single(model.StreamedPrompts);
        var toolMessage = Assert.Single(streamedPrompt, m => m.Role == MessageRole.Tool);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Equal("found", toolMessage.Content);
    }

    [Fact]
    public async Task StreamAsync_MaxStepsExhausted_ThrowsAgentStepLimitExceeded()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(ToolCallTurn(new ToolCall("c1", "echo", JsonSerializer.SerializeToElement(new { text = "a" }))));
        model.Responses.Enqueue(ToolCallTurn(new ToolCall("c2", "echo", JsonSerializer.SerializeToElement(new { text = "b" }))));
        model.StreamChunks.Enqueue(["never"]);
        var agent = new Agent(model, [new EchoTool()], options: new AgentOptions { MaxSteps = 2 });

        var exception = await Assert.ThrowsAsync<AgentStepLimitExceededException>(
            () => CollectAsync(agent.StreamAsync("go")));

        Assert.Equal(2, exception.MaxSteps);
        Assert.Equal(2, model.ReceivedPrompts.Count);
        Assert.Equal(0, model.StreamCalls);
    }

    [Fact]
    public async Task StreamAsync_CancelledMidStream_ThrowsOperationCanceledException()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Answer("ignored"));
        model.StreamChunks.Enqueue(["Hel", "lo", " world"]);
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [], memory);
        using var cts = new CancellationTokenSource();
        var received = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var delta in agent.StreamAsync("go", cts.Token))
            {
                received.Add(delta);
                cts.Cancel();
            }
        });

        Assert.Equal(["Hel"], received);

        // An interrupted answer is not recorded; only the user message remains.
        var history = await memory.RecallAsync("go");
        Assert.Equal([MessageRole.User], history.Select(m => m.Role));
    }

    [Fact]
    public async Task StreamAsync_SystemPrompt_IncludedInStreamedPrompt()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Answer("ignored"));
        model.StreamChunks.Enqueue(["ok"]);
        var options = new AgentOptions { SystemPrompt = "Be terse." };
        var agent = new Agent(model, [], options: options);

        await CollectAsync(agent.StreamAsync("go"));

        var streamedPrompt = Assert.Single(model.StreamedPrompts);
        Assert.Equal(MessageRole.System, streamedPrompt[0].Role);
        Assert.Equal("Be terse.", streamedPrompt[0].Content);
    }

    [Fact]
    public async Task StreamAsync_EmptyStream_AppendsEmptyAssistantMessage()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Answer(string.Empty));
        model.StreamChunks.Enqueue([]);
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [], memory);

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Empty(deltas);
        var history = await memory.RecallAsync("go");
        Assert.Equal(MessageRole.Assistant, history[^1].Role);
        Assert.Equal(string.Empty, history[^1].Content);
    }

    private static ChatCompletion Answer(string content)
        => new(ChatMessage.Assistant(content));

    private static ChatCompletion ToolCallTurn(params ToolCall[] calls)
        => new(ChatMessage.Assistant(string.Empty, calls));

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var items = new List<string>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }
}
