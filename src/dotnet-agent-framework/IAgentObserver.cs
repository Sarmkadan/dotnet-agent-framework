namespace DotnetAgentFramework;

/// <summary>
/// Receives notifications about what an agent run is doing, for logging, metrics and tracing. Attach one
/// through <see cref="AgentOptions.Observer"/>.
/// </summary>
/// <remarks>
/// Hooks are called synchronously from the run loop and must return quickly. Tool hooks can be called from
/// several threads at once when <see cref="AgentOptions.ParallelToolCalls"/> is on, so implementations must be thread-safe.
/// Exceptions thrown by a hook are swallowed by the agent and never change the outcome of a run.
/// </remarks>
public interface IAgentObserver
{
    /// <summary>A run has started with the given user input.</summary>
    /// <param name="input">User message that starts the run.</param>
    void OnRunStarted(string input);

    /// <summary>A reasoning step is about to call the model.</summary>
    /// <param name="stepNumber">One-based step number.</param>
    /// <param name="prompt">Messages sent to the model, including the system prompt if one is set.</param>
    void OnStepStarted(int stepNumber, IReadOnlyList<ChatMessage> prompt);

    /// <summary>The model returned a completion for a step. Fired before the completion is validated.</summary>
    /// <param name="stepNumber">One-based step number.</param>
    /// <param name="completion">The completion as the model returned it.</param>
    void OnCompletionReceived(int stepNumber, ChatCompletion completion);

    /// <summary>A tool call requested by the model is about to run.</summary>
    /// <param name="stepNumber">One-based step number.</param>
    /// <param name="toolCall">The tool call. Its name may not match a registered tool; the error is reported through <see cref="OnToolCompleted"/>.</param>
    void OnToolStarted(int stepNumber, ToolCall toolCall);

    /// <summary>A tool call finished, including calls that failed, timed out or named an unknown tool.</summary>
    /// <param name="stepNumber">One-based step number.</param>
    /// <param name="toolCall">The tool call.</param>
    /// <param name="result">Result text that is sent back to the model.</param>
    /// <param name="elapsed">Wall-clock time spent on the call.</param>
    void OnToolCompleted(int stepNumber, ToolCall toolCall, string result, TimeSpan elapsed);

    /// <summary>The run produced its final answer.</summary>
    /// <param name="result">The result returned to the caller.</param>
    void OnRunCompleted(AgentResult result);

    /// <summary>The run ended with an exception, such as <see cref="AgentStepLimitExceededException"/> or a model error.</summary>
    /// <param name="exception">The exception that is leaving the run.</param>
    void OnRunFailed(Exception exception);
}

/// <summary>
/// An <see cref="IAgentObserver"/> that ignores every event. Use <see cref="Instance"/> when an observer is required but nothing should be recorded.
/// </summary>
public sealed class NullAgentObserver : IAgentObserver
{
    /// <summary>Shared instance. The type has no state, so one instance serves every agent.</summary>
    public static readonly NullAgentObserver Instance = new();

    private NullAgentObserver()
    {
    }

    /// <inheritdoc />
    public void OnRunStarted(string input)
    {
    }

    /// <inheritdoc />
    public void OnStepStarted(int stepNumber, IReadOnlyList<ChatMessage> prompt)
    {
    }

    /// <inheritdoc />
    public void OnCompletionReceived(int stepNumber, ChatCompletion completion)
    {
    }

    /// <inheritdoc />
    public void OnToolStarted(int stepNumber, ToolCall toolCall)
    {
    }

    /// <inheritdoc />
    public void OnToolCompleted(int stepNumber, ToolCall toolCall, string result, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void OnRunCompleted(AgentResult result)
    {
    }

    /// <inheritdoc />
    public void OnRunFailed(Exception exception)
    {
    }
}

/// <summary>
/// An <see cref="IAgentObserver"/> that forwards each event to the delegate assigned to the matching property.
/// Events without a delegate are ignored. Set the properties in an object initializer.
/// </summary>
public sealed class DelegateAgentObserver : IAgentObserver
{
    /// <summary>Called for <see cref="IAgentObserver.OnRunStarted"/>.</summary>
    public Action<string>? RunStarted { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnStepStarted"/>.</summary>
    public Action<int, IReadOnlyList<ChatMessage>>? StepStarted { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnCompletionReceived"/>.</summary>
    public Action<int, ChatCompletion>? CompletionReceived { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnToolStarted"/>.</summary>
    public Action<int, ToolCall>? ToolStarted { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnToolCompleted"/>.</summary>
    public Action<int, ToolCall, string, TimeSpan>? ToolCompleted { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnRunCompleted"/>.</summary>
    public Action<AgentResult>? RunCompleted { get; init; }

    /// <summary>Called for <see cref="IAgentObserver.OnRunFailed"/>.</summary>
    public Action<Exception>? RunFailed { get; init; }

    /// <inheritdoc />
    public void OnRunStarted(string input) => RunStarted?.Invoke(input);

    /// <inheritdoc />
    public void OnStepStarted(int stepNumber, IReadOnlyList<ChatMessage> prompt) => StepStarted?.Invoke(stepNumber, prompt);

    /// <inheritdoc />
    public void OnCompletionReceived(int stepNumber, ChatCompletion completion) => CompletionReceived?.Invoke(stepNumber, completion);

    /// <inheritdoc />
    public void OnToolStarted(int stepNumber, ToolCall toolCall) => ToolStarted?.Invoke(stepNumber, toolCall);

    /// <inheritdoc />
    public void OnToolCompleted(int stepNumber, ToolCall toolCall, string result, TimeSpan elapsed)
        => ToolCompleted?.Invoke(stepNumber, toolCall, result, elapsed);

    /// <inheritdoc />
    public void OnRunCompleted(AgentResult result) => RunCompleted?.Invoke(result);

    /// <inheritdoc />
    public void OnRunFailed(Exception exception) => RunFailed?.Invoke(exception);
}
