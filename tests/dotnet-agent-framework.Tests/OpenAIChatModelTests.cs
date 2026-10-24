using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgentFramework.Tests.Fakes;

namespace DotnetAgentFramework.Tests;

public class OpenAIChatModelTests
{
    private const string _textResponse = """
        {
          "choices": [{"index": 0, "message": {"role": "assistant", "content": "Hello there"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15}
        }
        """;

    private const string _toolCallResponse = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_abc",
                "type": "function",
                "function": {"name": "get_weather", "arguments": "{\"city\":\"Kyiv\"}"}
              }]
            }
          }]
        }
        """;

    private static HttpClient CreateClient(StubHttpMessageHandler handler, string baseAddress = "https://example.test/v1/")
        => new(handler) { BaseAddress = new Uri(baseAddress) };

    [Fact]
    public void Constructor_NullBaseAddress_DefaultsToOpenAI()
    {
        var client = new HttpClient();

        _ = new OpenAIChatModel(client, "gpt-4o");

        Assert.Equal("https://api.openai.com/v1/", client.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void Constructor_BaseAddressWithoutTrailingSlash_Throws()
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:11434/v1") };

        var ex = Assert.Throws<ArgumentException>(() => new OpenAIChatModel(client, "llama3.1"));

        Assert.Equal("httpClient", ex.ParamName);
    }

    [Fact]
    public void Constructor_WhitespaceApiKey_Throws()
    {
        using var client = CreateClient(new StubHttpMessageHandler(_textResponse));

        Assert.Throws<ArgumentException>(() => new OpenAIChatModel(client, "gpt-4o", "  "));
    }

    [Fact]
    public async Task CompleteAsync_SendsPostToChatCompletionsWithBearerAndBody()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, "gpt-4o", "sk-test");
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        var tools = new[] { ToolDefinition.Create("get_weather", "Weather lookup", schema) };

        await model.CompleteAsync(
            [ChatMessage.System("be brief"), ChatMessage.User("hi")],
            tools);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "sk-test"), request.Headers.Authorization);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("be brief", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_weather", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Weather lookup", tool.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_NoApiKey_SendsNoAuthorizationHeader()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler, "http://localhost:11434/v1/");
        var model = new OpenAIChatModel(client, "llama3.1");

        await model.CompleteAsync([ChatMessage.User("hi")], []);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task CompleteAsync_NoTools_OmitsToolsField()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, "gpt-4o");

        await model.CompleteAsync([ChatMessage.User("hi")], []);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task CompleteAsync_ToolRoundTrip_SerializesToolCallAndToolResult()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, "gpt-4o");
        var args = JsonDocument.Parse("""{"city":"Kyiv"}""").RootElement.Clone();
        var call = ToolCall.Create("call_abc", "get_weather", args);

        await model.CompleteAsync(
            [
                ChatMessage.User("weather?"),
                ChatMessage.Assistant("", [call]),
                ChatMessage.ToolResult("call_abc", "sunny"),
            ],
            []);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var messages = body.RootElement.GetProperty("messages");

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var sent = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_abc", sent.GetProperty("id").GetString());
        Assert.Equal("function", sent.GetProperty("type").GetString());
        Assert.Equal("get_weather", sent.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"Kyiv"}""", sent.GetProperty("function").GetProperty("arguments").GetString());

        var toolResult = messages[2];
        Assert.Equal("tool", toolResult.GetProperty("role").GetString());
        Assert.Equal("call_abc", toolResult.GetProperty("tool_call_id").GetString());
        Assert.Equal("sunny", toolResult.GetProperty("content").GetString());
        Assert.False(toolResult.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task CompleteAsync_TextResponse_MapsContentAndUsage()
    {
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(_textResponse)), "gpt-4o");

        var completion = await model.CompleteAsync([ChatMessage.User("hi")], []);

        Assert.Equal(MessageRole.Assistant, completion.Message.Role);
        Assert.Equal("Hello there", completion.Message.Content);
        Assert.Empty(completion.Message.ToolCalls ?? []);
        Assert.Equal(new TokenUsage(12, 3), completion.Usage);
    }

    [Fact]
    public async Task CompleteAsync_ToolCallResponse_MapsIdNameAndArguments()
    {
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(_toolCallResponse)), "gpt-4o");

        var completion = await model.CompleteAsync([ChatMessage.User("weather?")], []);

        var call = Assert.Single(completion.Message.ToolCalls!);
        Assert.Equal("call_abc", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Kyiv", call.Arguments.GetProperty("city").GetString());
        Assert.Null(completion.Usage);
    }

    [Fact]
    public async Task CompleteAsync_TruncatedArguments_ProducesEmptyObjectAndKeepsCall()
    {
        const string truncated = """
            {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[
              {"id":"c1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Ky"}}
            ]}}]}
            """;
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(truncated)), "gpt-4o");

        var completion = await model.CompleteAsync([ChatMessage.User("weather?")], []);

        var call = Assert.Single(completion.Message.ToolCalls!);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    [Fact]
    public async Task CompleteAsync_ToolCallWithoutId_SynthesizesId()
    {
        const string noId = """
            {"choices":[{"message":{"tool_calls":[{"function":{"name":"get_weather","arguments":"{}"}}]}}]}
            """;
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(noId)), "llama3.1");

        var completion = await model.CompleteAsync([ChatMessage.User("weather?")], []);

        Assert.Equal("call_0", Assert.Single(completion.Message.ToolCalls!).Id);
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatus_ThrowsWithStatusAndBody()
    {
        const string errorBody = """{"error":{"message":"invalid model"}}""";
        var model = new OpenAIChatModel(
            CreateClient(new StubHttpMessageHandler(errorBody, HttpStatusCode.NotFound)),
            "nope");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => model.CompleteAsync([ChatMessage.User("hi")], []));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal($"OpenAI-compatible endpoint returned 404: {errorBody}", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_NoChoices_Throws()
    {
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler("""{"choices":[]}""")), "gpt-4o");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.CompleteAsync([ChatMessage.User("hi")], []));
    }

    [Fact]
    public async Task CompleteAsync_NonStreaming_OmitsStreamOptions()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        var model = new OpenAIChatModel(CreateClient(handler), "gpt-4o");

        await model.CompleteAsync([ChatMessage.User("hi")], []);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task StreamAsync_SendsStreamTrueWithIncludeUsage()
    {
        var handler = new StubHttpMessageHandler(_textDeltaStream);
        var model = new OpenAIChatModel(CreateClient(handler), "gpt-4o");

        await CollectAsync(model.StreamAsync([ChatMessage.User("hi")], []));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task StreamAsync_ContentDeltas_YieldedInOrderSkippingEmptyAndNull()
    {
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(_textDeltaStream)), "gpt-4o");

        var chunks = await CollectAsync(model.StreamAsync([ChatMessage.User("hi")], []));

        // The role-only chunk has null content, the "" chunk is empty, and the usage chunk has no choices.
        Assert.Equal(["Hel", "lo", " there"], chunks);
    }

    [Fact]
    public async Task StreamAsync_DoneMarker_StopsReadingFurtherData()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n" +
                            "data: [DONE]\n\n" +
                            "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\n";
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(body)), "gpt-4o");

        var chunks = await CollectAsync(model.StreamAsync([ChatMessage.User("hi")], []));

        Assert.Equal(["A"], chunks);
    }

    [Fact]
    public async Task StreamAsync_ToolCallDelta_EndsStreamWithoutFurtherText()
    {
        const string body = """
            data: {"choices":[{"delta":{"content":"Let me check. "}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}

            data: {"choices":[{"delta":{"content":"should not appear"}}]}

            data: [DONE]

            """;
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(body)), "gpt-4o");

        var chunks = await CollectAsync(model.StreamAsync([ChatMessage.User("weather?")], [_weatherTool]));

        Assert.Equal(["Let me check. "], chunks);
    }

    [Fact]
    public async Task StreamAsync_EmptyToolCallsArray_IsNotAToolCall()
    {
        const string body = """
            data: {"choices":[{"delta":{"content":"Hi","tool_calls":[]}}]}

            data: {"choices":[{"delta":{"content":"!","tool_calls":[]}}]}

            data: [DONE]

            """;
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(body)), "gpt-4o");

        var chunks = await CollectAsync(model.StreamAsync([ChatMessage.User("hi")], []));

        Assert.Equal(["Hi", "!"], chunks);
    }

    [Fact]
    public async Task StreamAsync_NonSuccessStatus_ThrowsWithStatusAndBody()
    {
        const string errorBody = """{"error":{"message":"invalid model"}}""";
        var model = new OpenAIChatModel(
            CreateClient(new StubHttpMessageHandler(errorBody, HttpStatusCode.Unauthorized)),
            "gpt-4o");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CollectAsync(model.StreamAsync([ChatMessage.User("hi")], [])));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal($"OpenAI-compatible endpoint returned 401: {errorBody}", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var model = new OpenAIChatModel(CreateClient(new StubHttpMessageHandler(_textDeltaStream)), "gpt-4o");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(model.StreamAsync([ChatMessage.User("hi")], [], cts.Token)));
    }

    [Fact]
    public async Task StreamAsync_ResponseHeadersElapse_ThrowsTimeoutException()
    {
        using var client = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("https://example.test/v1/") };
        var model = new OpenAIChatModel(client, new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => CollectAsync(model.StreamAsync([ChatMessage.User("hi")], [])));

        Assert.Contains("did not respond within 0.1 s", ex.Message);
    }

    private const string _textDeltaStream = """
        data: {"choices":[{"index":0,"delta":{"role":"assistant","content":null}}]}

        data: {"choices":[{"index":0,"delta":{"content":"Hel"}}]}

        data: {"choices":[{"index":0,"delta":{"content":""}}]}

        data: {"choices":[{"index":0,"delta":{"content":"lo"}}]}

        data: {"choices":[{"index":0,"delta":{"content":" there"},"finish_reason":"stop"}]}

        data: {"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}

        data: [DONE]

        """;

    private static readonly ToolDefinition _weatherTool = ToolDefinition.Create(
        "get_weather",
        "Weather lookup",
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task CompleteAsync_TemperatureAndMaxTokensSet_SendsBothFields()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            Temperature = 0.2,
            MaxTokens = 64,
        });

        await model.CompleteAsync([ChatMessage.User("hi")], []);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(0.2, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(64, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_TemperatureAndMaxTokensUnset_OmitsBothFields()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, new OpenAIChatModelOptions { ModelId = "gpt-4o" });

        await model.CompleteAsync([ChatMessage.User("hi")], []);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task CompleteAsync_DefaultHeaders_SentOnEveryRequest()
    {
        var handler = new StubHttpMessageHandler(_textResponse);
        using var client = CreateClient(handler);
        var model = new OpenAIChatModel(client, new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            DefaultHeaders = new Dictionary<string, string> { ["api-key"] = "az-key" },
        });

        await model.CompleteAsync([ChatMessage.User("one")], []);
        await model.CompleteAsync([ChatMessage.User("two")], []);

        Assert.All(handler.Requests, request =>
            Assert.Equal("az-key", Assert.Single(request.Headers.GetValues("api-key"))));
    }

    [Fact]
    public void Constructor_OptionsBaseAddressAppliedWhenClientHasNone()
    {
        using var client = new HttpClient();

        _ = new OpenAIChatModel(client, new OpenAIChatModelOptions
        {
            ModelId = "llama3.1",
            BaseAddress = new Uri("http://localhost:11434/v1/"),
        });

        Assert.Equal("http://localhost:11434/v1/", client.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public async Task CompleteAsync_RequestTimeoutElapses_ThrowsTimeoutException()
    {
        using var client = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("https://example.test/v1/") };
        var model = new OpenAIChatModel(client, new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => model.CompleteAsync([ChatMessage.User("hi")], []));

        Assert.Contains("did not respond within 0.1 s", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    /// <summary>Never answers; only the cancellation token can end the request.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
