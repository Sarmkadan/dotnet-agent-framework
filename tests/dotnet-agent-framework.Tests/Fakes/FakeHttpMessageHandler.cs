using System.Net;
using System.Text;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Answers every request with a configured response and remembers the last request together with a buffered copy
/// of its body. The response content is either a string (buffered) or a stream built from a string, which is how
/// Server-Sent Events are read in the streaming tests.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _content;
    private readonly string _contentType;
    private readonly bool _streamContent;

    /// <summary>
    /// Creates a handler that answers with <paramref name="content"/>.
    /// </summary>
    /// <param name="content">Response body text.</param>
    /// <param name="status">Status code of the response.</param>
    /// <param name="contentType">Media type of the response body.</param>
    /// <param name="streamContent">
    /// When true, the body is served as a non-buffered stream, as a real network response would be.
    /// </param>
    public FakeHttpMessageHandler(
        string content,
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "application/json",
        bool streamContent = false)
    {
        _content = content;
        _status = status;
        _contentType = contentType;
        _streamContent = streamContent;
    }

    /// <summary>The most recent request received, or null when none has arrived.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Text of the most recent request body, buffered when the request arrived. Null when it had no body.</summary>
    public string? LastRequestBody { get; private set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var bytes = Encoding.UTF8.GetBytes(_content);
        HttpContent content = _streamContent
            ? new StreamContent(new MemoryStream(bytes))
            : new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);

        return new HttpResponseMessage(_status) { Content = content };
    }
}
