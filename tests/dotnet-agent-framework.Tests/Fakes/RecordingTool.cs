using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Tool with a configurable name that records when each invocation started and ended. Each
/// invocation waits on <see cref="Gate"/>, so a test decides when (and in which order) calls complete.
/// </summary>
internal sealed class RecordingTool : ITool
{
    private readonly ConcurrentQueue<ToolInvocation> _invocations = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a recording tool with the given name.</summary>
    /// <param name="name">Tool name presented to the model.</param>
    public RecordingTool(string name)
    {
        Definition = new ToolDefinition(name, "Records its invocations.", JsonSerializer.SerializeToElement(new { type = "object" }));
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <summary>Completing this source makes the pending invocation return its value.</summary>
    public TaskCompletionSource<string> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when an invocation has entered <see cref="InvokeAsync"/>.</summary>
    public Task Started => _started.Task;

    /// <summary>Completes after an invocation has been recorded, whether it returned or was cancelled.</summary>
    public Task Completed => _completed.Task;

    /// <summary>Invocations that have finished, in completion order.</summary>
    public IReadOnlyList<ToolInvocation> Invocations => _invocations.ToArray();

    /// <inheritdoc />
    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        _started.TrySetResult();
        try
        {
            return await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _invocations.Enqueue(new ToolInvocation(start, Stopwatch.GetTimestamp()));
            _completed.TrySetResult();
        }
    }
}

/// <summary>
/// One finished invocation of a <see cref="RecordingTool"/>, as <see cref="Stopwatch"/> timestamps
/// (comparable across tools in the same process).
/// </summary>
/// <param name="StartedAt">Timestamp when the invocation began.</param>
/// <param name="EndedAt">Timestamp when the invocation finished.</param>
internal readonly record struct ToolInvocation(long StartedAt, long EndedAt);
