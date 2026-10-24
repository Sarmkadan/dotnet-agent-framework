using System.Text.Json;
using DotnetAgentFramework.Testing;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class AgentRunAsyncTests
{
    [Fact]
    public async Task RunAsync_ModelAnswersDirectly_ReturnsFinalAnswerWithOneStep()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("42")));
        var agent = new Agent(model, [new EchoTool()]);

        var result = await agent.RunAsync("What is the answer?");

        Assert.Equal("42", result.FinalAnswer);
        Assert.Equal(1, result.Steps);
    }

    [Fact]
    public async Task RunAsync_SystemPromptConfigured_FirstPromptMessageIsSystem()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("ok")));
        var memory = new InMemoryAgentMemory();
        var options = new AgentOptions { SystemPrompt = "Be terse." };
        var agent = new Agent(model, [], memory, options);

        await agent.RunAsync("hi");

        var prompt = model.Prompts.Single();
        Assert.Equal(MessageRole.System, prompt[0].Role);
        Assert.Equal("Be terse.", prompt[0].Content);

        var stored = await memory.RecallAsync("hi");
        Assert.DoesNotContain(stored, m => m.Role == MessageRole.System);
    }

    [Fact]
    public async Task RunAsync_ModelRequestsTool_ToolResultSentBackAndFinalAnswerReturned()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(
            ChatMessage.Assistant("", [new ToolCall("c1", "echo", JsonSerializer.SerializeToElement(new { text = "hi" }))])));
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("done")));
        var agent = new Agent(model, [new EchoTool()]);

        var result = await agent.RunAsync("say hi");

        Assert.Equal("done", result.FinalAnswer);
        Assert.Equal(2, result.Steps);

        var secondPrompt = model.Prompts[1];
        var toolMessage = Assert.Single(secondPrompt, m => m.Role == MessageRole.Tool);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Equal("echo:hi", toolMessage.Content);

        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant],
            result.Transcript.Select(m => m.Role));
    }

    [Fact]
    public async Task RunAsync_UsageReported_AggregatesAcrossSteps()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(
            ChatMessage.Assistant("", [new ToolCall("c1", "echo", JsonSerializer.SerializeToElement(new { text = "x" }))]),
            new TokenUsage(10, 5)));
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("done"), new TokenUsage(7, 3)));
        var agent = new Agent(model, [new EchoTool()]);

        var result = await agent.RunAsync("go");

        Assert.Equal(new TokenUsage(17, 8), result.Usage);
        Assert.Equal(25, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_ModelNeverStopsCallingTools_ThrowsAgentStepLimitExceeded()
    {
        var model = new ScriptedChatModel();
        for (var i = 1; i <= 3; i++)
        {
            model.Enqueue(new ChatCompletion(
                ChatMessage.Assistant("", [new ToolCall($"c{i}", "echo", JsonSerializer.SerializeToElement(new { text = "again" }))])));
        }

        var agent = new Agent(model, [new EchoTool()], options: new AgentOptions { MaxSteps = 3 });

        var exception = await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => agent.RunAsync("loop"));

        Assert.Equal(3, exception.MaxSteps);
        Assert.Equal(3, model.Prompts.Count);
    }

    [Fact]
    public async Task RunAsync_ToolDefinitionsPassedToModel_ContainRegisteredToolNames()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("ok")));
        var agent = new Agent(model, [new EchoTool()]);

        await agent.RunAsync("hi");

        var tools = model.ToolDefinitions.Single();
        Assert.Equal(["echo"], tools.Select(t => t.Name));
    }

    [Fact]
    public async Task RunAsync_CalledTwice_SecondRunSeesFirstConversation()
    {
        var model = new ScriptedChatModel();
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("one")));
        model.Enqueue(new ChatCompletion(ChatMessage.Assistant("two")));
        var agent = new Agent(model, [], new InMemoryAgentMemory());

        await agent.RunAsync("first");
        var second = await agent.RunAsync("second");

        Assert.Equal("two", second.FinalAnswer);
        Assert.Equal(["first", "one", "second"], model.Prompts[1].Select(m => m.Content));
    }
}
