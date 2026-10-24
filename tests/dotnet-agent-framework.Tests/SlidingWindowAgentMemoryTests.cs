using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class SlidingWindowAgentMemoryTests
{
    [Fact]
    public async Task RecallAsync_UnderLimit_ReturnsAllMessages()
    {
        var memory = new SlidingWindowAgentMemory(5);
        await ConversationFixtures.AppendAllAsync(
            memory,
            [ChatMessage.User("a"), ChatMessage.Assistant("b"), ChatMessage.User("c")]);

        var window = await memory.RecallAsync("");

        Assert.Equal(new[] { "a", "b", "c" }, window.Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_OverLimit_ReturnsNewestMessages()
    {
        var memory = new SlidingWindowAgentMemory(4);
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(10));

        var window = await memory.RecallAsync("");

        Assert.Equal(new[] { "m6", "m7", "m8", "m9" }, window.Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_FirstMessageIsSystem_AlwaysIncluded()
    {
        var memory = new SlidingWindowAgentMemory(4);
        var history = new[] { ChatMessage.System("rules") }.Concat(ConversationFixtures.Alternating(10));
        await ConversationFixtures.AppendAllAsync(memory, history);

        var window = await memory.RecallAsync("");

        Assert.Equal(4, window.Count);
        Assert.Equal(MessageRole.System, window[0].Role);
        Assert.Equal("rules", window[0].Content);
        Assert.Equal(new[] { "m7", "m8", "m9" }, window.Skip(1).Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_CutLandsOnToolResult_ExpandsBackToOwningAssistantMessage()
    {
        var memory = new SlidingWindowAgentMemory(2);
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.ToolRoundTrip());

        var window = await memory.RecallAsync("");

        Assert.Equal(4, window.Count);
        Assert.Equal(MessageRole.Assistant, window[0].Role);
        Assert.Equal(new[] { "c1", "c2" }, window[0].ToolCalls!.Select(call => call.Id));
        Assert.Equal(MessageRole.Tool, window[1].Role);
        Assert.Equal("c1", window[1].ToolCallId);
        Assert.Equal(MessageRole.Tool, window[2].Role);
        Assert.Equal("c2", window[2].ToolCallId);
        Assert.Equal("done", window[3].Content);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RecallAsync_NeverStartsWithToolMessage(int maxMessages)
    {
        var memory = new SlidingWindowAgentMemory(maxMessages);
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.ToolRoundTrip());

        var window = await memory.RecallAsync("");

        Assert.NotEmpty(window);
        Assert.NotEqual(MessageRole.Tool, window[0].Role);
    }

    [Fact]
    public void Constructor_MaxMessagesZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowAgentMemory(0));
    }

    [Fact]
    public async Task RecallAsync_UnderLimitStartsWithOrphanToolResult_DropsIt()
    {
        // The tool result answers a call that is not in the history, so no provider accepts it as the first message.
        var memory = new SlidingWindowAgentMemory(4);
        await ConversationFixtures.AppendAllAsync(
            memory,
            [ChatMessage.ToolResult("missing", "stale"), ChatMessage.User("hello")]);

        var window = await memory.RecallAsync("");

        Assert.Single(window);
        Assert.Equal(MessageRole.User, window[0].Role);
        Assert.Equal("hello", window[0].Content);
    }
}
