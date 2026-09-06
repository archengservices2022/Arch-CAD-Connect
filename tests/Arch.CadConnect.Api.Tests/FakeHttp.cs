using System.Net;
using System.Text;

namespace Arch.CadConnect.Api.Tests;

/// <summary>A scriptable <see cref="HttpMessageHandler"/> for API-client tests.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The request bodies, captured as strings BEFORE the client disposes them.</summary>
    public List<string> RequestBodies { get; } = new();

    public string? LastAuthorizationHeader { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();
        Requests.Add(request);
        return _responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static FakeHttpHandler Always(HttpStatusCode status, string body) =>
        new(_ => Json(status, body));

    public static FakeHttpHandler Throw(Exception ex) => new(_ => throw ex);
}
