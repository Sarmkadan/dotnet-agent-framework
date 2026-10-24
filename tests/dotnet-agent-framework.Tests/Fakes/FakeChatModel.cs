using System.Runtime.CompilerServices;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Scripted <see cref="IChatModel"/>: each call consumes the next queued <see cref="ChatCompletion"/>
/// and the prompts and tool definitions it received are recorded for assertions.
/// </summary>
internal sealed class FakeChatModel : IChatModel
{
    /// <summary>Completions returned in order, one per call to <see cref="CompleteAsync"/> or <see cref="StreamAsync"/>.</summary>
    public Queue<ChatCompletion> Responses { get; } = new();

    /// <summary>Prompts passed to <see cref="CompleteAsync"/>, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedPrompts { get; } = [];

    /// <summary>Tool definitions passed to <see cref="CompleteAsync"/>, in call order.</summary>
    public List<IReadOnlyList<ToolDefinition>> ReceivedTools { get; } = [];

    /// <inheritdoc />
    public string ModelId => "fake";

    /// <inheritdoc />
    public Task<ChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        ReceivedPrompts.Add(messages.ToArray());
        ReceivedTools.Add(tools);
        return Task.FromResult(Dequeue());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int chunkSize = 3;
        var content = Dequeue().Message.Content;

        for (var start = 0; start < content.Length; start += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return content.Substring(start, Math.Min(chunkSize, content.Length - start));
        }
    }

    private ChatCompletion Dequeue()
    {
        if (Responses.Count == 0)
        {
            throw new InvalidOperationException(
                "FakeChatModel has no queued responses left. Enqueue one for every model call the test expects.");
        }

        return Responses.Dequeue();
    }
}
