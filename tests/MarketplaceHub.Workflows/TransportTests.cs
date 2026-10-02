using MarketplaceHub.Core;
using MarketplaceHub.Services.Suz;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class TransportTests
{
    private const string Payload = "{\"productGroup\":\"lp\",\"attributes\":{\"releaseMethodType\":\"PRODUCTION\"},\"products\":[{\"gtin\":\"04601234567893\",\"quantity\":2,\"serialNumberType\":\"OPERATOR\",\"templateId\":10,\"cisType\":\"UNIT\"}]}";
    public static async Task Run(WorkflowTestRunner r)
    {
        foreach (var body in new[] { "{}", "<html>proxy</html>", "{\"error\":\"denied\",\"orderId\":\"FAKE\"}", "{" })
            await r.CheckAsync("create_malformed_2xx_is_unknown_" + Array.IndexOf(new[] { "{}", "<html>proxy</html>", "{\"error\":\"denied\",\"orderId\":\"FAKE\"}", "{" }, body), async () => {
                using var f = new TransportFixture(_ => SuzFixtureHttp.Json(body));
                var result = await f.Client.CreateOrderAsync(f.Intent, default);
                Expect(result.Kind == SuzOutcomeKind.Unknown && f.Handler.Requests.Count(x => x.Path == "/api/v3/order") == 1, "Malformed 2xx accepted or retried create");
            });
        await r.CheckAsync("signature_uses_exact_payload_and_detached_mode", async () => {
            using var f = new TransportFixture(_ => SuzFixtureHttp.Json("{\"orderId\":\"SUZ-1\"}"));
            var result = await f.Client.CreateOrderAsync(f.Intent, default);
            var sent = f.Handler.Requests.Single(x => x.Path == "/api/v3/order");
            Expect(result.Value?.OrderId == "SUZ-1" && sent.Body == Payload && f.Signer.Signed.Last().SequenceEqual(Encoding.UTF8.GetBytes(Payload)) && f.Signer.Detached.Last(), "Signed bytes or contract body differs");
        });
        await r.CheckAsync("safe_read_retries_fresh_messages_but_receive_never_reissues", async () => {
            var seen = new HashSet<HttpRequestMessage>();
            using var f = new TransportFixture(q => { Expect(seen.Add(q), "Retry reused disposed request"); throw new HttpRequestException("fixture secret response"); });
            await f.Policy.SendAsync(OperationKind.SuzStatus, () => new(HttpMethod.Get, "https://fixture.invalid/status"), default);
            Expect(f.Handler.Requests.Count == 3 && f.Clock.Delays.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }), "SafeRead attempts/backoff wrong");
            var before = f.Handler.Requests.Count;
            var result = await f.Policy.SendAsync(OperationKind.SuzReceiveCodes, () => new(HttpMethod.Get, "https://fixture.invalid/codes"), default);
            Expect(f.Handler.Requests.Count == before + 1 && result.ErrorCode == "transport_unknown", "Stateful GET reissued after lost response");
        });
        foreach (var header in new[] { "120", "Fri, 02 Oct 2026 20:02:00 GMT" })
            await r.CheckAsync("quota_deadline_" + (header == "120" ? "seconds" : "date"), async () => {
                using var f = new TransportFixture(_ => { var res = SuzFixtureHttp.Json("{}", HttpStatusCode.TooManyRequests); res.Headers.TryAddWithoutValidation("Retry-After", header); return res; });
                var result = await f.Policy.SendAsync(OperationKind.TrueApiCisesInfo, () => new(HttpMethod.Post, "https://fixture.invalid/cises/info"), default);
                Expect(result.RetryAt == DateTimeOffset.Parse("2026-10-02T20:02:00Z") && f.Handler.Requests.Count == 1 && f.Clock.Delays.Count == 0, "Quota deadline not returned to persistence");
            });
        await r.CheckAsync("401_xml_stops_without_purchase_and_diagnostics_are_redacted", async () => {
            using var f = new TransportFixture(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("<xml>" + WorkflowFixture.Raw + " fixture-token fixture-connection</xml>") }, auth: false);
            var result = await f.Client.CreateOrderAsync(f.Intent, default);
            Expect(f.Handler.Requests.All(x => x.Path != "/api/v3/order") && result.Kind != SuzOutcomeKind.Confirmed, "Authentication failure purchased");
            var text = result.ToString(); Expect(!text.Contains(WorkflowFixture.Raw) && !text.Contains("fixture-token") && !text.Contains("fixture-connection"), "Diagnostic leaked auth response");
        });
        await r.CheckAsync("unsupported_profile_never_guesses_production", async () => {
            using var f = new TransportFixture(_ => throw new Exception("Unsupported profile called HTTP"));
            var result = await f.Client.CreateOrderAsync(f.Intent with { Request = f.Intent.Request with { Profile = f.Intent.Request.Profile with { ReleaseMethodType = "IMPORT" } } }, default);
            Expect(result.Kind == SuzOutcomeKind.Unsupported && f.Handler.Requests.Count == 0, "Import silently used production contract");
        });
        await r.CheckAsync("wrong_order_or_gtin_block_is_unknown", async () => {
            using var f = new TransportFixture(_ => SuzFixtureHttp.Json(JsonSerializer.Serialize(new { blockId = "B", orderId = "WRONG", gtin = WorkflowFixture.Gtin, codes = new[] { WorkflowFixture.Raw } })));
            var result = await f.Client.ReceiveCodesAsync(f.Intent with { RemoteOrderId = "SUZ-1" }, 2, default);
            Expect(result.Kind == SuzOutcomeKind.Unknown, "Wrong order's block accepted");
        });
        await r.CheckAsync("wrong_gtin_status_cannot_authorize_receive", async () => {
            using var f = new TransportFixture(_ => SuzFixtureHttp.Json("[{\"orderId\":\"SUZ-1\",\"gtin\":\"04601234567886\",\"bufferStatus\":\"ACTIVE\",\"availableCodes\":2}]"));
            var result = await f.Client.ReadOrderAsync(f.Intent with { RemoteOrderId = "SUZ-1" }, default);
            Expect(result.Kind == SuzOutcomeKind.Unknown, "Wrong GTIN status authorized a stateful read");
        });
        await r.CheckAsync("true_api_maps_requested_cis_exactly_not_array_position", async () => {
            using var f = new TransportFixture(_ => SuzFixtureHttp.Json(JsonSerializer.Serialize(new[] {
                new { requestedCis = "010460123456789321OTHER-SERIAL", cisInfo = new { status = "INTRODUCED", statusEx = "EMPTY", gtin = WorkflowFixture.Gtin, ownerInn = "7701234567", packageType = "UNIT" } },
                new { requestedCis = "010460123456789321SERIAL0000001", cisInfo = new { status = "APPLIED", statusEx = "EMPTY", gtin = WorkflowFixture.Gtin, ownerInn = "7701234567", packageType = "UNIT" } } })));
            var reader = new TrueApiKizReader(f.Client, f.Policy, f.Http, f.Clock, f.Db.CodeProtector);
            var proofs = await reader.ReadAsync(f.Profile, new[] { WorkflowFixture.Raw }, default);
            Expect(proofs.Count == 1 && proofs[0].CodeHash == f.Db.CodeProtector.Identity(WorkflowFixture.Raw) && proofs[0].RawStatus == "APPLIED", "Proof mapped by position or foreign identifier");
        });
        await r.CheckAsync("non_json_content_type_does_not_complete_purchase", async () => {
            using var f = new TransportFixture(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"orderId\":\"SUZ-1\"}", Encoding.UTF8, "text/html") });
            var result = await f.Client.CreateOrderAsync(f.Intent, default); Expect(result.Kind == SuzOutcomeKind.Unknown, "Unexpected content type accepted");
        });
        await r.CheckAsync("conflicting_duplicate_true_api_rows_confer_no_legal_proof", async () => {
            using var f = new TransportFixture(_ => SuzFixtureHttp.Json(JsonSerializer.Serialize(new[] {
                new { requestedCis = "010460123456789321SERIAL0000001", cisInfo = new { status = "INTRODUCED", statusEx = "EMPTY", gtin = WorkflowFixture.Gtin, ownerInn = "7701234567", packageType = "UNIT" } },
                new { requestedCis = "010460123456789321SERIAL0000001", cisInfo = new { status = "RETIRED", statusEx = "EMPTY", gtin = WorkflowFixture.Gtin, ownerInn = "7701234567", packageType = "UNIT" } } })));
            var reader = new TrueApiKizReader(f.Client, f.Policy, f.Http, f.Clock, f.Db.CodeProtector);
            var proofs = await reader.ReadAsync(f.Profile, new[] { WorkflowFixture.Raw }, default);
            Expect(proofs.Count == 0, "Ambiguous duplicate statuses conferred legal proof");
        });
    }
    private sealed class TransportFixture : IDisposable
    {
        private readonly WorkflowFixture fixture = WorkflowFixture.Create();
        public MarketplaceHub.Infrastructure.AppDatabase Db => fixture.Db;
        public SuzProfile Profile { get; }
        public PurchaseIntent Intent { get; }
        public FakeWorkflowClock Clock { get; } = new();
        public SuzFixtureHttp Handler { get; }
        public HttpClient Http { get; }
        public FixtureSigner Signer { get; } = new();
        public OperationPolicy Policy { get; }
        public SuzHttpClient Client { get; }
        public TransportFixture(Func<HttpRequestMessage, HttpResponseMessage> response, bool auth = true)
        {
            Profile = fixture.Profile with { AutoPurchaseEnabled = true, ContractEnabled = true };
            Handler = new(q => auth && q.RequestUri!.AbsolutePath.EndsWith("/auth/key") ? SuzFixtureHttp.Json("{\"uuid\":\"uuid-fixture\",\"data\":\"challenge\"}") :
                auth && q.RequestUri!.AbsolutePath.Contains("/auth/simpleSignIn") ? SuzFixtureHttp.Json("{\"clientToken\":\"fixture-token\",\"token\":\"fixture-token\"}") : response(q));
            Http = new(Handler); Policy = new(Clock, () => 0, Http);
            Client = new(Http, Signer, _ => new("7701234567", "Production", "fixture-thumbprint", "", "", true, "fixture-oms", "fixture-connection", false), Policy, Clock);
            Intent = new("fixture-intent", new("fixture-job", 1, Profile, WorkflowFixture.Gtin, 2, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Payload)))), "stable-request", PurchaseStage.CreateSending, null, null, null);
        }
        public void Dispose() { Http.Dispose(); fixture.Dispose(); }
    }
    private sealed class FixtureSigner : ISuzSigner
    {
        public List<byte[]> Signed { get; } = new(); public List<bool> Detached { get; } = new();
        public Task<byte[]> SignAsync(byte[] payload, string certificateThumbprint, bool detached, CancellationToken ct) { Signed.Add(payload.ToArray()); Detached.Add(detached); return Task.FromResult(new byte[] { 1, 2, 3 }); }
    }
}
