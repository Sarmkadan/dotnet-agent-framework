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
/// <see cref="CompleteAsync"/> sends a single JSON response. <see cref="StreamAsync"/> streams Server-Sent Events.
/// </remarks>
public sealed class OpenAIChatModel : IChatModel
{
    private const string _completionsPath = "chat/completions";
    private const string _streamDone = "[DONE]";

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly double? _temperature;
    private readonly int? _maxTokens;
    private readonly TimeSpan _requestTimeout;
    private readonly KeyValuePair<string, string>[] _defaultHeaders;

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
        : this(httpClient, new OpenAIChatModelOptions { ModelId = modelId, ApiKey = apiKey })
    {
    }

    /// <summary>
    /// Creates an adapter from full options. The options are validated and copied, so later changes to them
    /// have no effect on this instance.
    /// </summary>
    /// <param name="httpClient">
    /// Client used for every request. When <see cref="HttpClient.BaseAddress"/> is null it is set to
    /// <see cref="OpenAIChatModelOptions.BaseAddress"/>. Otherwise it must end with '/', so that <c>chat/completions</c>
    /// resolves beneath it instead of replacing the last segment.
    /// </param>
    /// <param name="options">Model, endpoint and request settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An option is invalid (see <see cref="OpenAIChatModelOptions.Validate"/>), or the client's base address does not end with '/'.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range (see <see cref="OpenAIChatModelOptions.Validate"/>).</exception>
    public OpenAIChatModel(HttpClient httpClient, OpenAIChatModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        httpClient.BaseAddress ??= options.BaseAddress;
        if (!httpClient.BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException(
                $"BaseAddress must end with '/' so that '{_completionsPath}' resolves beneath it, but was '{httpClient.BaseAddress}'.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
        _apiKey = options.ApiKey;
        _temperature = options.Temperature;
        _maxTokens = options.MaxTokens;
        _requestTimeout = options.RequestTimeout;
        _defaultHeaders = options.DefaultHeaders.ToArray();
        ModelId = options.ModelId;
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

        using var request = CreateRequest(messages, tools, stream: false);

        // The timeout covers the body read too, so a server that stalls mid-response is cut off as well.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);
        string json;
        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException(response.StatusCode, json);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"OpenAI-compatible endpoint did not respond within {_requestTimeout.TotalSeconds:0.#} s.",
                ex);
        }

        var parsed = JsonSerializer.Deserialize<WireResponse>(json, _serializerOptions)
            ?? throw new InvalidOperationException("OpenAI-compatible endpoint returned an empty body.");

        return ToCompletion(parsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends the request with <c>stream: true</c> and <c>stream_options.include_usage</c>, then reads the
    /// Server-Sent Events response line by line and yields each non-empty <c>delta.content</c>. Token usage is
    /// not reported: <see cref="IChatModel.StreamAsync"/> has no channel for it.
    /// <para>
    /// If any chunk carries a non-empty <c>delta.tool_calls</c>, the model has chosen to call tools. The stream
    /// then ends without yielding further text, and the caller should fall back to <see cref="CompleteAsync"/>
    /// to receive the tool calls. Text yielded before that chunk stays yielded.
    /// </para>
    /// <para>
    /// <see cref="OpenAIChatModelOptions.RequestTimeout"/> applies only until the response headers arrive. It does
    /// not cap how long the stream stays open, so a long answer is not cut off. A stalled stream is not detected;
    /// cancel <paramref name="cancellationToken"/> to stop it.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// The endpoint answered with a non-success status. The message includes the response body.
    /// </exception>
    /// <exception cref="TimeoutException">No response headers arrived within <see cref="OpenAIChatModelOptions.RequestTimeout"/>.</exception>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, tools, stream: true);
        using var response = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateStatusException(response.StatusCode, error);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var data in SseParser.ReadDataLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (data == _streamDone)
            {
                yield break;
            }

            var chunk = JsonSerializer.Deserialize<WireStreamChunk>(data, _serializerOptions);
            if (chunk?.Choices is not { Count: > 0 } choices || choices[0].Delta is not { } delta)
            {
                // Usage-only chunks (sent after the content when include_usage is set) have no choices.
                continue;
            }

            if (delta.ToolCalls is { Count: > 0 })
            {
                yield break;
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                yield return delta.Content;
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        bool stream)
    {
        var body = new WireRequest(
            ModelId,
            messages.Select(ToWireMessage).ToList(),
            tools.Count == 0 ? null : tools.Select(ToWireTool).ToList(),
            Stream: stream,
            StreamOptions: stream ? new WireStreamOptions(IncludeUsage: true) : null,
            Temperature: _temperature,
            MaxTokens: _maxTokens);

        var request = new HttpRequestMessage(HttpMethod.Post, _completionsPath)
        {
            Content = JsonContent.Create(body, options: _serializerOptions),
        };
        foreach (var (name, value) in _defaultHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    /// <summary>
    /// Sends the request and returns once the response headers arrive. The body is left unread so it can be streamed.
    /// </summary>
    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"OpenAI-compatible endpoint did not respond within {_requestTimeout.TotalSeconds:0.#} s.",
                ex);
        }
    }

    private static HttpRequestException CreateStatusException(HttpStatusCode statusCode, string body) => new(
        $"OpenAI-compatible endpoint returned {(int)statusCode}: {body}",
        inner: null,
        statusCode: statusCode);

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
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("stream_options")] WireStreamOptions? StreamOptions,
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens);

    private sealed record WireStreamOptions(
        [property: JsonPropertyName("include_usage")] bool IncludeUsage);

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

    private sealed record WireStreamChunk(
        [property: JsonPropertyName("choices")] IReadOnlyList<WireStreamChoice>? Choices);

    private sealed record WireStreamChoice(
        [property: JsonPropertyName("delta")] WireDelta? Delta);

    private sealed record WireDelta(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls);
}
