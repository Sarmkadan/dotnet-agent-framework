using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class TokenBudgetAgentMemoryTests
{
    [Fact]
    public async Task RecallAsync_BudgetFitsAll_ReturnsAll()
    {
        var memory = new TokenBudgetAgentMemory(100, new FixedTokenCounter());
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(3));

        var window = await memory.RecallAsync("");

        Assert.Equal(new[] { "m0", "m1", "m2" }, window.Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_Budget35_ReturnsNewestThree()
    {
        var memory = new TokenBudgetAgentMemory(35, new FixedTokenCounter());
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(10));

        var window = await memory.RecallAsync("");

        Assert.Equal(new[] { "m7", "m8", "m9" }, window.Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_SystemMessageReservedFirst()
    {
        // The system message costs 10 of the 35 tokens, so only 25 remain: two newest messages fit.
        var memory = new TokenBudgetAgentMemory(35, new FixedTokenCounter());
        var history = new[] { ChatMessage.System("rules") }.Concat(ConversationFixtures.Alternating(10));
        await ConversationFixtures.AppendAllAsync(memory, history);

        var window = await memory.RecallAsync("");

        Assert.Equal(3, window.Count);
        Assert.Equal(MessageRole.System, window[0].Role);
        Assert.Equal("rules", window[0].Content);
        Assert.Equal(new[] { "m8", "m9" }, window.Skip(1).Select(m => m.Content));
    }

    [Fact]
    public async Task RecallAsync_SingleMessageOverBudget_ReturnsThatMessage()
    {
        // Each message costs 10 tokens against a budget of 5. The newest message is still returned.
        var memory = new TokenBudgetAgentMemory(5, new FixedTokenCounter());
        await ConversationFixtures.AppendAllAsync(memory, [ChatMessage.User("first"), ChatMessage.User("second")]);

        var window = await memory.RecallAsync("");

        Assert.Single(window);
        Assert.Equal("second", window[0].Content);
    }

    [Fact]
    public async Task RecallAsync_ToolPairPreserved_WhenBudgetCutsInsideRoundTrip()
    {
        // Budget 20 fits only the newest two messages, which are Tool(c2) and Assistant("done").
        // The cut would start on Tool(c2), so the window expands back to the Assistant that requested c1 and c2.
        var memory = new TokenBudgetAgentMemory(20, new FixedTokenCounter());
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.ToolRoundTrip());

        var window = await memory.RecallAsync("");

        Assert.Equal(4, window.Count);
        Assert.Equal(MessageRole.Assistant, window[0].Role);
        Assert.Equal(new[] { "c1", "c2" }, window[0].ToolCalls!.Select(call => call.Id));
        Assert.Equal(new[] { "c1", "c2" }, window.Skip(1).Take(2).Select(m => m.ToolCallId));
        Assert.Equal("done", window[3].Content);
    }

    [Fact]
    public async Task RecallAsync_UnderBudgetStartsWithOrphanToolResult_DropsIt()
    {
        // The tool result answers a call that is not in the history. The budget fits everything,
        // so the cut must still drop the orphan, as the sliding window does.
        var memory = new TokenBudgetAgentMemory(100, new FixedTokenCounter());
        await ConversationFixtures.AppendAllAsync(
            memory,
            [ChatMessage.ToolResult("missing", "stale"), ChatMessage.User("hello")]);

        var window = await memory.RecallAsync("");

        Assert.Single(window);
        Assert.Equal(MessageRole.User, window[0].Role);
    }

    [Fact]
    public async Task ApproximateTokenCounter_Count_LongerContentCountsMore()
    {
        var counter = new ApproximateTokenCounter();

        int shortCount = counter.Count(ChatMessage.User("hi"));
        int longCount = counter.Count(ChatMessage.User(new string('x', 400)));

        Assert.True(longCount > shortCount);
        Assert.Equal(4 + 100, longCount);
    }

    [Fact]
    public void ApproximateTokenCounter_Count_IncludesToolCallArguments()
    {
        var counter = new ApproximateTokenCounter();
        JsonElement arguments = JsonDocument.Parse("{\"query\":\"weather in Kyiv on Tuesday morning\"}").RootElement.Clone();
        var withoutCall = ChatMessage.Assistant("");
        var withCall = ChatMessage.Assistant("", new[] { ToolCall.Create("c1", "weather", arguments) });

        int plain = counter.Count(withoutCall);
        int called = counter.Count(withCall);

        Assert.True(called > plain);
    }

    /// <summary>
    /// Charges a flat 10 tokens per message, so budgets map directly to message counts.
    /// </summary>
    private sealed class FixedTokenCounter : ITokenCounter
    {
        public int Count(ChatMessage message) => 10;
    }
}
