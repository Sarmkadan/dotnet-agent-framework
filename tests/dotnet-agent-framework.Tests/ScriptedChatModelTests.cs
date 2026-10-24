using System.Text.Json;
using DotnetAgentFramework.Testing;

namespace DotnetAgentFramework.Tests;

public class ScriptedChatModelTests
{
    [Fact]
    public void Constructor_NoModelId_UsesScripted()
    {
        var model = new ScriptedChatModel();

        Assert.Equal("scripted", model.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_EmptyScript_ThrowsNamingLastUserMessage()
    {
        var model = new ScriptedChatModel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.CompleteAsync([ChatMessage.User("what is 2+2?")], []));

        Assert.StartsWith("ScriptedChatModel has no more scripted responses. Prompt received:", ex.Message);
        Assert.Contains("\"what is 2+2?\"", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_Answer_ReturnsTextAndUsage()
    {
        var model = new ScriptedChatModel().Answer("42", new TokenUsage(5, 2));

        var completion = await model.CompleteAsync([ChatMessage.User("q")], []);

        Assert.Equal("42", completion.Message.Content);
        Assert.Equal(new TokenUsage(5, 2), completion.Usage);
    }

    [Fact]
    public async Task CallTool_NoCallId_GeneratesSequentialIds()
    {
        var model = new ScriptedChatModel()
            .CallTool("lookup", new { text = "a" })
            .CallTool("lookup", new { text = "b" });

        var first = await model.CompleteAsync([], []);
        var second = await model.CompleteAsync([], []);

        var firstCall = Assert.Single(first.Message.ToolCalls!);
        Assert.Equal("call_1", firstCall.Id);
        Assert.Equal("lookup", firstCall.Name);
        Assert.Equal("a", firstCall.Arguments.GetProperty("text").GetString());
        Assert.Equal("call_2", Assert.Single(second.Message.ToolCalls!).Id);
    }

    [Fact]
    public async Task CallTool_ExplicitCallId_UsesIt()
    {
        var model = new ScriptedChatModel().CallTool("lookup", new { }, callId: "custom");

        var completion = await model.CompleteAsync([], []);

        Assert.Equal("custom", Assert.Single(completion.Message.ToolCalls!).Id);
    }

    [Fact]
    public async Task CallTools_MultipleCalls_ReturnsOneCompletionInRequestOrder()
    {
        var model = new ScriptedChatModel().CallTools(("first", new { }), ("second", new { }));

        var completion = await model.CompleteAsync([], []);

        Assert.Equal(MessageRole.Assistant, completion.Message.Role);
        Assert.Equal(["first", "second"], completion.Message.ToolCalls!.Select(c => c.Name));
        Assert.Equal(["call_1", "call_2"], completion.Message.ToolCalls!.Select(c => c.Id));
    }

    [Fact]
    public async Task StreamAsync_AnswerStreamed_YieldsChunksAndCompletesWithJoinedText()
    {
        var model = new ScriptedChatModel().AnswerStreamed("Hel", "lo");

        var completion = await model.CompleteAsync([], []);
        var deltas = new List<string>();
        await foreach (var delta in model.StreamAsync([], []))
        {
            deltas.Add(delta);
        }

        Assert.Equal("Hello", completion.Message.Content);
        Assert.Equal(["Hel", "lo"], deltas);
        Assert.Equal(1, model.CompletionCalls);
        Assert.Equal(1, model.StreamCalls);
    }

    [Fact]
    public async Task StreamAsync_EmptyScript_ThrowsOnEnumeration()
    {
        var model = new ScriptedChatModel();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in model.StreamAsync([ChatMessage.User("hi")], []))
            {
            }
        });
    }

    [Fact]
    public async Task Prompts_CalledTwice_RecordsSnapshotsInCallOrder()
    {
        var model = new ScriptedChatModel().Answer("one").Answer("two");
        var first = new List<ChatMessage> { ChatMessage.User("first") };
        var tools = new List<ToolDefinition> { ToolDefinition.Create("t", "d", JsonSerializer.SerializeToElement(new { })) };

        await model.CompleteAsync(first, tools);
        first.Add(ChatMessage.User("mutated after the call"));
        await model.CompleteAsync([ChatMessage.User("second")], []);

        Assert.Equal(["first"], model.Prompts[0].Select(m => m.Content));
        Assert.Equal(["second"], model.Prompts[1].Select(m => m.Content));
        Assert.Equal(["t"], model.ToolDefinitions[0].Select(t => t.Name));
        Assert.Empty(model.ToolDefinitions[1]);
    }
}
