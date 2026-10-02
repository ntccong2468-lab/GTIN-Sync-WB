using System.Net;
using System.Text;
namespace MarketplaceHub.Workflows;
public sealed class SuzFixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<(string Path, string Body, string? Signature)> Requests { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.TryGetValues("X-Signature", out var values) ? values.Single() : null));
        return response(request);
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
