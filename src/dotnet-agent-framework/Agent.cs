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
    /// When <see langword="true"/>, independent tool calls from a single completion run concurrently.
    /// </summary>
    public bool ParallelToolCalls { get; set; } = true;

    /// <summary>
    /// Per-tool-invocation timeout. <see langword="null"/> disables the timeout.
    /// </summary>
    public TimeSpan? ToolTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Orchestrates the reason-act loop: send the conversation to the model, execute any tools it
/// requests, append the results, and repeat until the model answers without tool calls or
/// <see cref="AgentOptions.MaxSteps"/> is reached.
/// </summary>
public sealed class Agent
{
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
    /// <exception cref="ArgumentException">Two tools share the same name.</exception>
    public Agent(
        IChatModel model,
        IEnumerable<ITool> tools,
        IAgentMemory? memory = null,
        AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);

        _model = model;
        _memory = memory ?? new InMemoryAgentMemory();
        _options = options ?? new AgentOptions();
        _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
        _toolDefinitions = _tools.Values.Select(t => t.Definition).ToList();
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
    /// <exception cref="AgentStepLimitExceededException">
    /// The model kept requesting tools after <see cref="AgentOptions.MaxSteps"/> iterations.
    /// </exception>
    public async Task<AgentResult> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var transcript = new List<ChatMessage>();
        var usage = default(TokenUsage);

        var userMessage = ChatMessage.User(input);
        await AppendAsync(userMessage, transcript, cancellationToken).ConfigureAwait(false);

        for (var step = 1; step <= _options.MaxSteps; step++)
        {
            var history = await _memory.RecallAsync(input, cancellationToken).ConfigureAwait(false);
            var messages = WithSystemPrompt(history);

            var completion = await _model.CompleteAsync(messages, _toolDefinitions, cancellationToken)
                .ConfigureAwait(false);
            usage += completion.Usage ?? default;

            var assistant = completion.Message;
            await AppendAsync(assistant, transcript, cancellationToken).ConfigureAwait(false);

            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
            {
                return new AgentResult(assistant.Content, step, transcript, usage);
            }

            foreach (var call in assistant.ToolCalls)
            {
                var result = await ExecuteToolCallAsync(call, cancellationToken).ConfigureAwait(false);
                await AppendAsync(result, transcript, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AgentStepLimitExceededException(_options.MaxSteps);
    }

    /// <summary>
    /// Runs the agent and streams the final answer's text as it is generated. Intermediate tool
    /// steps are executed silently before the stream starts.
    /// </summary>
    /// <param name="input">User message that starts (or continues) the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text deltas of the final answer.</returns>
    public IAsyncEnumerable<string> StreamAsync(string input, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    private async Task AppendAsync(ChatMessage message, List<ChatMessage> transcript, CancellationToken cancellationToken)
    {
        await _memory.AppendAsync(message, cancellationToken).ConfigureAwait(false);
        transcript.Add(message);
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

    private async Task<ChatMessage> ExecuteToolCallAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var tool = _tools[call.Name];
        var result = await tool.InvokeAsync(call.Arguments, cancellationToken).ConfigureAwait(false);
        return ChatMessage.ToolResult(call.Id, result);
    }
}

/// <summary>
/// Thrown when an agent run hits <see cref="AgentOptions.MaxSteps"/> without a final answer.
/// </summary>
public sealed class AgentStepLimitExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="maxSteps">The limit that was exceeded.</param>
    public AgentStepLimitExceededException(int maxSteps)
        : base($"Agent exceeded the maximum of {maxSteps} reasoning steps without producing a final answer.")
    {
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
