using DotnetAgentFramework.Testing;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public sealed class SummarizingAgentMemoryTests
{
    private static SummarizingAgentMemory CreateMemory(ScriptedChatModel summarizer, int maxTokens = 50, int keepRecent = 2)
        => new(summarizer, maxTokens, keepRecent, new FixedTokenCounter());

    [Fact]
    public async Task AppendAsync_UnderBudget_NoSummarizerCall()
    {
        var summarizer = new ScriptedChatModel();
        var memory = CreateMemory(summarizer);

        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(4));

        Assert.Equal(0, summarizer.CompletionCalls);
        Assert.Null(memory.Summary);
    }

    [Fact]
    public async Task AppendAsync_OverBudget_SummarizesOldestAndKeepsRecent()
    {
        var summarizer = new ScriptedChatModel().Answer("scripted summary");
        var memory = CreateMemory(summarizer);

        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(8));

        Assert.Equal("scripted summary", memory.Summary);
        // The summary message counts against the 50-token budget, so after the cut the window holds the summary plus
        // four recent messages (5 × 10 = 50, not over budget). Keeping two would need a second summarization here.
        Assert.Equal(4, memory.RecentCount);

        string userPrompt = summarizer.Prompts[0][1].Content;
        Assert.Contains("User: m0", userPrompt);
        Assert.Contains("Assistant: m3", userPrompt);
        Assert.DoesNotContain("m4", userPrompt);
    }

    [Fact]
    public async Task RecallAsync_AfterSummarization_ReturnsSystemThenSummaryThenRecent()
    {
        var summarizer = new ScriptedChatModel().Answer("scripted summary");
        var memory = CreateMemory(summarizer);
        ChatMessage[] history =
        [
            ChatMessage.System("rules"),
            ChatMessage.User("u1"),
            ChatMessage.Assistant("a2"),
            ChatMessage.User("u3"),
            ChatMessage.Assistant("a4"),
            ChatMessage.User("u5"),
        ];

        await ConversationFixtures.AppendAllAsync(memory, history);
        IReadOnlyList<ChatMessage> window = await memory.RecallAsync("");

        Assert.Equal(4, window.Count);
        Assert.Equal(new[] { "rules", "Summary of earlier conversation:\nscripted summary", "a4", "u5" }, window.Select(m => m.Content));
        Assert.Equal(
            new[] { MessageRole.System, MessageRole.System, MessageRole.Assistant, MessageRole.User },
            window.Select(m => m.Role));
    }

    [Fact]
    public async Task AppendAsync_SecondSummarization_IncludesPreviousSummaryInPrompt()
    {
        var summarizer = new ScriptedChatModel()
            .Answer("first summary")
            .Answer("second summary");
        var memory = CreateMemory(summarizer);

        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(9));

        Assert.Equal(2, summarizer.CompletionCalls);
        Assert.Equal("second summary", memory.Summary);
        Assert.Contains("Previous summary:\nfirst summary", summarizer.Prompts[1][1].Content);
    }

    [Fact]
    public async Task AppendAsync_CutWouldOrphanToolResult_KeepsAssistantWithItsToolResults()
    {
        var summarizer = new ScriptedChatModel().Answer("scripted summary");
        var memory = CreateMemory(summarizer, maxTokens: 40);

        // A naive cut at index 3 would keep the c2 result without its request. The cut must move back to the assistant.
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.ToolRoundTrip());
        IReadOnlyList<ChatMessage> window = await memory.RecallAsync("");

        Assert.Equal("scripted summary", memory.Summary);
        Assert.Equal(5, window.Count);
        Assert.Equal(MessageRole.Assistant, window[1].Role);
        Assert.Equal(new[] { "c1", "c2" }, window[1].ToolCalls!.Select(call => call.Id));
        Assert.Equal("c1", window[2].ToolCallId);
        Assert.Equal("c2", window[3].ToolCallId);
        Assert.Equal("done", window[4].Content);
    }

    [Fact]
    public async Task AppendAsync_SummarizerThrows_MessagesRetainedAndInvalidOperationExceptionThrown()
    {
        // Empty script: the summarizer call fails.
        var summarizer = new ScriptedChatModel();
        var memory = CreateMemory(summarizer);
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(5));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => memory.AppendAsync(ChatMessage.User("m5")));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Null(memory.Summary);
        Assert.Equal(6, memory.RecentCount);
        Assert.Equal("m5", (await memory.RecallAsync(""))[^1].Content);
    }

    [Fact]
    public async Task ClearAsync_ResetsSummaryAndRecent()
    {
        var summarizer = new ScriptedChatModel().Answer("scripted summary");
        var memory = CreateMemory(summarizer);
        await ConversationFixtures.AppendAllAsync(memory, ConversationFixtures.Alternating(6));
        Assert.NotNull(memory.Summary);

        await memory.ClearAsync();

        Assert.Null(memory.Summary);
        Assert.Equal(0, memory.RecentCount);
        Assert.Empty(await memory.RecallAsync(""));
    }

    /// <summary>
    /// Charges a flat 10 tokens per message, so budgets map directly to message counts.
    /// </summary>
    private sealed class FixedTokenCounter : ITokenCounter
    {
        public int Count(ChatMessage message) => 10;
    }
}
