using System.Text.Json;

namespace DotnetAgentFramework;

/// <summary>
/// Estimates how many model tokens a message occupies. Memories use the estimate to fit history into a budget.
/// </summary>
/// <remarks>
/// Implementations should be deterministic for a given message and cheap to call, because memories may call them
/// for every stored message on every recall. Counts must be non-negative.
/// </remarks>
public interface ITokenCounter
{
    /// <summary>
    /// Estimates the number of tokens a message occupies in the prompt.
    /// </summary>
    /// <param name="message">Message to measure.</param>
    /// <returns>Estimated token count, zero or greater.</returns>
    int Count(ChatMessage message);
}

/// <summary>
/// Estimates tokens from character counts without calling a tokenizer, so it works with any provider.
/// </summary>
/// <remarks>
/// <para>
/// For each message the counter measures the characters that reach the model: <see cref="ChatMessage.Content"/>,
/// the raw JSON text of every <see cref="ToolCall.Arguments"/>, and every <see cref="ToolCall.Name"/>.
/// It then computes <c>ceil(chars / 4) + 4</c> tokens. The divisor of 4 is the rough average for English text
/// in common tokenizers. The extra 4 tokens cover per-message role and structure overhead.
/// </para>
/// <para>
/// The estimate is approximate. It can under-count code, non-Latin scripts and dense JSON, where one token often
/// covers fewer characters. Use a provider-specific <see cref="ITokenCounter"/> when the budget must be exact.
/// </para>
/// </remarks>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    private const int _charsPerToken = 4;
    private const int _messageOverheadTokens = 4;

    /// <summary>
    /// Estimates the tokens for a message as <c>ceil(chars / 4) + 4</c>, where chars covers the content,
    /// tool-call arguments and tool-call names.
    /// </summary>
    /// <param name="message">Message to measure.</param>
    /// <returns>Estimated token count, always at least 4.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public int Count(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        long chars = message.Content.Length;

        if (message.ToolCalls is { } calls)
        {
            foreach (ToolCall call in calls)
            {
                chars += call.Name.Length;

                // A default JsonElement has no raw text, so it contributes nothing rather than throwing.
                if (call.Arguments.ValueKind != JsonValueKind.Undefined)
                {
                    chars += call.Arguments.GetRawText().Length;
                }
            }
        }

        return (int)((chars + _charsPerToken - 1) / _charsPerToken) + _messageOverheadTokens;
    }
}
