using System.Runtime.CompilerServices;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Scripted <see cref="IChatModel"/>: each <see cref="CompleteAsync"/> call consumes the next queued
/// <see cref="ChatCompletion"/>, each <see cref="StreamAsync"/> call consumes the next queued set of
/// deltas, and the prompts and tool definitions it received are recorded for assertions.
/// </summary>
internal sealed class FakeChatModel : IChatModel
{
    /// <summary>Completions returned in order, one per call to <see cref="CompleteAsync"/>.</summary>
    public Queue<ChatCompletion> Responses { get; } = new();

    /// <summary>
    /// Deltas yielded in order, one entry per call to <see cref="StreamAsync"/>. Each entry is the
    /// complete sequence for that call, so an empty array yields an empty stream.
    /// </summary>
    public Queue<string[]> StreamChunks { get; } = new();

    /// <summary>Prompts passed to <see cref="CompleteAsync"/>, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedPrompts { get; } = [];

    /// <summary>Tool definitions passed to <see cref="CompleteAsync"/>, in call order.</summary>
    public List<IReadOnlyList<ToolDefinition>> ReceivedTools { get; } = [];

    /// <summary>Prompts passed to <see cref="StreamAsync"/>, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> StreamedPrompts { get; } = [];

    /// <summary>
    /// Number of <see cref="StreamAsync"/> calls that have started enumerating. Counted when the
    /// stream is first enumerated, not when it is created.
    /// </summary>
    public int StreamCalls { get; private set; }

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
        return Task.FromResult(Dequeue(Responses, "responses"));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token is checked before every yield, so a consumer that cancels between deltas sees
    /// <see cref="OperationCanceledException"/> on its next move.
    /// </remarks>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamCalls++;
        StreamedPrompts.Add(messages.ToArray());
        var chunks = Dequeue(StreamChunks, "stream chunk sets");

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    private static T Dequeue<T>(Queue<T> queue, string what)
    {
        if (queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeChatModel has no queued {what} left. Enqueue one for every model call the test expects.");
        }

        return queue.Dequeue();
    }
}
