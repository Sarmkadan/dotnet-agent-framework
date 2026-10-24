using System.Globalization;

namespace DotnetAgentFramework;

/// <summary>
/// An <see cref="IAgentObserver"/> that writes one line per event to a <see cref="TextWriter"/>, for example:
/// <c>[agent] step 2: tool get_weather completed in 12 ms</c>.
/// </summary>
/// <remarks>
/// Writes are serialized on an internal lock, so lines from parallel tool calls never interleave.
/// Line breaks inside the run input or an exception message are replaced with spaces to keep one event per line.
/// </remarks>
public sealed class ConsoleAgentObserver : IAgentObserver
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    /// <summary>Creates an observer that writes to <paramref name="writer"/>.</summary>
    /// <param name="writer">Destination for the event lines. Defaults to <see cref="Console.Out"/>.</param>
    public ConsoleAgentObserver(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    /// <inheritdoc />
    public void OnRunStarted(string input)
        => WriteLine(FormattableString.Invariant($"[agent] run started: {Flatten(input)}"));

    /// <inheritdoc />
    public void OnStepStarted(int stepNumber, IReadOnlyList<ChatMessage> prompt)
        => WriteLine(FormattableString.Invariant($"[agent] step {stepNumber}: started with {prompt.Count} message(s)"));

    /// <inheritdoc />
    public void OnCompletionReceived(int stepNumber, ChatCompletion completion)
    {
        var toolCalls = completion.Message.ToolCalls?.Count ?? 0;
        var line = FormattableString.Invariant($"[agent] step {stepNumber}: completion received");
        if (toolCalls > 0)
        {
            line += FormattableString.Invariant($", {toolCalls} tool call(s)");
        }

        if (completion.Usage is { } usage)
        {
            line += FormattableString.Invariant($", {usage.InputTokens} input / {usage.OutputTokens} output tokens");
        }

        WriteLine(line);
    }

    /// <inheritdoc />
    public void OnToolStarted(int stepNumber, ToolCall toolCall)
        => WriteLine(FormattableString.Invariant($"[agent] step {stepNumber}: tool {toolCall.Name} started"));

    /// <inheritdoc />
    public void OnToolCompleted(int stepNumber, ToolCall toolCall, string result, TimeSpan elapsed)
        => WriteLine(FormattableString.Invariant(
            $"[agent] step {stepNumber}: tool {toolCall.Name} completed in {elapsed.TotalMilliseconds:0} ms"));

    /// <inheritdoc />
    public void OnRunCompleted(AgentResult result)
        => WriteLine(FormattableString.Invariant(
            $"[agent] run completed: {result.Steps} step(s), {result.Usage.InputTokens} input / {result.Usage.OutputTokens} output tokens"));

    /// <inheritdoc />
    public void OnRunFailed(Exception exception)
        => WriteLine(FormattableString.Invariant(
            $"[agent] run failed: {exception.GetType().Name}: {Flatten(exception.Message)}"));

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private static string Flatten(string text) => text.ReplaceLineEndings(" ");
}
