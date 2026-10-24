using DotnetAgentFramework.Testing;

namespace DotnetAgentFramework.Tests;

public class MemoryGuardTests
{
    private static readonly string _unusedLogPath = Path.Combine(Path.GetTempPath(), "memory-guard-unused.jsonl");

    // Factories only construct the memory. Null and empty-query checks run before any file or model access,
    // so the JSON file path is never written.
    public static TheoryData<Func<IAgentMemory>> Memories => new()
    {
        { () => new InMemoryAgentMemory() },
        { () => new SlidingWindowAgentMemory(4) },
        { () => new TokenBudgetAgentMemory(100) },
        { () => new SummarizingAgentMemory(new ScriptedChatModel(), 100) },
        { () => JsonFileAgentMemory.Create(_unusedLogPath) },
    };

    [Theory]
    [MemberData(nameof(Memories))]
    public async Task AppendAsync_NullMessage_ThrowsArgumentNullException(Func<IAgentMemory> factory)
    {
        IAgentMemory memory = factory();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => memory.AppendAsync(null!));

        Assert.Equal("message", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(Memories))]
    public async Task RecallAsync_NullQuery_ThrowsArgumentNullException(Func<IAgentMemory> factory)
    {
        IAgentMemory memory = factory();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => memory.RecallAsync(null!));

        Assert.Equal("query", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(Memories))]
    public async Task RecallAsync_EmptyQuery_ReturnsWindow(Func<IAgentMemory> factory)
    {
        IAgentMemory memory = factory();

        IReadOnlyList<ChatMessage> window = await memory.RecallAsync(string.Empty);

        Assert.NotNull(window);
    }

    [Fact]
    public void SlidingWindowAgentMemory_MaxMessagesZero_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowAgentMemory(0));

        Assert.Equal("maxMessages", ex.ParamName);
    }

    [Fact]
    public void TokenBudgetAgentMemory_MaxTokensZero_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudgetAgentMemory(0));

        Assert.Equal("maxTokens", ex.ParamName);
    }

    [Fact]
    public void SummarizingAgentMemory_NullSummarizer_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new SummarizingAgentMemory(null!, 100));

        Assert.Equal("summarizer", ex.ParamName);
    }

    [Fact]
    public void SummarizingAgentMemory_MaxTokensZero_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SummarizingAgentMemory(new ScriptedChatModel(), 0));

        Assert.Equal("maxTokens", ex.ParamName);
    }

    [Fact]
    public void SummarizingAgentMemory_NegativeKeepRecent_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SummarizingAgentMemory(new ScriptedChatModel(), 100, keepRecentMessages: -1));

        Assert.Equal("keepRecentMessages", ex.ParamName);
    }

    [Fact]
    public void SummarizingAgentMemory_KeepRecentZero_Allowed()
    {
        var memory = new SummarizingAgentMemory(new ScriptedChatModel(), 100, keepRecentMessages: 0);

        Assert.Equal(0, memory.RecentCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SummarizingAgentMemory_EmptyOrWhitespaceSummaryPrompt_ThrowsArgumentException(string summaryPrompt)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SummarizingAgentMemory(new ScriptedChatModel(), 100, summaryPrompt: summaryPrompt));

        Assert.Equal("summaryPrompt", ex.ParamName);
    }

    [Fact]
    public async Task SummarizingAgentMemory_NullSummaryPrompt_SendsDefaultPrompt()
    {
        var summarizer = new ScriptedChatModel().Answer("summary");
        var memory = new SummarizingAgentMemory(summarizer, maxTokens: 1, keepRecentMessages: 1, summaryPrompt: null);

        await memory.AppendAsync(ChatMessage.User("first message"));
        await memory.AppendAsync(ChatMessage.User("second message"));

        Assert.Equal(SummarizingAgentMemory.DefaultSummaryPrompt, Assert.Single(summarizer.Prompts)[0].Content);
    }

    [Fact]
    public void JsonFileAgentMemory_NullPath_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => JsonFileAgentMemory.Create(null!));

        Assert.Equal("filePath", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void JsonFileAgentMemory_EmptyOrWhitespacePath_ThrowsArgumentException(string filePath)
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonFileAgentMemory.Create(filePath));

        Assert.Equal("filePath", ex.ParamName);
    }

    [Fact]
    public void JsonFileAgentMemory_PathWithInvalidCharacter_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonFileAgentMemory.Create("bad\0name.jsonl"));

        Assert.Equal("filePath", ex.ParamName);
    }

    [Fact]
    public void ApproximateTokenCounter_NullMessage_ThrowsArgumentNullException()
    {
        var counter = new ApproximateTokenCounter();

        var ex = Assert.Throws<ArgumentNullException>(() => counter.Count(null!));

        Assert.Equal("message", ex.ParamName);
    }
}
