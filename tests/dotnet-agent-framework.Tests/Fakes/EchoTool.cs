using System.Text.Json;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>Arguments for <see cref="EchoTool"/>.</summary>
internal sealed record EchoArgs(string Text);

/// <summary>
/// Returns its input prefixed with <c>echo:</c>. The schema is hand-written so tests do not depend on
/// JSON Schema generation.
/// </summary>
internal sealed class EchoTool : Tool<EchoArgs>
{
    /// <inheritdoc />
    public override string Name => "echo";

    /// <inheritdoc />
    public override string Description => "Echoes the given text back.";

    /// <inheritdoc />
    protected override Task<string> InvokeAsync(EchoArgs args, CancellationToken cancellationToken)
        => Task.FromResult("echo:" + args.Text);

    /// <inheritdoc />
    protected override JsonElement BuildSchema()
        => JsonSerializer.SerializeToElement(new { type = "object" });
}
