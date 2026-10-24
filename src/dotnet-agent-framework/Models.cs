using System.Text.Json;

namespace DotnetAgentFramework;

/// <summary>
/// Role of a participant in a conversation.
/// </summary>
public enum MessageRole
{
    /// <summary>System-level instructions that shape agent behaviour.</summary>
    System,

    /// <summary>Input from the end user.</summary>
    User,

    /// <summary>Output produced by the language model.</summary>
    Assistant,

    /// <summary>Result of a tool invocation fed back to the model.</summary>
    Tool,
}

/// <summary>
/// A single message in an agent conversation.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Content">Text content. May be empty when the message carries only tool calls.</param>
/// <param name="ToolCalls">Tool invocations requested by the assistant, if any.</param>
/// <param name="ToolCallId">For <see cref="MessageRole.Tool"/> messages, the id of the call this message answers.</param>
public sealed record ChatMessage(
    MessageRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    /// <summary>Creates a system message.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static ChatMessage System(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new(MessageRole.System, content);
    }

    /// <summary>Creates a user message.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static ChatMessage User(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new(MessageRole.User, content);
    }

    /// <summary>Creates an assistant message.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCall>? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new(MessageRole.Assistant, content, toolCalls);
    }

    /// <summary>Creates a tool result message answering the given call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="toolCallId"/> or <paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="toolCallId"/> is empty or whitespace.</exception>
    public static ChatMessage ToolResult(string toolCallId, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(content);

        return new(MessageRole.Tool, content, ToolCallId: toolCallId);
    }

    /// <summary>
    /// Checks that a message is internally consistent. Use it on messages that come from outside the
    /// framework, such as model output, before they enter memory.
    /// </summary>
    /// <remarks>
    /// Rules: a <see cref="MessageRole.Tool"/> message must have a non-whitespace <see cref="ToolCallId"/>;
    /// no other role may set <see cref="ToolCallId"/>; only <see cref="MessageRole.Assistant"/> messages
    /// may carry <see cref="ToolCalls"/>.
    /// </remarks>
    /// <param name="message">Message to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> breaks one of the rules above.</exception>
    public static void Validate(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Role == MessageRole.Tool)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new ArgumentException("Tool messages must have a non-whitespace ToolCallId.", nameof(message));
            }
        }
        else if (message.ToolCallId is not null)
        {
            throw new ArgumentException(
                $"Only Tool messages may set ToolCallId, but a {message.Role} message did.",
                nameof(message));
        }

        if (message.Role != MessageRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            throw new ArgumentException(
                $"Only Assistant messages may carry ToolCalls, but a {message.Role} message did.",
                nameof(message));
        }
    }
}

/// <summary>
/// A request from the model to invoke a tool.
/// </summary>
/// <param name="Id">Provider-assigned identifier used to correlate the result.</param>
/// <param name="Name">Name of the tool to invoke.</param>
/// <param name="Arguments">Arguments as a JSON object.</param>
public sealed record ToolCall(string Id, string Name, JsonElement Arguments)
{
    /// <summary>
    /// Creates a validated tool call. Use this for calls built in code; the record constructor remains
    /// available for deserialization.
    /// </summary>
    /// <param name="id">Provider-assigned identifier. Must not be empty or whitespace.</param>
    /// <param name="name">Name of the tool to invoke. Must not be empty or whitespace.</param>
    /// <param name="arguments">Arguments as a JSON object.</param>
    /// <returns>The tool call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="name"/> is empty or whitespace, or <paramref name="arguments"/>
    /// is not a JSON object.
    /// </exception>
    public static ToolCall Create(string id, string name, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Tool call arguments must be a JSON object, but were {arguments.ValueKind}.",
                nameof(arguments));
        }

        return new(id, name, arguments);
    }
}

/// <summary>
/// Describes a tool's name, purpose and argument schema so the model can decide when to call it.
/// </summary>
/// <param name="Name">Unique tool name (snake_case recommended).</param>
/// <param name="Description">Natural-language description shown to the model.</param>
/// <param name="ParametersSchema">JSON Schema for the arguments object.</param>
public sealed record ToolDefinition(string Name, string Description, JsonElement ParametersSchema)
{
    /// <summary>
    /// Creates a validated tool definition. Use this for definitions built in code; the record constructor
    /// remains available for deserialization.
    /// </summary>
    /// <param name="name">Unique tool name. Must not be empty or whitespace.</param>
    /// <param name="description">Natural-language description shown to the model.</param>
    /// <param name="schema">JSON Schema for the arguments. Must be a JSON object.</param>
    /// <returns>The tool definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace, or <paramref name="schema"/> is not a JSON object.</exception>
    public static ToolDefinition Create(string name, string description, JsonElement schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Tool parameters schema must be a JSON object, but was {schema.ValueKind}.",
                nameof(schema));
        }

        return new(name, description, schema);
    }
}

/// <summary>
/// Token usage reported by the model provider for a single completion.
/// </summary>
/// <param name="InputTokens">Tokens consumed by the prompt.</param>
/// <param name="OutputTokens">Tokens generated by the model.</param>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Total tokens for this completion.</summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>Adds two usage records together.</summary>
    public static TokenUsage operator +(TokenUsage left, TokenUsage right)
        => new(left.InputTokens + right.InputTokens, left.OutputTokens + right.OutputTokens);
}

/// <summary>
/// Result of a single model completion.
/// </summary>
/// <param name="Message">The assistant message produced.</param>
/// <param name="Usage">Token usage for this call, if reported.</param>
public sealed record ChatCompletion(ChatMessage Message, TokenUsage? Usage = null);

/// <summary>
/// Final outcome of an agent run.
/// </summary>
/// <param name="FinalAnswer">The last assistant message content.</param>
/// <param name="Steps">Number of reasoning iterations performed.</param>
/// <param name="Transcript">Every message exchanged during the run, in order.</param>
/// <param name="Usage">Aggregate token usage across all completions.</param>
public sealed record AgentResult(
    string FinalAnswer,
    int Steps,
    IReadOnlyList<ChatMessage> Transcript,
    TokenUsage Usage);
