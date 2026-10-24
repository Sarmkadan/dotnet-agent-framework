using System.Text.Json;

namespace DotnetAgentFramework;

/// <summary>
/// Conversions between framework values and OpenAI wire-format values. Kept apart from
/// <see cref="OpenAIChatModel"/> so they can be unit-tested without an HTTP endpoint.
/// </summary>
internal static class OpenAIWireFormat
{
    /// <summary>Maps a <see cref="MessageRole"/> to the role string the chat completions API expects.</summary>
    /// <param name="role">Role to map.</param>
    /// <returns>"system", "user", "assistant" or "tool".</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined value.</exception>
    internal static string RoleName(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role."),
    };

    /// <summary>
    /// Serializes tool call arguments to the JSON string the API expects in <c>function.arguments</c>.
    /// </summary>
    /// <param name="arguments">Arguments object. An undefined element (for example from a default-constructed record) becomes "{}".</param>
    /// <returns>Raw JSON text of the arguments.</returns>
    internal static string ToArgumentsString(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();

    /// <summary>
    /// Parses the <c>function.arguments</c> string from a model response into a JSON object.
    /// </summary>
    /// <remarks>
    /// Models sometimes emit truncated or non-object JSON. Those cases return an empty object instead of
    /// throwing, so the run continues and the tool reports the missing arguments to the model as text.
    /// </remarks>
    /// <param name="arguments">Arguments string from the response. May be null or empty.</param>
    /// <returns>A cloned JSON object, never <see cref="JsonValueKind.Undefined"/>.</returns>
    internal static JsonElement ParseArguments(string? arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                using var document = JsonDocument.Parse(arguments);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Fall through to the empty object below.
            }
        }

        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}
