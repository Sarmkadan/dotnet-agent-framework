using System.Net;
using System.Text;

namespace DotnetAgentFramework.Tests.Fakes;

/// <summary>
/// Returns a canned response and records every request it receives. Lets adapter tests run without a network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string> _requestBodies = [];

    public StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    /// <summary>Requests received, in call order.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>Request bodies as text, captured when each request arrived.</summary>
    public IReadOnlyList<string> RequestBodies => _requestBodies;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        _requestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}
