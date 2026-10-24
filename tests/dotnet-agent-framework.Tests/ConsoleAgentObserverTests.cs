using DotnetAgentFramework.Testing;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class ConsoleAgentObserverTests
{
    [Fact]
    public async Task RunAsync_ToolRoundTrip_WritesOneLinePerEvent()
    {
        var model = new ScriptedChatModel()
            .CallTool("echo", new { text = "hi" }, "c1")
            .Answer("done");
        var writer = new StringWriter();
        var agent = new Agent(
            model,
            [new EchoTool()],
            options: new AgentOptions { Observer = new ConsoleAgentObserver(writer) });

        await agent.RunAsync("go");

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, lines.Length);
        Assert.Equal("[agent] run started: go", lines[0]);
        Assert.Equal("[agent] step 1: started with 1 message(s)", lines[1]);
        Assert.Equal("[agent] step 1: completion received, 1 tool call(s)", lines[2]);
        Assert.Equal("[agent] step 1: tool echo started", lines[3]);
        Assert.Matches(@"^\[agent\] step 1: tool echo completed in \d+ ms$", lines[4]);
        Assert.Equal("[agent] step 2: started with 3 message(s)", lines[5]);
        Assert.Equal("[agent] step 2: completion received", lines[6]);
        Assert.Equal("[agent] run completed: 2 step(s), 0 input / 0 output tokens", lines[7]);
    }

    [Fact]
    public async Task RunAsync_StepLimitExceeded_WritesFailureLine()
    {
        var model = new ScriptedChatModel().CallTool("echo", new { text = "a" }, "c1");
        var writer = new StringWriter();
        var agent = new Agent(
            model,
            [new EchoTool()],
            options: new AgentOptions { MaxSteps = 1, Observer = new ConsoleAgentObserver(writer) });

        await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => agent.RunAsync("go"));

        var lastLine = writer.ToString().TrimEnd().Split(Environment.NewLine).Last();
        Assert.StartsWith("[agent] run failed: AgentStepLimitExceededException: Agent exceeded the maximum of 1", lastLine);
    }
}
