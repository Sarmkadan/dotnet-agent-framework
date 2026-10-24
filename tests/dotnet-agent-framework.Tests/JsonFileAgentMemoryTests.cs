using System.Text.Json;

namespace DotnetAgentFramework.Tests;

public sealed class JsonFileAgentMemoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jfam-{Guid.NewGuid():N}");

    public JsonFileAgentMemoryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task AppendAsync_Message_WritesOneJsonLine()
    {
        string path = PathFor("log.jsonl");
        var memory = JsonFileAgentMemory.Create(path);

        await memory.AppendAsync(ChatMessage.User("hello"));
        await memory.AppendAsync(ChatMessage.Assistant("hi"));

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"role\":\"User\"", lines[0]);
        Assert.DoesNotContain("toolCalls", lines[0]);
    }

    [Fact]
    public async Task OpenAsync_ConversationWithToolCalls_RoundTripsEveryMessage()
    {
        string path = PathFor("log.jsonl");
        var arguments = JsonDocument.Parse("""{"city":"Kyiv","units":"metric"}""").RootElement.Clone();
        ChatMessage[] history =
        [
            ChatMessage.System("be brief"),
            ChatMessage.User("weather?"),
            ChatMessage.Assistant("", [new ToolCall("c1", "get_weather", arguments)]),
            ChatMessage.ToolResult("c1", "18°C"),
            ChatMessage.Assistant("It is 18°C."),
        ];
        var writer = JsonFileAgentMemory.Create(path);
        foreach (ChatMessage message in history)
        {
            await writer.AppendAsync(message);
        }

        JsonFileAgentMemory reopened = await JsonFileAgentMemory.OpenAsync(path);
        IReadOnlyList<ChatMessage> replayed = await reopened.RecallAsync("");

        // Records compare tool calls and JsonElements by reference, so compare the parts that matter.
        Assert.Equal(history.Length, replayed.Count);
        for (int i = 0; i < history.Length; i++)
        {
            Assert.Equal(history[i].Role, replayed[i].Role);
            Assert.Equal(history[i].Content, replayed[i].Content);
            Assert.Equal(history[i].ToolCallId, replayed[i].ToolCallId);
            Assert.Equal(
                history[i].ToolCalls?.Select(c => (c.Id, c.Name, c.Arguments.GetRawText())),
                replayed[i].ToolCalls?.Select(c => (c.Id, c.Name, c.Arguments.GetRawText())));
        }

        ToolCall call = Assert.Single(replayed[2].ToolCalls!);
        Assert.Equal("Kyiv", call.Arguments.GetProperty("city").GetString());
    }

    [Fact]
    public async Task OpenAsync_ExistingFile_ReplaysIntoProvidedInner()
    {
        string path = PathFor("log.jsonl");
        var writer = JsonFileAgentMemory.Create(path);
        await writer.AppendAsync(ChatMessage.User("one"));
        await writer.AppendAsync(ChatMessage.Assistant("two"));

        var inner = new InMemoryAgentMemory();
        JsonFileAgentMemory reopened = await JsonFileAgentMemory.OpenAsync(path, inner);

        Assert.Equal(2, inner.Count);
        Assert.Equal(path, reopened.FilePath);
    }

    [Fact]
    public async Task OpenAsync_MissingFile_StartsEmpty()
    {
        string path = PathFor("missing.jsonl");

        JsonFileAgentMemory memory = await JsonFileAgentMemory.OpenAsync(path);

        Assert.Empty(await memory.RecallAsync(""));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OpenAsync_BlankLines_AreSkipped()
    {
        string path = PathFor("log.jsonl");
        await File.WriteAllTextAsync(path, "\n" + JsonSerializer.Serialize(ChatMessage.User("a")) + "\n\n");

        JsonFileAgentMemory memory = await JsonFileAgentMemory.OpenAsync(path);

        Assert.Equal(["a"], (await memory.RecallAsync("")).Select(m => m.Content));
    }

    [Fact]
    public async Task OpenAsync_CorruptLine_ThrowsInvalidDataWithLineNumber()
    {
        string path = PathFor("log.jsonl");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(ChatMessage.User("ok")) + "\n{not json\n");

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => JsonFileAgentMemory.OpenAsync(path));

        Assert.Contains("Line 2", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task Create_ExistingFile_DoesNotReplay()
    {
        string path = PathFor("log.jsonl");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ChatMessage.User("old")) + "\n");

        JsonFileAgentMemory memory = JsonFileAgentMemory.Create(path);

        Assert.Empty(await memory.RecallAsync(""));
    }

    [Fact]
    public async Task ClearAsync_ClearsInnerAndTruncatesFile()
    {
        string path = PathFor("log.jsonl");
        var inner = new InMemoryAgentMemory();
        var memory = JsonFileAgentMemory.Create(path, inner);
        await memory.AppendAsync(ChatMessage.User("gone soon"));

        await memory.ClearAsync();

        Assert.Equal(0, inner.Count);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
        Assert.Empty(await (await JsonFileAgentMemory.OpenAsync(path)).RecallAsync(""));
    }
}
