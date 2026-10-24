using System.Text.Json;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Tool whose invocation always faults with <see cref="InvalidOperationException"/> ("boom").
/// </summary>
internal sealed class ThrowingTool : ITool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "throwing",
        "Always fails.",
        JsonSerializer.SerializeToElement(new { type = "object" }));

    /// <inheritdoc />
    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException("boom"));
}
