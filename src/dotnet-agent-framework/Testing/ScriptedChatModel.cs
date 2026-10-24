using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DotnetAgentFramework.Testing;

/// <summary>
/// Deterministic <see cref="IChatModel"/> for tests of code built on the framework. Responses are queued
/// up front with the fluent methods and consumed in call order, so a run always produces the same
/// transcript. Every prompt and tool list the model receives is recorded for assertions.
/// </summary>
/// <remarks>
/// Two queues back the model. <see cref="IChatModel.CompleteAsync"/> consumes the completion queue and
/// <see cref="IChatModel.StreamAsync"/> consumes the stream queue. <see cref="Agent.StreamAsync"/> calls
/// both: a completion for every step, then one stream for the final answer. <see cref="AnswerStreamed"/>
/// fills both queues, so one call covers a streamed final answer.
/// <para>
/// Instances are thread-safe. Calling a model with an empty queue throws
/// <see cref="InvalidOperationException"/>, which names the last user message so the missing script is easy to find.
/// </para>
/// </remarks>
public sealed class ScriptedChatModel : IChatModel
{
    private readonly Lock _gate = new();
    private readonly Queue<ChatCompletion> _completions = new();
    private readonly Queue<string[]> _streams = new();
    private readonly List<IReadOnlyList<ChatMessage>> _prompts = [];
    private readonly List<IReadOnlyList<ToolDefinition>> _toolDefinitions = [];
    private int _toolCallCounter;
    private int _completionCalls;
    private int _streamCalls;

    /// <summary>Creates a model with an empty script.</summary>
    /// <param name="modelId">Value returned by <see cref="ModelId"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is empty or whitespace.</exception>
    public ScriptedChatModel(string modelId = "scripted")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        ModelId = modelId;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <summary>Prompts received by <see cref="CompleteAsync"/> and <see cref="StreamAsync"/>, in call order.</summary>
    /// <remarks>Each entry is a copy taken when the call was made, so later changes to the caller's list do not show up here.</remarks>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Prompts
    {
        get
        {
            lock (_gate)
            {
                return _prompts.ToArray();
            }
        }
    }

    /// <summary>Tool definitions received by <see cref="CompleteAsync"/> and <see cref="StreamAsync"/>, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<ToolDefinition>> ToolDefinitions
    {
        get
        {
            lock (_gate)
            {
                return _toolDefinitions.ToArray();
            }
        }
    }

    /// <summary>Number of <see cref="CompleteAsync"/> calls made so far.</summary>
    public int CompletionCalls
    {
        get
        {
            lock (_gate)
            {
                return _completionCalls;
            }
        }
    }

    /// <summary>
    /// Number of <see cref="StreamAsync"/> calls that have started enumerating. Counted when the stream is first
    /// enumerated, not when it is created.
    /// </summary>
    public int StreamCalls
    {
        get
        {
            lock (_gate)
            {
                return _streamCalls;
            }
        }
    }

    /// <summary>Queues a plain assistant answer for the next <see cref="CompleteAsync"/> call.</summary>
    /// <param name="text">Assistant message content.</param>
    /// <param name="usage">Token usage reported for the completion, if any.</param>
    /// <returns>This model, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public ScriptedChatModel Answer(string text, TokenUsage? usage = null)
        => Enqueue(new ChatCompletion(ChatMessage.Assistant(text), usage));

    /// <summary>
    /// Queues an assistant completion that requests a single tool call.
    /// </summary>
    /// <param name="toolName">Name of the tool the model asks for.</param>
    /// <param name="arguments">Arguments, serialized to a JSON object.</param>
    /// <param name="callId">Id of the tool call. When <see langword="null"/>, the id is <c>call_N</c>, where N counts
    /// generated ids from 1 in this model instance.</param>
    /// <returns>This model, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> or <paramref name="callId"/> is empty or whitespace, or
    /// <paramref name="arguments"/> does not serialize to a JSON object.</exception>
    public ScriptedChatModel CallTool(string toolName, object arguments, string? callId = null)
        => Enqueue(new ChatCompletion(ChatMessage.Assistant(string.Empty, [CreateToolCall(toolName, arguments, callId)])));

    /// <summary>
    /// Queues one assistant completion that requests several tool calls in the same step. Generated ids follow
    /// the same <c>call_N</c> scheme as <see cref="CallTool"/>.
    /// </summary>
    /// <param name="calls">Tool names and arguments, in the order the model requests them.</param>
    /// <returns>This model, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="calls"/> is empty, or a call is invalid as in <see cref="CallTool"/>.</exception>
    public ScriptedChatModel CallTools(params (string Name, object Arguments)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Length == 0)
        {
            throw new ArgumentException("At least one tool call is required.", nameof(calls));
        }

        var toolCalls = calls.Select(call => CreateToolCall(call.Name, call.Arguments, callId: null)).ToArray();
        return Enqueue(new ChatCompletion(ChatMessage.Assistant(string.Empty, toolCalls)));
    }

    /// <summary>
    /// Queues a streamed final answer. The chunks are yielded by the next <see cref="StreamAsync"/> call, and
    /// their concatenation is queued as a plain answer for the next <see cref="CompleteAsync"/> call, which
    /// <see cref="Agent.StreamAsync"/> makes before it streams.
    /// </summary>
    /// <param name="chunks">Deltas in yield order. An empty list yields an empty stream.</param>
    /// <returns>This model, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> or one of its entries is <see langword="null"/>.</exception>
    public ScriptedChatModel AnswerStreamed(params string[] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(chunk => chunk is null))
        {
            throw new ArgumentNullException(nameof(chunks), "Stream chunks must not be null.");
        }

        var deltas = chunks.ToArray();
        var completion = new ChatCompletion(ChatMessage.Assistant(string.Concat(deltas)));
        lock (_gate)
        {
            _completions.Enqueue(completion);
            _streams.Enqueue(deltas);
        }

        return this;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The completion queue is empty.</exception>
    public Task<ChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _completionCalls++;
            Record(messages, tools);
            if (_completions.Count == 0)
            {
                throw Exhausted("responses", messages);
            }

            return Task.FromResult(_completions.Dequeue());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Yields each queued chunk after an <see cref="Task.Yield"/>, so consumers see the stream as asynchronous. The
    /// token is checked before each chunk, so a consumer that cancels between deltas sees
    /// <see cref="OperationCanceledException"/> on its next move.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stream queue is empty when enumeration starts.</exception>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        cancellationToken.ThrowIfCancellationRequested();

        string[] chunks;
        lock (_gate)
        {
            _streamCalls++;
            Record(messages, tools);
            if (_streams.Count == 0)
            {
                throw Exhausted("stream chunk sets", messages);
            }

            chunks = _streams.Dequeue();
        }

        foreach (var chunk in chunks)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    /// <summary>
    /// Queues a raw completion. Internal, so the framework's own tests can script shapes the fluent API cannot
    /// express, such as explicit tool call ids, orphan tool messages or non-object arguments.
    /// </summary>
    internal ScriptedChatModel Enqueue(ChatCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        lock (_gate)
        {
            _completions.Enqueue(completion);
        }

        return this;
    }

    private ToolCall CreateToolCall(string toolName, object arguments, string? callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var id = callId ?? NextCallId();
        return ToolCall.Create(id, toolName, JsonSerializer.SerializeToElement(arguments));
    }

    private string NextCallId()
    {
        lock (_gate)
        {
            return $"call_{++_toolCallCounter}";
        }
    }

    // Callers must hold _gate.
    private void Record(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
    {
        _prompts.Add(messages.ToArray());
        _toolDefinitions.Add(tools.ToArray());
    }

    private static InvalidOperationException Exhausted(string what, IReadOnlyList<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == MessageRole.User);
        var received = lastUser is null ? "(no user message)" : $"\"{lastUser.Content}\"";

        return new InvalidOperationException(
            $"ScriptedChatModel has no more scripted {what}. Prompt received: last user message {received}.");
    }
}
