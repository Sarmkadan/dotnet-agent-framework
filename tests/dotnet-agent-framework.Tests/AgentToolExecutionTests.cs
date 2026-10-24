using System.Diagnostics;
using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class AgentToolExecutionTests
{
    private static TimeSpan WaitLimit => TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_UnknownToolName_ModelReceivesErrorTextListingAvailableTools()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "missing")));
        model.Responses.Enqueue(Answer("done"));
        var agent = new Agent(model, [new EchoTool()]);

        var result = await agent.RunAsync("go");

        var toolMessage = ToolMessageFrom(model, 1);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Contains("unknown tool 'missing'", toolMessage.Content);
        Assert.Contains("Available tools: echo", toolMessage.Content);
        Assert.Equal("done", result.FinalAnswer);
    }

    [Fact]
    public async Task RunAsync_ToolThrows_ModelReceivesErrorTextAndRunContinues()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "throwing")));
        model.Responses.Enqueue(Answer("recovered"));
        var agent = new Agent(model, [new ThrowingTool()]);

        var result = await agent.RunAsync("go");

        var toolMessage = ToolMessageFrom(model, 1);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Contains("InvalidOperationException", toolMessage.Content);
        Assert.Contains("boom", toolMessage.Content);
        Assert.Equal("recovered", result.FinalAnswer);
    }

    [Fact]
    public async Task RunAsync_ToolExceedsToolTimeout_ModelReceivesTimeoutText()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "slow")));
        model.Responses.Enqueue(Answer("gave up on the tool"));
        var slow = new SlowTool(TimeSpan.FromSeconds(5));
        var options = new AgentOptions { ToolTimeout = TimeSpan.FromMilliseconds(50) };
        var agent = new Agent(model, [slow], options: options);

        var stopwatch = Stopwatch.StartNew();
        var result = await agent.RunAsync("go");
        stopwatch.Stop();

        Assert.True(slow.WasCancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Run took {stopwatch.Elapsed}.");
        Assert.Contains("timed out", ToolMessageFrom(model, 1).Content);
        Assert.Equal("gave up on the tool", result.FinalAnswer);
    }

    [Fact]
    public async Task RunAsync_ToolTimeoutNull_SlowToolCompletes()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "slow")));
        model.Responses.Enqueue(Answer("done"));
        var options = new AgentOptions { ToolTimeout = null };
        var agent = new Agent(model, [new SlowTool(TimeSpan.FromMilliseconds(100))], options: options);

        await agent.RunAsync("go");

        Assert.Equal("slow-done", ToolMessageFrom(model, 1).Content);
    }

    [Fact]
    public async Task RunAsync_CallerCancelsDuringTool_ThrowsOperationCanceledException()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "slow")));
        var slow = new SlowTool(TimeSpan.FromSeconds(5));
        var agent = new Agent(model, [slow]);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync("go", cts.Token));

        Assert.True(slow.WasCancelled);
    }

    [Fact]
    public async Task RunAsync_ParallelToolCallsTrue_ToolsRunConcurrently()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "first"), Call("c2", "second")));
        model.Responses.Enqueue(Answer("done"));
        var first = new RecordingTool("first");
        var second = new RecordingTool("second");
        var options = new AgentOptions { ParallelToolCalls = true };
        var agent = new Agent(model, [first, second], options: options);

        var run = agent.RunAsync("go");
        await Task.WhenAll(first.Started, second.Started).WaitAsync(WaitLimit);

        // Finish the second call while the first is still blocked on its gate.
        second.Gate.SetResult("second-done");
        await second.Completed.WaitAsync(WaitLimit);
        first.Gate.SetResult("first-done");

        await run;

        var firstInvocation = Assert.Single(first.Invocations);
        var secondInvocation = Assert.Single(second.Invocations);
        Assert.True(secondInvocation.EndedAt < firstInvocation.EndedAt);
        Assert.True(
            Math.Max(firstInvocation.StartedAt, secondInvocation.StartedAt)
                < Math.Min(firstInvocation.EndedAt, secondInvocation.EndedAt),
            "Both invocations should have been running at the same time.");

        var toolMessages = model.ReceivedPrompts[1].Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(["c1", "c2"], toolMessages.Select(m => m.ToolCallId));
        Assert.Equal(["first-done", "second-done"], toolMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task RunAsync_ParallelToolCallsFalse_ToolsRunSequentially()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "first"), Call("c2", "second")));
        model.Responses.Enqueue(Answer("done"));
        var first = new RecordingTool("first");
        var second = new RecordingTool("second");
        first.Gate.SetResult("first-done");
        second.Gate.SetResult("second-done");
        var options = new AgentOptions { ParallelToolCalls = false };
        var agent = new Agent(model, [first, second], options: options);

        await agent.RunAsync("go");

        var firstInvocation = Assert.Single(first.Invocations);
        var secondInvocation = Assert.Single(second.Invocations);
        Assert.True(secondInvocation.StartedAt >= firstInvocation.EndedAt);
    }

    [Fact]
    public async Task RunAsync_TwoToolCallsSameStep_BothResultsAppendedBeforeNextCompletion()
    {
        var model = new FakeChatModel();
        model.Responses.Enqueue(Requests(Call("c1", "echo", "a"), Call("c2", "echo", "b")));
        model.Responses.Enqueue(Answer("done"));
        var agent = new Agent(model, [new EchoTool()]);

        await agent.RunAsync("go");

        // The second completion must see the assistant message followed by both results, in call order.
        var secondPrompt = model.ReceivedPrompts[1];
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Tool],
            secondPrompt.Select(m => m.Role));
        Assert.Equal(2, secondPrompt[1].ToolCalls!.Count);

        var toolMessages = secondPrompt.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(["c1", "c2"], toolMessages.Select(m => m.ToolCallId));
        Assert.Equal(["echo:a", "echo:b"], toolMessages.Select(m => m.Content));
    }

    private static ChatCompletion Requests(params ToolCall[] calls)
        => new(ChatMessage.Assistant(string.Empty, calls));

    private static ChatCompletion Answer(string text)
        => new(ChatMessage.Assistant(text));

    private static ToolCall Call(string id, string name, string text = "x")
        => new(id, name, JsonSerializer.SerializeToElement(new { text }));

    private static ChatMessage ToolMessageFrom(FakeChatModel model, int promptIndex)
        => Assert.Single(model.ReceivedPrompts[promptIndex], m => m.Role == MessageRole.Tool);
}
