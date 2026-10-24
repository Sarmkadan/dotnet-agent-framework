using System.Collections.Concurrent;
using System.Text.Json;
using DotnetAgentFramework.Testing;

namespace DotnetAgentFramework.Tests;

public class AgentTests
{
    [Fact]
    public async Task RunAsync_ParallelToolCalls_RunsConcurrentlyAndKeepsCallOrder()
    {
        var barrierTimeout = TimeSpan.FromSeconds(5);
        var started = new CountdownEvent(2);
        var bothStarted = new TaskCompletionSource();

        async Task<string> Slow(JsonElement _, CancellationToken ct)
        {
            started.Signal();
            await bothStarted.Task.WaitAsync(barrierTimeout, ct);
            await Task.Delay(50, ct);
            return "a-done";
        }

        async Task<string> Fast(JsonElement _, CancellationToken ct)
        {
            started.Signal();
            await bothStarted.Task.WaitAsync(barrierTimeout, ct);
            return "b-done";
        }

        _ = Task.Run(() =>
        {
            started.Wait(barrierTimeout);
            bothStarted.TrySetResult();
        });

        var model = Script(
            Turn(new ToolCall("call-a", "slow", default), new ToolCall("call-b", "fast", default)),
            Turn(ChatMessage.Assistant("done")));
        var agent = new Agent(model, [new FakeTool("slow", Slow), new FakeTool("fast", Fast)]);

        var result = await agent.RunAsync("go");

        var toolMessages = result.Transcript.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(["call-a", "call-b"], toolMessages.Select(m => m.ToolCallId));
        Assert.Equal(["a-done", "b-done"], toolMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task RunAsync_ParallelToolCallsDisabled_RunsCallsOneAtATime()
    {
        var active = 0;
        var maxActive = 0;

        async Task<string> Track(JsonElement _, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref active);
            return "ok";
        }

        var model = Script(
            Turn(new ToolCall("1", "t", default), new ToolCall("2", "t", default), new ToolCall("3", "t", default)),
            Turn(ChatMessage.Assistant("done")));
        var options = new AgentOptions { ParallelToolCalls = false };
        var agent = new Agent(model, [new FakeTool("t", Track)], options: options);

        var result = await agent.RunAsync("go");

        Assert.Equal(1, maxActive);
        Assert.Equal(["1", "2", "3"], result.Transcript.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolCallId));
    }

    [Fact]
    public async Task StreamAsync_FinalAnswer_YieldsDeltasAndRecordsConcatenatedAnswer()
    {
        var model = new ScriptedChatModel().AnswerStreamed("Hel", "lo");
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [], memory);

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Equal(["Hel", "lo"], deltas);
        Assert.Equal(1, model.StreamCalls);
        var history = await memory.RecallAsync("go");
        Assert.Equal(["go", "Hello"], history.Select(m => m.Content));
        Assert.Equal(MessageRole.Assistant, history[^1].Role);
    }

    [Fact]
    public async Task StreamAsync_ToolCallThenAnswer_ExecutesToolsBeforeStreaming()
    {
        var model = Script(Turn(new ToolCall("call-1", "lookup", default)))
            .AnswerStreamed("ok");
        var memory = new InMemoryAgentMemory();
        var agent = new Agent(model, [new FakeTool("lookup", (_, _) => Task.FromResult("found"))], memory);

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Equal(["ok"], deltas);
        Assert.Equal(1, model.StreamCalls);
        var history = await memory.RecallAsync("go");
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant],
            history.Select(m => m.Role));
        Assert.Equal("found", history[2].Content);
        Assert.Equal("ok", history[^1].Content);
    }

    [Fact]
    public async Task StreamAsync_StepLimitReachedWithToolCalls_ThrowsWithoutStreaming()
    {
        var model = Script(
            Turn(new ToolCall("1", "t", default)),
            Turn(new ToolCall("2", "t", default)));
        var agent = new Agent(model, [new FakeTool("t", (_, _) => Task.FromResult("ok"))], options: new AgentOptions { MaxSteps = 2 });

        await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => CollectAsync(agent.StreamAsync("go")));
        Assert.Equal(0, model.StreamCalls);
    }

    private static ChatCompletion Turn(params ToolCall[] calls)
        => new(ChatMessage.Assistant(string.Empty, calls));

    private static ChatCompletion Turn(ChatMessage message)
        => new(message);

    private static ScriptedChatModel Script(params ChatCompletion[] turns)
    {
        var model = new ScriptedChatModel();
        foreach (var turn in turns)
        {
            model.Enqueue(turn);
        }

        return model;
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var items = new List<string>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (candidate <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, seen) != seen);
    }

    private sealed class FakeTool(string name, Func<JsonElement, CancellationToken, Task<string>> invoke) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, name, default);

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => invoke(arguments, cancellationToken);
    }
}
