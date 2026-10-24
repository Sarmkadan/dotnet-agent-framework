using System.Runtime.CompilerServices;
using System.Text;

namespace DotnetAgentFramework;

/// <summary>
/// Configuration for an <see cref="Agent"/> run loop.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// System prompt prepended to every conversation. <see langword="null"/> means no system message.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Upper bound on model round-trips per <see cref="Agent.RunAsync"/>. Each iteration is one
    /// completion plus the execution of any tool calls it requests. Prevents runaway loops.
    /// </summary>
    public int MaxSteps { get; set; } = 10;

    /// <summary>
    /// When <see langword="true"/>, the tool calls requested in a single completion run concurrently.
    /// Results are still appended to memory and the transcript in the same order as the calls the
    /// model requested, and only after every call in the batch has finished, so each tool result
    /// follows its assistant message in order and memory never holds a partial step. When
    /// <see langword="false"/>, calls run one after another in request order. A single call always
    /// runs inline regardless of this setting. Tool failures and timeouts are returned as results;
    /// only caller cancellation propagates.
    /// </summary>
    public bool ParallelToolCalls { get; set; } = true;

    /// <summary>
    /// Per-tool-invocation timeout. <see langword="null"/> disables the timeout.
    /// </summary>
    public TimeSpan? ToolTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checks the loop settings and throws for the first rule that is broken.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MaxSteps"/> is less than 1, or <see cref="ToolTimeout"/> is set to a non-positive value.
    /// </exception>
    public void Validate()
    {
        // The name is "options" because that is the parameter callers pass these through (see Agent's constructor).
        if (MaxSteps < 1)
        {
            throw new ArgumentOutOfRangeException("options", MaxSteps, "MaxSteps must be at least 1.");
        }

        if (ToolTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                "options",
                timeout,
                "ToolTimeout must be positive when set. Use null to disable the timeout.");
        }
    }
}

/// <summary>
/// Orchestrates the reason-act loop: send the conversation to the model, execute any tools it
/// requests, append the results, and repeat until the model answers without tool calls or
/// <see cref="AgentOptions.MaxSteps"/> is reached.
/// </summary>
public sealed class Agent
{
    private const string ToolErrorPrefix = "Error: ";

    private readonly IChatModel _model;
    private readonly IAgentMemory _memory;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly AgentOptions _options;
    private readonly IReadOnlyList<ToolDefinition> _toolDefinitions;

    /// <summary>
    /// Creates an agent.
    /// </summary>
    /// <param name="model">Language model used for reasoning.</param>
    /// <param name="tools">Tools available to the model. Names must be unique.</param>
    /// <param name="memory">Conversation memory. Defaults to <see cref="InMemoryAgentMemory"/>.</param>
    /// <param name="options">Loop configuration. Defaults to <see cref="AgentOptions"/> defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="tools"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="tools"/> contains a <see langword="null"/> entry, a tool whose name is <see langword="null"/>
    /// or whitespace, or two tools that share the same name.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AgentOptions.MaxSteps"/> is less than 1, or <see cref="AgentOptions.ToolTimeout"/> is set to a
    /// non-positive value.
    /// </exception>
    public Agent(
        IChatModel model,
        IEnumerable<ITool> tools,
        IAgentMemory? memory = null,
        AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);

        options ??= new AgentOptions();
        options.Validate();

        var toolList = tools.ToList();
        var toolsByName = new Dictionary<string, ITool>(toolList.Count, StringComparer.Ordinal);
        var toolDefinitions = new List<ToolDefinition>(toolList.Count);
        foreach (var tool in toolList)
        {
            if (tool is null)
            {
                throw new ArgumentException("Tools collection contains a null entry.", nameof(tools));
            }

            var definition = tool.Definition;
            var name = definition.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    $"Tool of type '{tool.GetType().FullName}' has a null or whitespace name.",
                    nameof(tools));
            }

            if (!toolsByName.TryAdd(name, tool))
            {
                throw new ArgumentException($"Duplicate tool name '{name}'.", nameof(tools));
            }

            toolDefinitions.Add(definition);
        }

        _model = model;
        _memory = memory ?? new InMemoryAgentMemory();
        _options = options;
        _tools = toolsByName;
        _toolDefinitions = toolDefinitions;
    }

    /// <summary>
    /// Model this agent reasons with.
    /// </summary>
    public IChatModel Model => _model;

    /// <summary>
    /// Tools registered on this agent, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, ITool> Tools => _tools;

    /// <summary>
    /// Runs the agent on a user input until it produces a final answer.
    /// </summary>
    /// <param name="input">User message that starts (or continues) the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final answer, transcript and aggregate usage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is empty or whitespace.</exception>
    /// <exception cref="AgentStepLimitExceededException">
    /// The model kept requesting tools after <see cref="AgentOptions.MaxSteps"/> iterations.
    /// </exception>
    public async Task<AgentResult> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var transcript = new List<ChatMessage>();
        var usage = default(TokenUsage);

        var userMessage = ChatMessage.User(input);
        await AppendAsync(userMessage, transcript, cancellationToken).ConfigureAwait(false);

        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var messages = await BuildPromptAsync(input, cancellationToken).ConfigureAwait(false);

            var completion = await _model.CompleteAsync(messages, _toolDefinitions, cancellationToken)
                .ConfigureAwait(false);
            usage += completion.Usage ?? default;

            var assistant = ValidateModelMessage(completion.Message);
            await AppendAsync(assistant, transcript, cancellationToken).ConfigureAwait(false);

            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
            {
                return new AgentResult(assistant.Content, step, transcript, usage);
            }

            var results = await ExecuteToolCallsAsync(assistant.ToolCalls, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                await AppendAsync(result, transcript, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AgentStepLimitExceededException(_options.MaxSteps);
    }

    /// <summary>
    /// Runs the agent and streams the final answer's text as it is generated. Intermediate tool
    /// steps are executed silently before the stream starts.
    /// </summary>
    /// <remarks>
    /// Deciding whether to call a tool requires the complete, non-streaming response, so every
    /// step is first made with <see cref="IChatModel.CompleteAsync"/>. Once a step comes back
    /// without tool calls, its content is discarded and the same prompt is sent again through
    /// <see cref="IChatModel.StreamAsync"/> so callers receive the answer token by token. The
    /// answer is appended to memory only after the stream completes; if the caller stops
    /// enumerating early or the token is cancelled, nothing is recorded for that answer.
    /// Input is validated when this method is called, not when the returned sequence is first enumerated.
    /// </remarks>
    /// <param name="input">User message that starts (or continues) the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text deltas of the final answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is empty or whitespace.</exception>
    /// <exception cref="AgentStepLimitExceededException">
    /// Raised while enumerating: the model kept requesting tools after <see cref="AgentOptions.MaxSteps"/> iterations.
    /// </exception>
    public IAsyncEnumerable<string> StreamAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        return StreamCoreAsync(input, cancellationToken);
    }

    private async IAsyncEnumerable<string> StreamCoreAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _memory.AppendAsync(ChatMessage.User(input), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChatMessage>? finalPrompt = null;
        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var messages = await BuildPromptAsync(input, cancellationToken).ConfigureAwait(false);

            var completion = await _model.CompleteAsync(messages, _toolDefinitions, cancellationToken)
                .ConfigureAwait(false);

            var assistant = ValidateModelMessage(completion.Message);
            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
            {
                finalPrompt = messages;
                break;
            }

            await _memory.AppendAsync(assistant, cancellationToken).ConfigureAwait(false);

            var results = await ExecuteToolCallsAsync(assistant.ToolCalls, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                await _memory.AppendAsync(result, cancellationToken).ConfigureAwait(false);
            }
        }

        if (finalPrompt is null)
        {
            throw new AgentStepLimitExceededException(_options.MaxSteps);
        }

        var builder = new StringBuilder();
        await foreach (var delta in _model.StreamAsync(finalPrompt, _toolDefinitions, cancellationToken)
            .ConfigureAwait(false))
        {
            builder.Append(delta);
            yield return delta;
        }

        await _memory.AppendAsync(ChatMessage.Assistant(builder.ToString()), cancellationToken).ConfigureAwait(false);
    }

    private ChatMessage ValidateModelMessage(ChatMessage message)
    {
        try
        {
            ChatMessage.Validate(message);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Model '{_model.ModelId}' returned an invalid message: {ex.Message}",
                ex);
        }

        return message;
    }

    private async Task AppendAsync(ChatMessage message, List<ChatMessage> transcript, CancellationToken cancellationToken)
    {
        await _memory.AppendAsync(message, cancellationToken).ConfigureAwait(false);
        transcript.Add(message);
    }

    private async Task<IReadOnlyList<ChatMessage>> BuildPromptAsync(string input, CancellationToken cancellationToken)
    {
        var history = await _memory.RecallAsync(input, cancellationToken).ConfigureAwait(false);
        return WithSystemPrompt(history);
    }

    private IReadOnlyList<ChatMessage> WithSystemPrompt(IReadOnlyList<ChatMessage> history)
    {
        if (_options.SystemPrompt is null || (history.Count > 0 && history[0].Role == MessageRole.System))
        {
            return history;
        }

        var messages = new List<ChatMessage>(history.Count + 1) { ChatMessage.System(_options.SystemPrompt) };
        messages.AddRange(history);
        return messages;
    }

    private async Task<IReadOnlyList<ChatMessage>> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> calls,
        CancellationToken cancellationToken)
    {
        if (!_options.ParallelToolCalls || calls.Count <= 1)
        {
            var sequential = new List<ChatMessage>(calls.Count);
            foreach (var call in calls)
            {
                sequential.Add(await ExecuteToolCallAsync(call, cancellationToken).ConfigureAwait(false));
            }

            return sequential;
        }

        // Task.WhenAll preserves input order in its result, so each tool message lines up with its call.
        var tasks = calls.Select(call => ExecuteToolCallAsync(call, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ChatMessage> ExecuteToolCallAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return ChatMessage.ToolResult(
                call.Id,
                $"{ToolErrorPrefix}unknown tool '{call.Name}'. Available tools: {string.Join(", ", _tools.Keys)}");
        }

        var timeout = _options.ToolTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutSource.CancelAfter(limit);
        }

        try
        {
            var result = await tool.InvokeAsync(call.Arguments, timeoutSource.Token).ConfigureAwait(false);
            return ChatMessage.ToolResult(call.Id, result);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ChatMessage.ToolResult(
                call.Id,
                $"{ToolErrorPrefix}tool '{call.Name}' timed out after {timeout.GetValueOrDefault().TotalSeconds:0.#}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ChatMessage.ToolResult(
                call.Id,
                $"{ToolErrorPrefix}tool '{call.Name}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// Thrown when an agent run hits <see cref="AgentOptions.MaxSteps"/> without a final answer.
/// </summary>
public sealed class AgentStepLimitExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="maxSteps">The limit that was exceeded. Must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSteps"/> is less than 1.</exception>
    public AgentStepLimitExceededException(int maxSteps)
        : base($"Agent exceeded the maximum of {maxSteps} reasoning steps without producing a final answer.")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

        MaxSteps = maxSteps;
    }

    /// <summary>Creates the exception with no message.</summary>
    public AgentStepLimitExceededException()
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public AgentStepLimitExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public AgentStepLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The step limit that was exceeded.</summary>
    public int MaxSteps { get; }
}
