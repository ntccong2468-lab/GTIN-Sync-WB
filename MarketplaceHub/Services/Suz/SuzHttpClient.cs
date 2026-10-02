using MarketplaceHub.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MarketplaceHub.Services.Suz;
public interface ISuzClient
{
    Task<SuzOutcome<SuzOrderReceipt>> CreateOrderAsync(PurchaseIntent intent, CancellationToken ct);
    Task<SuzOutcome<SuzOrderStatus>> ReadOrderAsync(PurchaseIntent intent, CancellationToken ct);
    Task<SuzOutcome<IReadOnlyList<SuzOrderCandidate>>> ListOrdersAsync(PurchaseIntent intent, CancellationToken ct);
    Task<SuzOutcome<SuzBlock>> ReceiveCodesAsync(PurchaseIntent intent, int quantity, CancellationToken ct);
    Task<SuzOutcome<IReadOnlyList<SuzBlock>>> ListBlocksAsync(PurchaseIntent intent, CancellationToken ct);
    Task<SuzOutcome<SuzBlock>> RecoverBlockAsync(PurchaseIntent intent, string blockId, CancellationToken ct);
}
public interface ISuzSigner { Task<byte[]> SignAsync(byte[] payload, string certificateThumbprint, bool detached, CancellationToken ct); }
public interface IKizLegalReader { Task<IReadOnlyList<KizLegalProof>> ReadAsync(SuzProfile profile, IReadOnlyList<string> rawCodes, CancellationToken ct); }
public static class SuzProfileCatalog
{
    public static bool Supported(SuzProfile p) => p.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase) && p.ProductGroup == "lp" && p.CisType == "UNIT" && p.TemplateId == 10 && p.ReleaseMethodType == "PRODUCTION" &&
        p.OwnerInn.Length is 10 or 12 && p.OwnerInn.All(char.IsDigit) && SafeHost(p.SuzBaseUri, "suzgrid.crpt.ru") && SafeHost(p.TrueApiBaseUri, "markirovka.crpt.ru");
    private static bool SafeHost(Uri uri, string host) => uri.Scheme == "https" && uri.Host == host && uri.IsDefaultPort && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "";
}
public sealed class SuzHttpClient
    : ISuzClient
{
    private readonly ISuzSigner signer; private readonly Func<SuzProfile, ZnakConfig> resolve; private readonly OperationPolicy policy; private readonly IWorkflowClock clock;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> tokens = new();
    public SuzHttpClient(HttpClient http, ISuzSigner signer, Func<SuzProfile, ZnakConfig> resolveCredentials, OperationPolicy policy, IWorkflowClock clock)
    { this.signer = signer; resolve = resolveCredentials; this.policy = policy.WithClient(http); this.clock = clock; }
    public static byte[] Payload(PurchaseIntentRequest request) => JsonSerializer.SerializeToUtf8Bytes(new { productGroup = request.Profile.ProductGroup, attributes = new { releaseMethodType = request.Profile.ReleaseMethodType }, products = new[] { new { gtin = request.Gtin, quantity = request.Quantity, serialNumberType = "OPERATOR", templateId = request.Profile.TemplateId, cisType = request.Profile.CisType } } });
    public static string PayloadHash(PurchaseIntentRequest request) => Convert.ToHexString(SHA256.HashData(Payload(request)));
    private ZnakConfig Credentials(SuzProfile profile)
    {
        var c = resolve(profile);
        if (!c.Enabled || c.Inn.Trim() != profile.OwnerInn || !c.Environment.Equals(profile.Environment, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(c.CertificateThumbprint)) throw new InvalidOperationException("credential_scope_mismatch");
        return c;
    }
    public async Task<SuzOutcome<string>> TokenAsync(SuzProfile profile, bool suz, CancellationToken ct)
    {
        if (!SuzProfileCatalog.Supported(profile)) return new(SuzOutcomeKind.Unsupported, null, "unsupported_profile");
        var key = profile.Id + "/" + profile.Version + "/" + profile.CredentialVersion + "/" + suz;
        try
        {
            var config = Credentials(profile); // Revalidate even for a cached token.
            if (suz && (string.IsNullOrWhiteSpace(config.OmsId) || string.IsNullOrWhiteSpace(config.OmsConnection))) return new(SuzOutcomeKind.Rejected, null, "missing_oms_configuration");
            if (tokens.TryGetValue(key, out var token) && token.Expires > clock.UtcNow) return new(SuzOutcomeKind.Confirmed, token.Token);
            var auth = new Uri(profile.TrueApiBaseUri, "/api/v3/true-api/");
            var challenge = await policy.SendAsync(OperationKind.SuzAuthChallenge, () => new(HttpMethod.Get, new Uri(auth, "auth/key")), ct);
            var data = Json(challenge); var uuid = Text(data, "uuid"); var challengeData = Text(data, "data");
            if (uuid == "" || challengeData == "") return Failure<string>(challenge, "auth_challenge_invalid");
            var signature = await signer.SignAsync(Encoding.UTF8.GetBytes(challengeData), config.CertificateThumbprint, false, ct);
            var body = JsonSerializer.SerializeToUtf8Bytes(new { uuid, data = Convert.ToBase64String(signature), inn = profile.OwnerInn });
            var path = "auth/simpleSignIn" + (suz ? "/" + Uri.EscapeDataString(config.OmsConnection.Trim()) : "");
            var login = await policy.SendAsync(OperationKind.SuzSignIn, () => Message(HttpMethod.Post, new Uri(auth, path), body), ct);
            var parsed = Json(login); var value = Text(parsed, suz ? "clientToken" : "token");
            if (value == "") return Failure<string>(login, "auth_token_invalid");
            tokens[key] = (value, clock.UtcNow.AddMinutes(10)); return new(SuzOutcomeKind.Confirmed, value);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or CryptographicException or IOException or OperationCanceledException or TimeoutException)
        { return new(SuzOutcomeKind.Rejected, null, "authentication_failed"); }
    }
    public async Task<SuzOutcome<SuzOrderReceipt>> CreateOrderAsync(PurchaseIntent intent, CancellationToken ct)
    {
        if (!intent.Request.Profile.ContractEnabled || !SuzProfileCatalog.Supported(intent.Request.Profile)) return new(SuzOutcomeKind.Unsupported, null, "unverified_contract");
        if (intent.Request.Quantity <= 0 || GtinCode.Normalize(intent.Request.Gtin) != intent.Request.Gtin || PayloadHash(intent.Request) != intent.Request.PayloadHash) return new(SuzOutcomeKind.Rejected, null, "invalid_purchase_payload");
        try
        {
            var token = await TokenAsync(intent.Request.Profile, true, ct); if (token.Kind != SuzOutcomeKind.Confirmed) return new(token.Kind, null, token.ErrorCode, token.RetryAt);
            var config = Credentials(intent.Request.Profile); var payload = Payload(intent.Request); var signature = Convert.ToBase64String(await signer.SignAsync(payload, config.CertificateThumbprint, true, ct));
            var result = await policy.SendAsync(OperationKind.SuzCreate, () => { var q = Message(HttpMethod.Post, Url(intent, config, "/api/v3/order"), payload, token.Value); q.Headers.TryAddWithoutValidation("X-Signature", signature); return q; }, ct);
            var root = Json(result); var id = Text(root, "orderId");
            return result.Success && id != "" ? new(SuzOutcomeKind.Confirmed, new SuzOrderReceipt(id)) : Failure<SuzOrderReceipt>(result, "create_response_unknown");
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or CryptographicException or IOException)
        { return new(SuzOutcomeKind.Unknown, null, "create_unknown"); }
    }
    public Task<SuzOutcome<SuzOrderStatus>> ReadOrderAsync(PurchaseIntent intent, CancellationToken ct) => Request(intent, OperationKind.SuzStatus, "/api/v3/order/status", "&orderId=" + Escape(intent.RemoteOrderId), root => {
        var entries = root as JsonArray ?? new JsonArray(root?.DeepClone());
        var row = entries.FirstOrDefault(x => Text(x, "gtin") == intent.Request.Gtin) ?? (entries.Count == 1 ? entries[0] : null);
        if (row is null || (Text(row, "orderId") != "" && Text(row, "orderId") != intent.RemoteOrderId) || (Text(row, "gtin") != "" && Text(row, "gtin") != intent.Request.Gtin)) return null;
        var state = Text(row, "bufferStatus"); if (state == "") state = Text(row, "status");
        return state == "" ? null : new SuzOrderStatus(intent.RemoteOrderId!, state, Int(row, "availableCodes"), state is "REJECTED" or "DECLINED" ? "remote_rejected" : null);
    }, ct);
    public Task<SuzOutcome<IReadOnlyList<SuzOrderCandidate>>> ListOrdersAsync(PurchaseIntent intent, CancellationToken ct) => Request<IReadOnlyList<SuzOrderCandidate>>(intent, OperationKind.SuzListOrders, "/api/v3/order/list", "", root => {
        var list = root as JsonArray ?? root?["orders"] as JsonArray; if (list is null) return null;
        return list.Select(x => new SuzOrderCandidate(Text(x, "orderId"), Text(x, "ownerInn"), Text(x, "environment"), Text(x, "gtin"), Int(x, "quantity"), Text(x, "correlationProof"))).Where(x => x.OrderId != "").ToArray();
    }, ct);
    public Task<SuzOutcome<SuzBlock>> ReceiveCodesAsync(PurchaseIntent intent, int quantity, CancellationToken ct) => Request(intent, OperationKind.SuzReceiveCodes, "/api/v3/codes", "&orderId=" + Escape(intent.RemoteOrderId) + "&gtin=" + Escape(intent.Request.Gtin) + "&quantity=" + quantity, root => Block(intent, root, null), ct);
    public Task<SuzOutcome<IReadOnlyList<SuzBlock>>> ListBlocksAsync(PurchaseIntent intent, CancellationToken ct) => Request<IReadOnlyList<SuzBlock>>(intent, OperationKind.SuzListBlocks, "/api/v3/order/codes/blocks", "&orderId=" + Escape(intent.RemoteOrderId) + "&gtin=" + Escape(intent.Request.Gtin), root => {
        var list = root as JsonArray ?? root?["blocks"] as JsonArray; if (list is null) return null;
        var result = new List<SuzBlock>();
        foreach (var row in list) { var id = row is JsonValue ? row.ToString() : Text(row, "blockId"); if (id == "" || (row is JsonObject && ((Text(row, "orderId") != "" && Text(row, "orderId") != intent.RemoteOrderId) || (Text(row, "gtin") != "" && Text(row, "gtin") != intent.Request.Gtin)))) return null; result.Add(new(id, intent.RemoteOrderId!, intent.Request.Gtin, Array.Empty<string>())); }
        return result;
    }, ct);
    public Task<SuzOutcome<SuzBlock>> RecoverBlockAsync(PurchaseIntent intent, string blockId, CancellationToken ct) => Request(intent, OperationKind.SuzRecoverBlock, "/api/v3/order/codes/retry", "&blockId=" + Escape(blockId), root => Block(intent, root, blockId), ct);
    private async Task<SuzOutcome<T>> Request<T>(PurchaseIntent intent, OperationKind operation, string path, string query, Func<JsonNode?, T?> parse, CancellationToken ct) where T : class
    {
        try
        {
            if (operation != OperationKind.SuzListOrders && string.IsNullOrWhiteSpace(intent.RemoteOrderId)) return new(SuzOutcomeKind.Rejected, null, "missing_remote_order_id");
            var token = await TokenAsync(intent.Request.Profile, true, ct); if (token.Kind != SuzOutcomeKind.Confirmed) return new(token.Kind, null, token.ErrorCode, token.RetryAt);
            var config = Credentials(intent.Request.Profile);
            var result = await policy.SendAsync(operation, () => Message(HttpMethod.Get, new Uri(Url(intent, config, path).AbsoluteUri + query), null, token.Value), ct);
            var root = Json(result); var value = root is null ? null : parse(root);
            if (value is null || !result.Success) return Failure<T>(result, "response_unknown");
            return new(SuzOutcomeKind.Confirmed, value);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or FormatException or OperationCanceledException) { return new(SuzOutcomeKind.Unknown, null, "response_unknown"); }
    }
    private static SuzBlock? Block(PurchaseIntent intent, JsonNode? root, string? knownId)
    {
        if (root is JsonObject && ((Text(root, "orderId") != "" && Text(root, "orderId") != intent.RemoteOrderId) || (Text(root, "gtin") != "" && Text(root, "gtin") != intent.Request.Gtin))) return null;
        var id = Text(root, "blockId"); if (id == "") id = knownId ?? ""; if (id == "" || (knownId is not null && id != knownId)) return null;
        var codes = root as JsonArray ?? root?["codes"] as JsonArray; if (codes is null) return null;
        var raw = codes.Select(x => x is JsonValue ? x.ToString() : Text(x, "cis")).ToArray();
        if (raw.Any(x => x == "" || KizCodeIdentity.Gtin(x) != intent.Request.Gtin)) return null;
        return new(id, intent.RemoteOrderId!, intent.Request.Gtin, raw);
    }
    internal static JsonNode? Json(OperationHttpResult result)
    {
        if (!result.Success || result.ContentType is not ("application/json" or "application/problem+json")) return null;
        try { var node = JsonNode.Parse(result.Bytes); return node is JsonObject obj && (obj["error"] is not null || obj["errors"] is JsonArray { Count: > 0 } || obj["success"]?.ToString() == "false") ? null : node; }
        catch (JsonException) { return null; }
    }
    internal static string Text(JsonNode? root, string key) => root is JsonObject obj && obj[key] is JsonValue value ? value.ToString() : "";
    private static int Int(JsonNode? root, string key) => int.TryParse(Text(root, key), out var n) ? n : 0;
    internal static HttpRequestMessage Message(HttpMethod method, Uri uri, byte[]? body = null, string? token = null)
    {
        var q = new HttpRequestMessage(method, uri); q.Headers.Accept.Add(new("application/json"));
        if (token is not null) q.Headers.TryAddWithoutValidation("clientToken", token);
        if (body is not null) { q.Content = new ByteArrayContent(body); q.Content.Headers.ContentType = new("application/json"); } return q;
    }
    private static Uri Url(PurchaseIntent intent, ZnakConfig config, string path) => new(intent.Request.Profile.SuzBaseUri, path + "?omsId=" + Escape(config.OmsId.Trim()));
    private static string Escape(string? value) => Uri.EscapeDataString(value ?? "");
    private static SuzOutcome<T> Failure<T>(OperationHttpResult result, string malformed) => new(result.RetryAt is not null ? SuzOutcomeKind.Pending : result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? SuzOutcomeKind.Rejected : result.Status == HttpStatusCode.NotFound ? SuzOutcomeKind.Unsupported : SuzOutcomeKind.Unknown, default, result.ErrorCode ?? malformed, result.RetryAt);
}
