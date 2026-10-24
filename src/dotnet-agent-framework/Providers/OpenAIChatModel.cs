using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework;

/// <summary>
/// Adapter for OpenAI-compatible <c>POST {baseAddress}/chat/completions</c> endpoints. Covers OpenAI, Ollama,
/// vLLM and OpenRouter. The core library takes no provider SDK dependency: this class only uses
/// <see cref="HttpClient"/> and <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// This adapter is non-streaming. <see cref="StreamAsync"/> yields the whole completion as one chunk.
/// </remarks>
public sealed class OpenAIChatModel : IChatModel
{
    private const string _defaultBaseAddress = "https://api.openai.com/v1/";
    private const string _completionsPath = "chat/completions";

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    /// <summary>
    /// Creates an adapter bound to one model on one endpoint.
    /// </summary>
    /// <param name="httpClient">
    /// Client used for every request. When <see cref="HttpClient.BaseAddress"/> is null it is set to
    /// <c>https://api.openai.com/v1/</c>. Otherwise it must end with '/', so that <c>chat/completions</c>
    /// resolves beneath it instead of replacing the last segment.
    /// </param>
    /// <param name="modelId">Model identifier sent in each request, for example "gpt-4o" or "llama3.1".</param>
    /// <param name="apiKey">
    /// Sent as a Bearer token on each request. Pass null for servers without authentication, such as a local Ollama.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="modelId"/> or <paramref name="apiKey"/> is empty or whitespace, or the base address does not end with '/'.
    /// </exception>
    public OpenAIChatModel(HttpClient httpClient, string modelId, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (apiKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        }

        httpClient.BaseAddress ??= new Uri(_defaultBaseAddress);
        if (!httpClient.BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException(
                $"BaseAddress must end with '/' so that '{_completionsPath}' resolves beneath it, but was '{httpClient.BaseAddress}'.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
        _apiKey = apiKey;
        ModelId = modelId;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">
    /// The endpoint answered with a non-success status. The message includes the response body.
    /// </exception>
    /// <exception cref="InvalidOperationException">The response contains no choices or an unusable tool call.</exception>
    public async Task<ChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var body = new WireRequest(
            ModelId,
            messages.Select(ToWireMessage).ToList(),
            tools.Count == 0 ? null : tools.Select(ToWireTool).ToList(),
            Stream: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, _completionsPath)
        {
            Content = JsonContent.Create(body, options: _serializerOptions),
        };
        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI-compatible endpoint returned {(int)response.StatusCode}: {json}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<WireResponse>(json, _serializerOptions)
            ?? throw new InvalidOperationException("OpenAI-compatible endpoint returned an empty body.");

        return ToCompletion(parsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO: stream for real (<c>stream: true</c>, parse SSE <c>data:</c> lines). Until the SSE follow-up lands,
    /// this calls <see cref="CompleteAsync"/> and yields the whole content as a single chunk.
    /// </remarks>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completion = await CompleteAsync(messages, tools, cancellationToken).ConfigureAwait(false);
        if (completion.Message.Content.Length > 0)
        {
            yield return completion.Message.Content;
        }
    }

    private static WireMessage ToWireMessage(ChatMessage message) => new(
        OpenAIWireFormat.RoleName(message.Role),
        message.Content,
        message.ToolCalls is { Count: > 0 } calls ? calls.Select(ToWireToolCall).ToList() : null,
        message.ToolCallId);

    private static WireToolCall ToWireToolCall(ToolCall call) => new(
        call.Id,
        "function",
        new WireFunction(call.Name, OpenAIWireFormat.ToArgumentsString(call.Arguments)));

    private static WireTool ToWireTool(ToolDefinition tool) => new(
        "function",
        new WireFunctionDefinition(tool.Name, tool.Description, tool.ParametersSchema));

    private static ChatCompletion ToCompletion(WireResponse response)
    {
        if (response.Choices is not { Count: > 0 } choices)
        {
            throw new InvalidOperationException("OpenAI-compatible endpoint returned no choices.");
        }

        var message = choices[0].Message
            ?? throw new InvalidOperationException("OpenAI-compatible endpoint returned a choice without a message.");

        var toolCalls = message.ToolCalls is { Count: > 0 } wireCalls
            ? wireCalls.Select(ToToolCall).ToList()
            : null;
        var assistant = ChatMessage.Assistant(message.Content ?? string.Empty, toolCalls);

        TokenUsage? usage = response.Usage is { } wireUsage
            ? new TokenUsage(wireUsage.PromptTokens ?? 0, wireUsage.CompletionTokens ?? 0)
            : null;

        return new ChatCompletion(assistant, usage);
    }

    private static ToolCall ToToolCall(WireToolCall call, int index)
    {
        var function = call.Function
            ?? throw new InvalidOperationException("OpenAI-compatible endpoint returned a tool call without a function.");
        if (string.IsNullOrWhiteSpace(function.Name))
        {
            throw new InvalidOperationException("OpenAI-compatible endpoint returned a tool call without a function name.");
        }

        // Some servers omit call ids. Synthesize one so the tool result can still be correlated.
        var id = string.IsNullOrWhiteSpace(call.Id) ? $"call_{index}" : call.Id;

        return ToolCall.Create(id, function.Name, OpenAIWireFormat.ParseArguments(function.Arguments));
    }

    private sealed record WireRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("tools")] IReadOnlyList<WireTool>? Tools,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

    private sealed record WireToolCall(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("function")] WireFunction? Function);

    private sealed record WireFunction(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("arguments")] string? Arguments);

    private sealed record WireTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] WireFunctionDefinition Function);

    private sealed record WireFunctionDefinition(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record WireResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<WireChoice>? Choices,
        [property: JsonPropertyName("usage")] WireUsage? Usage);

    private sealed record WireChoice(
        [property: JsonPropertyName("message")] WireMessage? Message);

    private sealed record WireUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
