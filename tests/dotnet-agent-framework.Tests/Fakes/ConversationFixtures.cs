using System.Text.Json;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Shared conversation shapes for memory tests.
/// </summary>
internal static class ConversationFixtures
{
    /// <summary>
    /// Builds <paramref name="count"/> alternating user/assistant messages whose content is "m0", "m1", ...
    /// </summary>
    internal static ChatMessage[] Alternating(int count)
    {
        var messages = new ChatMessage[count];
        for (int i = 0; i < count; i++)
        {
            messages[i] = i % 2 == 0
                ? ChatMessage.User($"m{i}")
                : ChatMessage.Assistant($"m{i}");
        }

        return messages;
    }

    /// <summary>
    /// Builds <c>[User, Assistant(calls c1, c2), Tool(c1), Tool(c2), Assistant("done")]</c>.
    /// </summary>
    internal static ChatMessage[] ToolRoundTrip()
    {
        JsonElement noArguments = JsonDocument.Parse("{}").RootElement.Clone();

        return
        [
            ChatMessage.User("weather and time?"),
            ChatMessage.Assistant(
                "",
                new[] { ToolCall.Create("c1", "weather", noArguments), ToolCall.Create("c2", "clock", noArguments) }),
            ChatMessage.ToolResult("c1", "18C"),
            ChatMessage.ToolResult("c2", "09:00"),
            ChatMessage.Assistant("done"),
        ];
    }

    /// <summary>
    /// Appends each message to <paramref name="memory"/> in order.
    /// </summary>
    internal static async Task AppendAllAsync(IAgentMemory memory, IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            await memory.AppendAsync(message);
        }
    }
}
