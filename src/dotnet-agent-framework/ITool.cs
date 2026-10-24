using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework;

/// <summary>
/// A capability the agent can invoke: a function with a name, a description and a JSON argument schema.
/// Implementations should be stateless or thread-safe, because a single instance may serve
/// concurrent agent runs.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Definition exposed to the model: name, description and JSON Schema for the arguments.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Executes the tool with the arguments chosen by the model.
    /// </summary>
    /// <param name="arguments">Arguments as a JSON object matching <see cref="ToolDefinition.ParametersSchema"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A string result fed back to the model. Errors that the model should be able to recover from
    /// (bad arguments, not found, ...) should be returned as text rather than thrown.
    /// </returns>
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Strongly-typed convenience base for tools. Arguments are deserialized into <typeparamref name="TArgs"/>
/// and the JSON Schema is derived from its shape.
/// </summary>
/// <typeparam name="TArgs">POCO describing the tool's arguments.</typeparam>
public abstract class Tool<TArgs> : ITool
    where TArgs : class
{
    // Enums are advertised as string names by JsonSchemaGenerator, so they must also be read from strings.
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Per closed generic type, so reflection runs once for each TArgs.
    private static readonly Lazy<JsonElement> s_schema = new(() => JsonSchemaGenerator.Generate(typeof(TArgs)));

    /// <summary>Unique tool name presented to the model.</summary>
    public abstract string Name { get; }

    /// <summary>Natural-language description presented to the model.</summary>
    public abstract string Description { get; }

    /// <inheritdoc />
    public ToolDefinition Definition => new(Name, Description, BuildSchema());

    /// <summary>
    /// Executes the tool with deserialized arguments.
    /// </summary>
    /// <param name="args">Deserialized arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result text fed back to the model.</returns>
    protected abstract Task<string> InvokeAsync(TArgs args, CancellationToken cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Arguments the model got wrong (not an object, or not deserializable into <typeparamref name="TArgs"/>)
    /// are returned as an error result text so the model can retry; they do not throw.
    /// </remarks>
    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult($"Error: tool '{Name}' expected a JSON object but received {arguments.ValueKind}.");
        }

        TArgs? args;
        try
        {
            args = arguments.Deserialize<TArgs>(s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"Error: tool '{Name}' received arguments that could not be deserialized: {ex.Message}");
        }

        return InvokeAsync(
            args ?? throw new ArgumentException($"Tool '{Name}' received null arguments.", nameof(arguments)),
            cancellationToken);
    }

    /// <summary>
    /// Builds the JSON Schema for <typeparamref name="TArgs"/>. The default implementation derives
    /// it from public properties (cached per type); override to hand-craft the schema.
    /// </summary>
    protected virtual JsonElement BuildSchema() => s_schema.Value;
}
