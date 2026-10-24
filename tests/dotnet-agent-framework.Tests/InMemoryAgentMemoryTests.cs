namespace DotnetAgentFramework.Tests;

public class InMemoryAgentMemoryTests
{
    [Fact]
    public async Task RecallAsync_Empty_ReturnsEmptyList()
    {
        var memory = new InMemoryAgentMemory();

        var messages = await memory.RecallAsync("anything");

        Assert.Empty(messages);
    }

    [Fact]
    public async Task AppendAsync_ThreeMessages_RecallReturnsInOrder()
    {
        var memory = new InMemoryAgentMemory();

        await memory.AppendAsync(ChatMessage.System("be brief"));
        await memory.AppendAsync(ChatMessage.User("hello"));
        await memory.AppendAsync(ChatMessage.Assistant("hi"));

        var messages = await memory.RecallAsync("");

        Assert.Equal(3, messages.Count);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Equal("be brief", messages[0].Content);
        Assert.Equal(MessageRole.User, messages[1].Role);
        Assert.Equal("hello", messages[1].Content);
        Assert.Equal(MessageRole.Assistant, messages[2].Role);
        Assert.Equal("hi", messages[2].Content);
    }

    [Fact]
    public async Task RecallAsync_ReturnsSnapshot_LaterAppendNotVisible()
    {
        var memory = new InMemoryAgentMemory();
        await memory.AppendAsync(ChatMessage.User("first"));

        var snapshot = await memory.RecallAsync("");
        await memory.AppendAsync(ChatMessage.User("second"));

        Assert.Single(snapshot);
        Assert.Equal(2, memory.Count);
    }

    [Fact]
    public async Task ClearAsync_AfterAppend_RecallReturnsEmpty()
    {
        var memory = new InMemoryAgentMemory();
        await memory.AppendAsync(ChatMessage.User("hello"));

        await memory.ClearAsync();

        Assert.Empty(await memory.RecallAsync(""));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public async Task RecallAsync_QueryIgnored_SameResultForDifferentQueries()
    {
        var memory = new InMemoryAgentMemory();
        await memory.AppendAsync(ChatMessage.User("alpha"));
        await memory.AppendAsync(ChatMessage.Assistant("beta"));

        var forFirst = await memory.RecallAsync("first query");
        var forSecond = await memory.RecallAsync("completely different");

        Assert.Equal(forFirst, forSecond);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentWriters_AllMessagesStored()
    {
        const int writers = 8;
        const int appendsPerWriter = 250;
        var memory = new InMemoryAgentMemory();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, writers),
            async (writer, cancellationToken) =>
            {
                for (var i = 0; i < appendsPerWriter; i++)
                {
                    await memory.AppendAsync(ChatMessage.User($"{writer}-{i}"), cancellationToken);
                }
            });

        Assert.Equal(writers * appendsPerWriter, memory.Count);
        Assert.Equal(writers * appendsPerWriter, (await memory.RecallAsync("")).Count);
    }

    [Fact]
    public async Task AppendAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var memory = new InMemoryAgentMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => memory.AppendAsync(ChatMessage.User("never stored"), cts.Token));

        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public async Task AppendAsync_ToolResultMessage_PreservesToolCallId()
    {
        var memory = new InMemoryAgentMemory();

        await memory.AppendAsync(ChatMessage.ToolResult("call_42", "{\"ok\":true}"));

        var message = Assert.Single(await memory.RecallAsync(""));
        Assert.Equal(MessageRole.Tool, message.Role);
        Assert.Equal("call_42", message.ToolCallId);
        Assert.Equal("{\"ok\":true}", message.Content);
    }
}
