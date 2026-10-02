using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

var failures = new List<string>();
var checks = 0;
async Task Check(string name, Func<Task> test)
{
    checks++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Expect(bool value, string message) { if (!value) throw new Exception(message); }
StoreProfile Store(Marketplace m) => new(1, m, "fixture", "123", "test-key", "456", "789", "test-token", true);
MarketplaceGateway Api(Func<HttpRequestMessage, HttpResponseMessage> respond)
{
    var api = new MarketplaceGateway((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    typeof(MarketplaceGateway).GetField("http", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(api, new HttpClient(new FixtureHttp(respond)));
    return api;
}
HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

await Check("WB GTIN writeback retains every size and existing barcode",()=>{
    var type=typeof(ProductCatalog).Assembly.GetType("MarketplaceHub.Services.WbGtinPayloads");
    var method=type?.GetMethod("BuildWbGtinCard");Expect(method is not null,"Full-size WB GTIN writeback builder is missing.");
    var raw="{\"nmID\":900,\"vendorCode\":\"A\",\"title\":\"Áo\",\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"old-barcode\"]},{\"chrtID\":2,\"techSize\":\"50\",\"skus\":[\"other-size\"]}]}";
    var payload=(JsonObject)method!.Invoke(null,new object[]{raw,new Dictionary<string,string>{{"1","04601234567893"}}})!;
    var sizes=payload["sizes"]!.AsArray();Expect(sizes.Count==2&&sizes[0]!["skus"]!.AsArray().Select(x=>x!.ToString()).SequenceEqual(new[]{"old-barcode","04601234567893"})&&sizes[1]!["skus"]![0]!.ToString()=="other-size","Writeback dropped another size or replaced an existing WB barcode.");
    var invalid=false;try{method.Invoke(null,new object[]{raw,new Dictionary<string,string>{{"1","04601234567894"}}});}catch(TargetInvocationException ex){invalid=ex.InnerException is InvalidOperationException;}Expect(invalid,"Writeback accepted invalid GTIN checksum.");return Task.CompletedTask;
});

await Check("Catalog retries 429 at the same cursor", async () =>
{
    var requests = new List<string>();
    var api = Api(r =>
    {
        if (r.RequestUri!.Host.Contains("discounts-prices")) return Json("{\"data\":{\"listGoods\":[]}}");
        requests.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        if (requests.Count == 1)
        {
            var limited = Json("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "1"); return limited;
        }
        return Json("{\"cards\":[{\"nmID\":101,\"vendorCode\":\"A\",\"sizes\":[{\"chrtID\":1,\"techSize\":\"M\",\"skus\":[\"4601234567893\"]}]}],\"cursor\":{\"total\":1}}");
    });
    var products = await api.SyncProductsAsync(Store(Marketplace.Wildberries));
    Expect(products.Count == 1 && requests.Count == 2 && requests[0] == requests[1], "429 lost the current page or restarted the traversal.");
});
await Check("Malformed success body cannot become an empty catalog", async () =>
{
    var api = Api(_ => Json("{\"error\":true,\"message\":\"upstream partial failure\"}"));
    var rejected = false;
    try { await api.SyncProductsAsync(Store(Marketplace.Yandex)); }
    catch (InvalidDataException) { rejected = true; }
    Expect(rejected, "Malformed API response was treated as a successful empty catalog.");
});
await Check("Ozon catalog image follows returned offer identity", async () =>
{
    var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/product/list")
        ? Json("{\"result\":{\"items\":[{\"product_id\":1,\"offer_id\":\"A\"},{\"product_id\":2,\"offer_id\":\"B\"}],\"last_id\":\"\",\"total\":2}}")
        : Json("{\"items\":[{\"id\":2,\"offer_id\":\"B\",\"primary_image\":[\"https://img.example/B.jpg\"]},{\"id\":1,\"offer_id\":\"A\",\"primary_image\":[\"https://img.example/A.jpg\"]}]}"));
    var products = await api.SyncProductsAsync(Store(Marketplace.Ozon));
    Expect(products.Single(p => p.Sku == "A").ImageUrl == "https://img.example/A.jpg"
        && products.Single(p => p.Sku == "B").ImageUrl == "https://img.example/B.jpg", "Primary images were missing or assigned by list position.");
});
await Check("Incomplete Ozon detail batch cannot publish a partial catalog", async () =>
{
    var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/product/list")
        ? Json("{\"result\":{\"items\":[{\"product_id\":1,\"offer_id\":\"A\"},{\"product_id\":2,\"offer_id\":\"B\"}],\"last_id\":\"\",\"total\":2}}")
        : Json("{\"items\":[{\"id\":1,\"offer_id\":\"A\",\"images\":[\"https://img.example/A.jpg\"]}]}"));
    var rejected = false;
    try { await api.SyncProductsAsync(Store(Marketplace.Ozon)); }
    catch (InvalidDataException) { rejected = true; }
    Expect(rejected, "Missing detail rows were accepted and could erase the cached product B.");
});

await Check("Ozon missing cursor and understated terminal count cannot end synchronization",async()=>{
    foreach(var body in new[]{"{\"result\":{\"items\":[{\"product_id\":1,\"offer_id\":\"A\"}],\"total\":1}}","{\"result\":{\"items\":[{\"product_id\":1,\"offer_id\":\"A\"}],\"last_id\":\"\",\"total\":2}}"}) {
        var api=Api(r=>r.RequestUri!.AbsolutePath.EndsWith("/product/list")?Json(body):Json("{\"items\":[{\"id\":1,\"offer_id\":\"A\"}]}"));var rejected=false;
        try{await api.SyncProductsAsync(Store(Marketplace.Ozon));}catch(InvalidDataException){rejected=true;}Expect(rejected,"Incomplete catalog became complete.");
    }
});
await Check("Multiple distinct GTINs require an explicit variant instead of the first barcode",()=>{
    Expect(ProductCatalog.UniqueGtin(new[]{"4601234567893","4601234567886"})=="" && ProductCatalog.UniqueGtin(new[]{"4601234567893","04601234567893"})=="04601234567893" && ProductCatalog.NormalizeGtin("4601234567890")=="","Ambiguous or invalid barcode passed.");return Task.CompletedTask;
});
await Check("Yandex marketSku mapping and offer photos preserve seller identity",async()=>{
    var api=Api(_=>Json("{\"status\":\"OK\",\"result\":{\"offerMappings\":[{\"offer\":{\"offerId\":\"A\",\"name\":\"A\",\"barcodes\":[\"4601234567893\"],\"mediaFiles\":{\"pictures\":[\"https://img.example/A.jpg\"]}},\"mapping\":{\"marketSku\":123}}]}}"));
    var row=(await api.SyncProductsAsync(Store(Marketplace.Yandex))).Single();Expect(row.ExternalId=="A" && row.ImageUrl=="https://img.example/A.jpg" && row.RawJson.Contains("123"),"Mapping or image moved away from the exact offer.");
});
await Check("WB catalog refresh enriches size prices without dropping sizes",async()=>{
    var api=Api(r=>Json(r.RequestUri!.Host.Contains("discounts-prices")?"{\"data\":{\"listGoods\":[{\"nmID\":101,\"sizes\":[{\"sizeID\":1,\"price\":1000,\"discountedPrice\":800}]}]}}":"{\"cards\":[{\"nmID\":101,\"vendorCode\":\"A\",\"sizes\":[{\"chrtID\":1,\"skus\":[\"4601234567893\"]},{\"chrtID\":2,\"skus\":[\"4601234567886\"]}]}],\"cursor\":{\"total\":1}}"));
    var row=(await api.SyncProductsAsync(Store(Marketplace.Wildberries))).Single();Expect(row.Price==1000 && ProductCatalog.Entry(row).Variants.Count==2,"Price enrichment lost sizes or price.");
});
Console.WriteLine($"{checks - failures.Count}/{checks} product synchronization checks passed");
return failures.Count == 0 ? 0 : 1;

sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
}
