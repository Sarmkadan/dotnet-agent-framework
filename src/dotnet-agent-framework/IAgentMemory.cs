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
public sealed class InMemoryAgentMemory : IAgentMemory
{
    /// <inheritdoc />
    public Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
