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
    public Task<AgentResult> RunAsync(string input, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>
    /// Runs the agent and streams the final answer's text as it is generated. Intermediate tool
    /// steps are executed silently before the stream starts.
    /// </summary>
    /// <param name="input">User message that starts (or continues) the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text deltas of the final answer.</returns>
    public IAsyncEnumerable<string> StreamAsync(string input, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
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
