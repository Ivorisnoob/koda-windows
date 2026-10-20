using System.Net;

namespace Koda.Core.Tests;

/// <summary>Makes the engine's offline-ness deterministic: every visitor mint fails as if unreachable.</summary>
internal sealed class FailingHttpHandler : HttpMessageHandler
{
    public static readonly FailingHttpHandler Instance = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellation) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("offline by test design"));
}

internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellation)
    {
        Requests.Add(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"responseContext": {"visitorData": "TESTTOKEN%3D%3D"}}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
