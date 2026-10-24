namespace DotnetAgentFramework;

/// <summary>
/// Shared cut logic for memories that keep only a suffix of the history and must return a valid conversation.
/// </summary>
internal static class ConversationWindow
{
    /// <summary>
    /// Returns the suffix of <paramref name="history"/> that starts at <paramref name="startIndex"/>, moved so that it
    /// does not begin with a <see cref="MessageRole.Tool"/> message whose <see cref="MessageRole.Assistant"/> request
    /// lies outside the suffix.
    /// </summary>
    /// <remarks>
    /// If the start lands on a tool result, the start moves backwards to the assistant message whose
    /// <see cref="ChatMessage.ToolCalls"/> contain the result's <see cref="ChatMessage.ToolCallId"/>. A tool result
    /// whose request is not in the history at all is dropped instead, so the start moves forwards past it.
    /// The returned suffix can therefore be longer or shorter than <c>history.Count - startIndex</c>.
    /// </remarks>
    /// <param name="history">Full stored history in chronological order.</param>
    /// <param name="startIndex">Index of the first message the caller wants to keep.</param>
    /// <returns>A copy of the aligned suffix in chronological order. Empty only if the cut drops every message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="startIndex"/> is negative or greater than <c>history.Count</c>.
    /// </exception>
    internal static IReadOnlyList<ChatMessage> TakeSuffixPreservingToolPairs(
        IReadOnlyList<ChatMessage> history,
        int startIndex)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, history.Count);

        int start = AlignToToolBoundary(history, startIndex);

        return history.Skip(start).ToArray();
    }

    /// <summary>
    /// Moves <paramref name="start"/> so that it does not point at a tool result whose request is outside the window.
    /// </summary>
    private static int AlignToToolBoundary(IReadOnlyList<ChatMessage> history, int start)
    {
        while (start < history.Count && history[start].Role == MessageRole.Tool)
        {
            int owner = FindToolCallOwner(history, start, history[start].ToolCallId);
            if (owner >= 0)
            {
                return owner;
            }

            // The result has no matching request in the history. Including it would be invalid, so drop it.
            start++;
        }

        return start;
    }

    private static int FindToolCallOwner(IReadOnlyList<ChatMessage> history, int before, string? toolCallId)
    {
        if (toolCallId is null)
        {
            return -1;
        }

        for (int i = before - 1; i >= 0; i--)
        {
            ChatMessage candidate = history[i];

            if (candidate.Role == MessageRole.Assistant
                && candidate.ToolCalls is { } calls
                && calls.Any(call => call.Id == toolCallId))
            {
                return i;
            }
        }

        return -1;
    }
}
