using System.Diagnostics;

namespace DotnetAgentFramework;

/// <summary>
/// Keeps the full history in process but returns only the most recent messages to the model.
/// The window is cut so that it stays a valid conversation for providers that require tool results
/// to follow the assistant message that requested them.
/// </summary>
/// <remarks>
/// <para>Invariants of the window returned by <see cref="RecallAsync"/>:</para>
/// <list type="number">
/// <item>
/// <description>
/// It never starts with a <see cref="MessageRole.Tool"/> message. If the cut lands inside a tool round-trip,
/// the start moves backwards to the <see cref="MessageRole.Assistant"/> message whose
/// <see cref="ChatMessage.ToolCalls"/> contain the orphaned <see cref="ChatMessage.ToolCallId"/>.
/// A tool result whose request is not in the history at all is dropped instead.
/// </description>
/// </item>
/// <item>
/// <description>
/// It never ends with an assistant message that requested tools whose results are missing. A suffix of the
/// history always ends with the latest stored message, so this is checked with <c>Debug.Assert</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// If the first stored message is a <see cref="MessageRole.System"/> message, it is always the first element
/// of the window, even when it falls outside the window. It occupies one of the <see cref="MaxMessages"/> slots.
/// </description>
/// </item>
/// </list>
/// <para>
/// Because rule 1 moves the start backwards, the window can be longer than <see cref="MaxMessages"/> by the
/// number of tool results that belong to the split round-trip. The cap is therefore a target, not a hard limit,
/// whenever a tool round-trip straddles the cut.
/// </para>
/// <para>
/// Example with <c>maxMessages = 2</c> and the history <c>[User "Weather?", Assistant (call c1), Tool "18°C" (c1), Assistant "18°C now"]</c>:
/// a plain suffix would start at the orphaned tool result, so the window is extended back to the assistant
/// message that made call <c>c1</c>, giving <c>[Assistant (call c1), Tool "18°C" (c1), Assistant "18°C now"]</c>.
/// </para>
/// <para>
/// All members are thread-safe. <see cref="RecallAsync"/> ignores its <c>query</c> argument.
/// </para>
/// </remarks>
public sealed class SlidingWindowAgentMemory : IAgentMemory
{
    private readonly Lock _gate = new();
    private readonly List<ChatMessage> _messages = [];

    /// <summary>
    /// Creates a sliding window memory.
    /// </summary>
    /// <param name="maxMessages">
    /// Target maximum number of messages returned by <see cref="RecallAsync"/>, including a pinned system message.
    /// Must be at least 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxMessages"/> is less than 1.</exception>
    public SlidingWindowAgentMemory(int maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        MaxMessages = maxMessages;
    }

    /// <summary>
    /// Gets the target maximum number of messages returned by <see cref="RecallAsync"/>.
    /// </summary>
    public int MaxMessages { get; }

    /// <summary>
    /// Gets the number of messages currently stored, including messages outside the window.
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
    /// Returns the sliding window over the stored history. The <paramref name="query"/> is ignored by this implementation.
    /// </summary>
    /// <param name="query">Ignored by this implementation. May be empty, but not <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A copy of the window in chronological order. The list is not affected by later appends or clears.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ChatMessage> window;

        lock (_gate)
        {
            window = BuildWindow(_messages);
        }

        return Task.FromResult(window);
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

    private IReadOnlyList<ChatMessage> BuildWindow(List<ChatMessage> history)
    {
        if (history.Count <= MaxMessages)
        {
            // Everything fits, but the cut still drops a leading tool result whose request is missing.
            return ConversationWindow.TakeSuffixPreservingToolPairs(history, 0);
        }

        // A pinned system message takes one slot, so the suffix gets the rest of the budget.
        bool pinSystem = history[0].Role == MessageRole.System;
        int budget = pinSystem ? MaxMessages - 1 : MaxMessages;
        IReadOnlyList<ChatMessage> suffix = ConversationWindow.TakeSuffixPreservingToolPairs(history, history.Count - budget);

        var window = new List<ChatMessage>(suffix.Count + 1);

        if (pinSystem)
        {
            window.Add(history[0]);
        }

        window.AddRange(suffix);

        Debug.Assert(
            window.Count == 0 || window[0].Role != MessageRole.Tool,
            "A sliding window must not start with a tool result.");

        Debug.Assert(
            window.Count == 0 || window[^1].Role != MessageRole.Assistant || window[^1].ToolCalls is not { Count: > 0 },
            "A sliding window must not end with an assistant message whose tool results are missing.");

        return window;
    }
}
