using MarketplaceHub.Core;
using System.Net;
namespace MarketplaceHub.Services.Suz;
public enum OperationKind { SuzCreate, SuzStatus, SuzListOrders, SuzListBlocks, SuzReceiveCodes, SuzRecoverBlock, TrueApiCisesInfo, SuzAuthChallenge, SuzSignIn, FbsMutation }
public enum OperationSafety { SafeRead, StatefulRead, Mutation, Reconcile }
public sealed record OperationHttpResult(HttpStatusCode Status, byte[] Bytes, string? ContentType, string? ErrorCode, DateTimeOffset? RetryAt)
{
    public bool Success => (int)Status is >= 200 and < 300 && ErrorCode is null;
    public override string ToString() => $"HTTP {(int)Status} {ErrorCode}";
}
public sealed class OperationPolicy(IWorkflowClock clock, Func<double>? jitter = null, HttpClient? httpClient = null)
{
    private readonly HttpClient http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    public OperationPolicy WithClient(HttpClient client) => new(clock, jitter, client);
    public static OperationSafety Safety(OperationKind operation) => operation switch {
        OperationKind.SuzStatus or OperationKind.SuzListOrders or OperationKind.SuzListBlocks or OperationKind.TrueApiCisesInfo => OperationSafety.SafeRead,
        OperationKind.SuzReceiveCodes => OperationSafety.StatefulRead, OperationKind.SuzRecoverBlock => OperationSafety.Reconcile, _ => OperationSafety.Mutation };
    public async Task<OperationHttpResult> SendAsync(OperationKind operation, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var attempts = Safety(operation) == OperationSafety.SafeRead ? 3 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using var request = createRequest(); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = response.StatusCode;
                if (status == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    var at = retry?.Date ?? clock.UtcNow + (retry?.Delta ?? TimeSpan.FromSeconds(2));
                    return new(status, Array.Empty<byte>(), null, "quota", at < clock.UtcNow ? clock.UtcNow : at);
                }
                if ((int)status >= 500 && attempt + 1 < attempts) { await Backoff(attempt, ct); continue; }
                if ((int)status is >= 300 and < 400) return new(status, Array.Empty<byte>(), null, "redirect_refused", null);
                if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) return new(status, Array.Empty<byte>(), null, "response_too_large", null);
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (bytes.Length > 4 * 1024 * 1024) return new(status, Array.Empty<byte>(), null, "response_too_large", null);
                return new(status, bytes, response.Content.Headers.ContentType?.MediaType, response.IsSuccessStatusCode ? null : status switch { HttpStatusCode.Unauthorized => "authentication_failed", HttpStatusCode.Forbidden => "permission_denied", HttpStatusCode.NotFound => "unsupported_endpoint", _ => "http_failure" }, null);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                if (ct.IsCancellationRequested || attempt + 1 == attempts) return new(0, Array.Empty<byte>(), null, "transport_unknown", null);
                await Backoff(attempt, ct);
            }
        }
        return new(0, Array.Empty<byte>(), null, "transport_unknown", null);
    }
    private Task Backoff(int attempt, CancellationToken ct) => clock.DelayAsync(TimeSpan.FromSeconds(attempt + 1) + TimeSpan.FromMilliseconds(250 * Math.Clamp((jitter ?? Random.Shared.NextDouble)(), 0, 1)), ct);
}
