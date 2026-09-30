using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Net;
using System.Reflection;
using System.Text;

var failures = new List<string>();
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Expect(bool value, string message) { if (!value) throw new Exception(message); }
StoreProfile Store(Marketplace m) => new(1, m, "fixture", "123", "test-key", "456", "789", "test-token", true);
MarketplaceGateway Api(Func<HttpRequestMessage, HttpResponseMessage> respond)
{
    var api = new MarketplaceGateway();
    typeof(MarketplaceGateway).GetField("http", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(api, new HttpClient(new FixtureHttp(respond)) { Timeout = TimeSpan.FromSeconds(3) });
    return api;
}
HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

await Check("WB status failure never turns cached orders into new orders", async () =>
{
    var api = Api(r => r.RequestUri!.AbsolutePath switch
    {
        "/api/v3/orders/new" => Json("{\"orders\":[{\"id\":1,\"article\":\"A\"}]}"),
        "/api/v3/orders" => Json("{\"orders\":[],\"next\":0}"),
        _ => Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized)
    });
    var rejected = false;
    try { await api.SyncFbsAsync(Store(Marketplace.Wildberries)); }
    catch (Exception ex) { rejected = ex.Message.Contains("401"); }
    Expect(rejected, "Status API failure must fail the sync; unknown status must not become new.");
});
await Check("WB confirmed status and required SGTIN are preserved", async () =>
{
    var api = Api(r => r.RequestUri!.AbsolutePath switch
    {
        "/api/v3/orders/new" => Json("{\"orders\":[{\"id\":1,\"article\":\"A\",\"requiredMeta\":[\"sgtin\"]}]}"),
        "/api/v3/orders" => Json("{\"orders\":[],\"next\":0}"),
        _ => Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}")
    });
    var rows = await api.SyncFbsAsync(Store(Marketplace.Wildberries));
    Expect(rows.Count == 1 && rows[0].Status == "confirm" && rows[0].NeedsKiz, "Lost WB status/SGTIN metadata.");
});
await Check("Ozon country-of-origin requirement does not require KIZ", async () =>
{
    var api = Api(_ => Json("{\"result\":{\"postings\":[{\"posting_number\":\"P\",\"status\":\"awaiting_packaging\",\"requirements\":{\"products_requiring_country\":[100]},\"products\":[{\"product_id\":100,\"offer_id\":\"A\",\"quantity\":1}]}],\"has_next\":false}}"));
    var rows = await api.SyncFbsAsync(Store(Marketplace.Ozon));
    Expect(rows.Count == 1 && !rows[0].NeedsKiz, "Country/GTD/UIN requirements must not be treated as mandatory KIZ.");
});
await Check("Yandex hasCis flag is used before instances exist", async () =>
{
    var api = Api(_ => Json("{\"orders\":[{\"id\":1,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2,\"hasCis\":true}]}]}"));
    var rows = await api.SyncFbsAsync(Store(Marketplace.Yandex));
    Expect(rows.Count == 1 && rows[0].NeedsKiz && rows[0].Quantity == 2, "hasCis/quantity was lost.");
});
foreach (var status in new[] { "awaiting_packaging", "awaiting_deliver" })
    await Check("Ozon ship readback " + status, async () =>
    {
        var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/ship")
            ? Json("{\"result\":[\"P\"]}")
            : Json("{\"result\":{\"status\":\"" + status + "\",\"substatus\":\"\"}}"));
        var order = new FbsOrderRow(1, Marketplace.Ozon, "P", "A", "A", 1, "awaiting_packaging", false,
            "{\"products\":[{\"sku\":100,\"quantity\":1}]}" );
        var packed = await api.PackOrderAsync(Store(Marketplace.Ozon), order);
        Expect(packed.Success == (status == "awaiting_deliver"), "Ship success must be verified by the posting state.");
    });
foreach (var lastState in new[] { "pending", "passed" })
    await Check("Ozon waits for every KIZ exemplar: " + lastState, async () =>
    {
        var api = Api(r => r.RequestUri!.AbsolutePath switch
        {
            "/v3/posting/fbs/get" => Json("{\"result\":{\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"product_id\":100,\"offer_id\":\"A\",\"quantity\":2}]}}"),
            "/v6/fbs/posting/product/exemplar/create-or-get" => Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":1},{\"exemplar_id\":2}]}]}"),
            "/v5/fbs/posting/product/exemplar/status" => Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":1,\"status\":\"passed\"},{\"exemplar_id\":2,\"status\":\"" + lastState + "\"}]}]}"),
            _ => Json("{}")
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await api.PrepareOzonKizAsync(Store(Marketplace.Ozon), "P",
            new Dictionary<string, IReadOnlyList<string>> { ["A"] = new[] { "code1", "code2" } }, timeout.Token);
        Expect(result.Success == (lastState == "passed"), "A single accepted exemplar must not release the whole posting.");
    });
await Check("Non-PDF success response is never sent to the label printer", async () =>
{
    var api = Api(_ => Json("{\"error\":\"label_not_ready\"}"));
    var label = await api.DownloadLabelAsync(Store(Marketplace.Ozon), "P");
    if (label.FilePath is not null) File.Delete(label.FilePath);
    Expect(!label.Success && label.FilePath is null, "JSON/error body was saved as an official PDF label.");
});
await Check("Yandex complete box sends one CIS per unit without removing items", async () =>
{
    var boxChecked = false;
    var api = Api(r =>
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/boxes"))
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            boxChecked = body["allowRemove"]!.GetValue<bool>() == false
                && body["boxes"]![0]!["items"]![0]!["instances"]!.AsArray().Count == 2
                && body["boxes"]![0]!["items"]![0]!["fullCount"]!.GetValue<int>() == 2;
            return Json("{\"status\":\"OK\"}");
        }
        return Json("{\"result\":{\"items\":[{\"instances\":[{\"cis\":\"code1\",\"status\":\"OK\"},{\"cis\":\"code2\",\"status\":\"OK\"}]}]}}");
    });
    var sample = new FbsOrderRow(1, Marketplace.Yandex, "1", "A", "A", 2, "PROCESSING/STARTED", true,
        "{\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2}]}" );
    var result = await api.PrepareYandexBoxesAsync(Store(Marketplace.Yandex), sample,
        new Dictionary<string, IReadOnlyList<string>> { ["A"] = new[] { "code1", "code2" } });
    Expect(result.Success && boxChecked, "Invalid box layout/CIS count.");
});
Console.WriteLine($"{10 - failures.Count}/10 contracts passed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
