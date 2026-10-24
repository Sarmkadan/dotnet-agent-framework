using DotnetAgentFramework.Testing;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class AgentObserverTests
{
    [Fact]
    public async Task RunAsync_ToolRoundTrip_FiresHooksInOrder()
    {
        var model = new ScriptedChatModel()
            .CallTool("echo", new { text = "hi" }, "c1")
            .Answer("done");
        var observer = new RecordingAgentObserver();
        var agent = new Agent(model, [new EchoTool()], options: new AgentOptions { Observer = observer });

        await agent.RunAsync("go");

        Assert.Equal(
            [
                "RunStarted:go",
                "StepStarted:1",
                "CompletionReceived:1",
                "ToolStarted:1:echo",
                "ToolCompleted:1:echo:echo:hi",
                "StepStarted:2",
                "CompletionReceived:2",
                "RunCompleted:done",
            ],
            observer.Events);
    }

    [Fact]
    public async Task RunAsync_StepLimitExceeded_FiresRunFailedAndNoRunCompleted()
    {
        var model = new ScriptedChatModel().CallTool("echo", new { text = "a" }, "c1");
        var observer = new RecordingAgentObserver();
        var agent = new Agent(model, [new EchoTool()], options: new AgentOptions { MaxSteps = 1, Observer = observer });

        await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => agent.RunAsync("go"));

        Assert.Equal(["RunStarted:go", "StepStarted:1", "CompletionReceived:1", "ToolStarted:1:echo", "ToolCompleted:1:echo:echo:a", "RunFailed:AgentStepLimitExceededException"], observer.Events);
        Assert.IsType<AgentStepLimitExceededException>(observer.Failures.Single());
    }

    [Fact]
    public async Task RunAsync_ObserverThrowsOnEveryHook_RunStillReturnsAnswer()
    {
        var model = new ScriptedChatModel()
            .CallTool("echo", new { text = "hi" }, "c1")
            .Answer("done");
        var agent = new Agent(model, [new EchoTool()], options: new AgentOptions { Observer = new ThrowingAgentObserver() });

        var result = await agent.RunAsync("go");

        Assert.Equal("done", result.FinalAnswer);
        Assert.Equal(2, result.Steps);
    }

    [Fact]
    public async Task RunAsync_ObserverThrowsInOnRunFailed_OriginalExceptionStillPropagates()
    {
        var model = new ScriptedChatModel().CallTool("echo", new { text = "a" }, "c1");
        var agent = new Agent(
            model,
            [new EchoTool()],
            options: new AgentOptions { MaxSteps = 1, Observer = new ThrowingAgentObserver() });

        await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => agent.RunAsync("go"));
    }

    [Fact]
    public async Task StreamAsync_StreamedAnswer_FiresRunCompletedWithStreamedText()
    {
        var model = new ScriptedChatModel().AnswerStreamed("Hel", "lo");
        var observer = new RecordingAgentObserver();
        var agent = new Agent(model, [], options: new AgentOptions { Observer = observer });

        var deltas = await CollectAsync(agent.StreamAsync("go"));

        Assert.Equal(["Hel", "lo"], deltas);
        Assert.Equal(
            ["RunStarted:go", "StepStarted:1", "CompletionReceived:1", "RunCompleted:Hello"],
            observer.Events);
        var completed = observer.Results.Single();
        Assert.Equal(1, completed.Steps);
        Assert.Equal(2, completed.Transcript.Count);
    }

    [Fact]
    public async Task RunAsync_NoObserver_RunsNormally()
    {
        var model = new ScriptedChatModel().Answer("done");
        var agent = new Agent(model, []);

        var result = await agent.RunAsync("go");

        Assert.Equal("done", result.FinalAnswer);
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

    /// <summary>Records every event as a short string, so tests can assert the exact order.</summary>
    private sealed class RecordingAgentObserver : IAgentObserver
    {
        private readonly List<string> _events = [];
        private readonly List<AgentResult> _results = [];
        private readonly List<Exception> _failures = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public IReadOnlyList<AgentResult> Results => _results;

        public IReadOnlyList<Exception> Failures => _failures;

        public void OnRunStarted(string input) => Add($"RunStarted:{input}");

        public void OnStepStarted(int step, IReadOnlyList<ChatMessage> prompt) => Add($"StepStarted:{step}");

        public void OnCompletionReceived(int step, ChatCompletion completion) => Add($"CompletionReceived:{step}");

        public void OnToolStarted(int step, ToolCall call) => Add($"ToolStarted:{step}:{call.Name}");

        public void OnToolCompleted(int step, ToolCall call, string result, TimeSpan elapsed)
            => Add($"ToolCompleted:{step}:{call.Name}:{result}");

        public void OnRunCompleted(AgentResult result)
        {
            _results.Add(result);
            Add($"RunCompleted:{result.FinalAnswer}");
        }

        public void OnRunFailed(Exception exception)
        {
            _failures.Add(exception);
            Add($"RunFailed:{exception.GetType().Name}");
        }

        private void Add(string value)
        {
            lock (_events)
            {
                _events.Add(value);
            }
        }
    }

    /// <summary>Throws from every hook, to prove the agent isolates observer failures.</summary>
    private sealed class ThrowingAgentObserver : IAgentObserver
    {
        public void OnRunStarted(string input) => throw new InvalidOperationException("observer");

        public void OnStepStarted(int step, IReadOnlyList<ChatMessage> prompt) => throw new InvalidOperationException("observer");

        public void OnCompletionReceived(int step, ChatCompletion completion) => throw new InvalidOperationException("observer");

        public void OnToolStarted(int step, ToolCall call) => throw new InvalidOperationException("observer");

        public void OnToolCompleted(int step, ToolCall call, string result, TimeSpan elapsed)
            => throw new InvalidOperationException("observer");

        public void OnRunCompleted(AgentResult result) => throw new InvalidOperationException("observer");

        public void OnRunFailed(Exception exception) => throw new InvalidOperationException("observer");
    }
}
