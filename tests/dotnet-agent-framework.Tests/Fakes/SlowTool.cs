using System.Text.Json;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Waits for <see cref="Delay"/> honouring its cancellation token, then returns <c>slow-done</c>.
/// Records whether the token was cancelled while it was waiting.
/// </summary>
internal sealed class SlowTool : ITool
{
    private volatile bool _wasCancelled;

    /// <summary>Creates a tool that takes <paramref name="delay"/> to finish.</summary>
    /// <param name="delay">How long the invocation waits when it is not cancelled.</param>
    public SlowTool(TimeSpan delay)
    {
        Delay = delay;
    }

    /// <summary>How long the invocation waits when it is not cancelled.</summary>
    public TimeSpan Delay { get; }

    /// <summary>True once an invocation observed its cancellation token being cancelled.</summary>
    public bool WasCancelled => _wasCancelled;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "slow",
        "Takes a while to finish.",
        JsonSerializer.SerializeToElement(new { type = "object" }));

    /// <inheritdoc />
    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _wasCancelled = true;
            throw;
        }

        return "slow-done";
    }
}
