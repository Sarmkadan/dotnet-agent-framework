namespace DotnetAgentFramework;

/// <summary>
/// Persists conversation state between reasoning steps and, optionally, between agent runs.
/// Implementations range from an in-process list to vector stores with semantic recall.
/// </summary>
public interface IAgentMemory
{
    /// <summary>
    /// Appends a message to memory.
    /// </summary>
    /// <param name="message">Message to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the messages that should be sent to the model for the next completion.
    /// Implementations may truncate, summarize or semantically filter the history to fit
    /// a token budget; the returned list must still be a valid conversation.
    /// </summary>
    /// <param name="query">The latest user input, used by recall-based memories to rank relevance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Messages in chronological order.</returns>
    Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all stored messages.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Simplest possible memory: an unbounded in-process list that returns the full history.
/// Suitable for short interactive sessions and tests.
/// </summary>
/// <remarks>
/// All members are thread-safe, so concurrent appends from parallel tool execution are supported.
/// <see cref="RecallAsync"/> ignores its <c>query</c> argument and always returns the full history.
/// </remarks>
public sealed class InMemoryAgentMemory : IAgentMemory
{
    private readonly Lock _gate = new();
    private readonly List<ChatMessage> _messages = [];

    /// <summary>
    /// Gets the number of messages currently stored.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _messages.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a snapshot of the full history. The <paramref name="query"/> is ignored by this implementation.
    /// The returned list is a copy, so later appends or clears do not affect it.
    /// </summary>
    /// <param name="query">Ignored by this implementation. May be empty, but not <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A copy of all stored messages in chronological order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<ChatMessage> snapshot;

        lock (_gate)
        {
            snapshot = _messages.ToArray();
        }

        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _messages.Clear();
        }

        return Task.CompletedTask;
    }
}
