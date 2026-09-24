namespace DotnetAgentFramework;

/// <summary>
/// Abstraction over a chat-capable language model. Implementations wrap a concrete provider
/// (OpenAI, Anthropic, Azure OpenAI, Ollama, ...) and translate between provider wire formats
/// and the framework's <see cref="ChatMessage"/> model.
/// </summary>
public interface IChatModel
{
    /// <summary>
    /// Provider-specific model identifier (for example "gpt-4o" or "claude-sonnet-4-5").
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Produces a single completion for the given conversation, optionally allowing the model
    /// to request one or more of the supplied tools.
    /// </summary>
    /// <param name="messages">Conversation history in chronological order.</param>
    /// <param name="tools">Tools the model may call. Empty means plain text completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant message together with token usage.</returns>
    Task<ChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the text of a completion token by token. Tool calls are not streamed; when the
    /// model decides to call tools, the stream ends and the caller should fall back to
    /// <see cref="CompleteAsync"/>.
    /// </summary>
    /// <param name="messages">Conversation history in chronological order.</param>
    /// <param name="tools">Tools the model may call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of text deltas.</returns>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default);
}
