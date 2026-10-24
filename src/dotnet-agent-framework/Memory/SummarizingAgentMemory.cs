using System.Text;
using System.Text.Json;

namespace DotnetAgentFramework;

/// <summary>
/// Keeps the most recent messages verbatim and folds older history into a running summary written by a model.
/// When the history grows past a token budget, the oldest messages are summarized and removed, so the conversation
/// keeps its facts and decisions without growing without bound.
/// </summary>
/// <remarks>
/// <para>
/// The window returned by <see cref="RecallAsync"/> is, in order: the pinned system message (if the first message
/// ever appended was a <see cref="MessageRole.System"/> message), a system message holding the running summary
/// (if one exists), and the verbatim recent messages.
/// </para>
/// <para>
/// Summarization runs inside <see cref="AppendAsync"/>, after the new message is stored. The budget is checked
/// against that window using <see cref="ITokenCounter"/>. When it is exceeded and more than
/// <c>keepRecentMessages</c> recent messages exist, the oldest messages are sent to the summarizer. The cut is moved
/// earlier when needed so an assistant message that requested tools stays with its tool results. Only one
/// summarization pass runs per append, so the budget can still be exceeded if the recent messages alone are too large.
/// </para>
/// <para>
/// If the summarizer throws, nothing is removed. The messages stay in memory, and the exception is rethrown as an
/// <see cref="InvalidOperationException"/> with the original as its inner exception. The append that triggered the
/// failure has still stored its message. Cancellation is not wrapped.
/// </para>
/// <para>
/// All operations are serialized by a semaphore because summarization awaits the model. The <see cref="Summary"/> and
/// <see cref="RecentCount"/> properties are not synchronized and exist for tests and diagnostics.
/// <see cref="RecallAsync"/> ignores its <c>query</c> argument.
/// </para>
/// </remarks>
// CA1001: SemaphoreSlim is disposable, but this type never touches its AvailableWaitHandle, so it holds no unmanaged
// resources and the memory does not need to be disposable. Keeps the public shape to IAgentMemory alone.
#pragma warning disable CA1001
public sealed class SummarizingAgentMemory : IAgentMemory
#pragma warning restore CA1001
{
    /// <summary>
    /// Default instruction given to the summarizer model.
    /// </summary>
    public const string DefaultSummaryPrompt =
        "Summarize the conversation so far in under 200 words. Preserve facts, decisions, user preferences, tool results and open questions. Write in third person.";

    private const string _summaryHeader = "Summary of earlier conversation:\n";

    private readonly IChatModel _summarizer;
    private readonly int _maxTokens;
    private readonly int _keepRecentMessages;
    private readonly ITokenCounter _counter;
    private readonly string _summaryPrompt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatMessage> _recent = [];
    private string? _summary;
    private ChatMessage? _systemMessage;

    /// <summary>
    /// Creates a summarizing memory.
    /// </summary>
    /// <param name="summarizer">Model used to write the summary. It is called with no tools.</param>
    /// <param name="maxTokens">
    /// Token budget for the window returned by <see cref="RecallAsync"/>, as measured by the counter. Summarization
    /// is triggered when the window exceeds it. Must be at least 1.
    /// </param>
    /// <param name="keepRecentMessages">
    /// Number of most recent messages kept verbatim after summarization. Must be zero or greater.
    /// </param>
    /// <param name="counter">Estimator used to measure messages. Defaults to <see cref="ApproximateTokenCounter"/>.</param>
    /// <param name="summaryPrompt">
    /// System instruction for the summarizer. Pass <see langword="null"/> to use <see cref="DefaultSummaryPrompt"/>.
    /// Otherwise it must not be empty or whitespace.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="summarizer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxTokens"/> is less than 1, or <paramref name="keepRecentMessages"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="summaryPrompt"/> is empty or whitespace.</exception>
    public SummarizingAgentMemory(
        IChatModel summarizer,
        int maxTokens,
        int keepRecentMessages = 6,
        ITokenCounter? counter = null,
        string? summaryPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(summarizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(keepRecentMessages);

        if (summaryPrompt is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(summaryPrompt);
        }

        _summarizer = summarizer;
        _maxTokens = maxTokens;
        _keepRecentMessages = keepRecentMessages;
        _counter = counter ?? new ApproximateTokenCounter();
        _summaryPrompt = summaryPrompt ?? DefaultSummaryPrompt;
    }

    /// <summary>
    /// Gets the running summary of messages that were removed from memory, or <see langword="null"/> if none exist yet.
    /// </summary>
    public string? Summary => _summary;

    /// <summary>
    /// Gets the number of verbatim recent messages, excluding the pinned system message and the summary.
    /// </summary>
    public int RecentCount => _recent.Count;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Summarization was needed and the summarizer failed.</exception>
    public async Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The system message is pinned outside _recent so summarization never folds it away.
            bool isFirstAppend = _systemMessage is null && _recent.Count == 0 && _summary is null;
            if (isFirstAppend && message.Role == MessageRole.System)
            {
                _systemMessage = message;
            }
            else
            {
                _recent.Add(message);
            }

            if (_recent.Count > _keepRecentMessages && CountTokens(BuildWindow()) > _maxTokens)
            {
                await SummarizeOldestAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the summary, if any, followed by the verbatim recent messages. The <paramref name="query"/> is ignored by this implementation.
    /// </summary>
    /// <param name="query">Ignored by this implementation. May be empty, but not <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A copy of the window in chronological order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildWindow();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _recent.Clear();
            _summary = null;
            _systemMessage = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SummarizeOldestAsync(CancellationToken cancellationToken)
    {
        // Keep the suffix that ConversationWindow would keep for the desired size. Its length tells us where the
        // cut lands after any move needed to keep tool round-trips together.
        int start = _recent.Count - _keepRecentMessages;
        int cut = _recent.Count - ConversationWindow.TakeSuffixPreservingToolPairs(_recent, start).Count;
        if (cut <= 0)
        {
            return;
        }

        List<ChatMessage> old = _recent.GetRange(0, cut);
        ChatMessage[] prompt =
        [
            ChatMessage.System(_summaryPrompt),
            ChatMessage.User($"Previous summary:\n{_summary ?? "(none)"}\n\nNew messages:\n{RenderMessages(old)}"),
        ];

        ChatCompletion completion;
        try
        {
            completion = await _summarizer.CompleteAsync(prompt, [], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Summarization failed", ex);
        }

        // Only remove the messages once the summary exists, so a failure above loses nothing.
        _summary = completion.Message.Content;
        _recent.RemoveRange(0, cut);
    }

    private List<ChatMessage> BuildWindow()
    {
        var window = new List<ChatMessage>(_recent.Count + 2);

        if (_systemMessage is not null)
        {
            window.Add(_systemMessage);
        }

        if (_summary is not null)
        {
            window.Add(ChatMessage.System(_summaryHeader + _summary));
        }

        window.AddRange(_recent);
        return window;
    }

    private int CountTokens(IEnumerable<ChatMessage> messages)
    {
        int total = 0;
        foreach (ChatMessage message in messages)
        {
            total += _counter.Count(message);
        }

        return total;
    }

    private static string RenderMessages(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();

        foreach (ChatMessage message in messages)
        {
            builder.Append(message.Role).Append(": ").AppendLine(message.Content);

            if (message.ToolCalls is not { } calls)
            {
                continue;
            }

            foreach (ToolCall call in calls)
            {
                // A default JsonElement has no raw text, so it renders as empty arguments rather than throwing.
                string arguments = call.Arguments.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : call.Arguments.GetRawText();

                builder.Append(message.Role).Append(" called ").Append(call.Name)
                    .Append('(').Append(arguments).AppendLine(")");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
