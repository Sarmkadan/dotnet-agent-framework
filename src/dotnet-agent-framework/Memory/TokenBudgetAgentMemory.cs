namespace DotnetAgentFramework;

/// <summary>
/// Keeps the full history in process but returns only the most recent messages that fit a token budget.
/// The window is cut so that it stays a valid conversation for providers that require tool results
/// to follow the assistant message that requested them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecallAsync"/> walks the history from the newest message backwards, adding up the
/// <see cref="ITokenCounter"/> estimates, and stops before the total would exceed <see cref="MaxTokens"/>.
/// It then applies the same tool-pair-preserving cut as <see cref="SlidingWindowAgentMemory"/>, so the window
/// never starts with an orphaned tool result.
/// </para>
/// <para>Rules for the window returned by <see cref="RecallAsync"/>:</para>
/// <list type="number">
/// <item>
/// <description>
/// If the first stored message is a <see cref="MessageRole.System"/> message, its tokens are reserved first and the
/// message is always the first element of the window, even if its cost alone exceeds <see cref="MaxTokens"/>.
/// The remaining budget is <c>MaxTokens - systemTokens</c>, which may be zero or negative.
/// </description>
/// </item>
/// <item>
/// <description>
/// If even the newest message does not fit the remaining budget, the window contains only that newest message
/// (plus the pinned system message, if any). The result is never empty for a non-empty history, so the budget
/// is a soft limit in this case. Callers that need a hard cap must check the returned messages themselves.
/// </description>
/// </item>
/// <item>
/// <description>
/// The tool-pair cut can move the start backwards to the assistant message that requested a tool result,
/// which can take the window over budget. This keeps a tool round-trip intact. The budget is therefore a target
/// across tool round-trips, not a hard limit, and the same applies when the newest message is a tool result.
/// </description>
/// </item>
/// </list>
/// <para>
/// All members are thread-safe. <see cref="RecallAsync"/> ignores its <c>query</c> argument.
/// Each recall calls the counter for the messages it considers, so counters should be cheap.
/// </para>
/// </remarks>
public sealed class TokenBudgetAgentMemory : IAgentMemory
{
    private readonly Lock _gate = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly ITokenCounter _counter;

    /// <summary>
    /// Creates a token budget memory.
    /// </summary>
    /// <param name="maxTokens">
    /// Target maximum number of tokens for the window returned by <see cref="RecallAsync"/>, as measured by the counter.
    /// Must be at least 1.
    /// </param>
    /// <param name="counter">
    /// Estimator used to measure messages. Defaults to <see cref="ApproximateTokenCounter"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTokens"/> is less than 1.</exception>
    public TokenBudgetAgentMemory(int maxTokens, ITokenCounter? counter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);

        MaxTokens = maxTokens;
        _counter = counter ?? new ApproximateTokenCounter();
    }

    /// <summary>
    /// Gets the target maximum number of tokens for the window returned by <see cref="RecallAsync"/>.
    /// </summary>
    public int MaxTokens { get; }

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
    /// Returns the most recent messages that fit the token budget, cut to a valid conversation.
    /// The <paramref name="query"/> is ignored by this implementation.
    /// </summary>
    /// <param name="query">Ignored by this implementation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A copy of the window in chronological order. The list is not affected by later appends or clears.
    /// Empty only when no messages are stored.
    /// </returns>
    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ChatMessage> window;

        lock (_gate)
        {
            window = BuildWindow(new List<ChatMessage>(_messages));
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
        if (history.Count == 0)
        {
            return history;
        }

        // A pinned system message is always included, so its tokens come off the top of the budget.
        bool pinSystem = history[0].Role == MessageRole.System;
        if (pinSystem && history.Count == 1)
        {
            // The system message is also the newest message, so it must not be added twice.
            return history;
        }

        int remaining = MaxTokens - (pinSystem ? _counter.Count(history[0]) : 0);
        int firstCandidate = pinSystem ? 1 : 0;

        // Start at the newest message. If it does not fit, the window still keeps it, so the start stays at the end.
        int nominalStart = history.Count - 1;
        int used = 0;

        for (int i = history.Count - 1; i >= firstCandidate; i--)
        {
            int cost = _counter.Count(history[i]);
            if (used + cost > remaining)
            {
                break;
            }

            used += cost;
            nominalStart = i;
        }

        IReadOnlyList<ChatMessage> suffix = ConversationWindow.TakeSuffixPreservingToolPairs(history, nominalStart);

        if (suffix.Count == 0)
        {
            // Only reachable when the newest message is an orphaned tool result, which the cut drops.
            // Keep it anyway so a non-empty history never yields an empty window.
            suffix = [history[^1]];
        }

        if (!pinSystem)
        {
            return suffix;
        }

        var window = new List<ChatMessage>(suffix.Count + 1) { history[0] };
        window.AddRange(suffix);
        return window;
    }
}
