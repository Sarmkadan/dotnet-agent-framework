using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework;

/// <summary>
/// Shared argument handling for typed tools: <see cref="Tool{TArgs}"/> and <see cref="DelegateTool"/>.
/// </summary>
internal static class ToolArguments
{
    // Enums are advertised as string names by JsonSchemaGenerator, so they must also be read from strings.
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Deserializes model-supplied arguments into <typeparamref name="TArgs"/>.
    /// </summary>
    /// <param name="arguments">Arguments chosen by the model.</param>
    /// <param name="args">The deserialized arguments when this returns <see langword="true"/>.</param>
    /// <param name="error">
    /// When this returns <see langword="false"/>, a description of the problem that follows
    /// <c>"Error: tool 'name' "</c> in the text returned to the model.
    /// </param>
    /// <returns><see langword="true"/> if the arguments were deserialized; otherwise <see langword="false"/>.</returns>
    public static bool TryDeserialize<TArgs>(JsonElement arguments, out TArgs? args, out string? error)
        where TArgs : class
    {
        args = null;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = $"expected a JSON object but received {arguments.ValueKind}.";
            return false;
        }

        try
        {
            args = arguments.Deserialize<TArgs>(s_jsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"received arguments that could not be deserialized: {ex.Message}";
            return false;
        }

        if (args is null)
        {
            throw new ArgumentException("Tool received null arguments.", nameof(arguments));
        }

        return true;
    }
}
