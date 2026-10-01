using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Net;
using System.Reflection;
using System.Text;

var failures = new List<string>();
var checks=0;
async Task Check(string name, Func<Task> test)
{
    checks++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Expect(bool value, string message) { if (!value) throw new Exception(message); }
StoreProfile Store(Marketplace m) => new(1, m, "fixture", "123", "test-key", "456", "789", "test-token", true);
MarketplaceGateway Api(Func<HttpRequestMessage, HttpResponseMessage> respond,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    var api = new MarketplaceGateway(delay);
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
foreach (var lastState in new[] { "pending", "passed", "absent", "missing" })
    await Check("Ozon waits for every KIZ exemplar: " + lastState, async () =>
    {
        var confirmation = lastState == "absent" ? "{\"exemplar_id\":1,\"status\":\"passed\"}" : lastState == "missing" ? "{\"exemplar_id\":1,\"status\":\"passed\"},{\"exemplar_id\":2}" : "{\"exemplar_id\":1,\"status\":\"passed\"},{\"exemplar_id\":2,\"status\":\"" + lastState + "\"}";
        var api = Api(r => r.RequestUri!.AbsolutePath switch
        {
            "/v3/posting/fbs/get" => Json("{\"result\":{\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"product_id\":100,\"offer_id\":\"A\",\"quantity\":2}]}}"),
            "/v6/fbs/posting/product/exemplar/create-or-get" => Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":1},{\"exemplar_id\":2}]}]}"),
            "/v5/fbs/posting/product/exemplar/status" => Json("{\"products\":[{\"product_id\":100,\"exemplars\":[" + confirmation + "]}]}"),
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
await Check("Ozon diagnostics rejects empty JSON instead of reporting green", async () =>
{
    var api = Api(_ => Json("{}"));
    var report = await api.DiagnoseOzonAsync(Store(Marketplace.Ozon));
    Expect(report.Steps.Count == 1 && !report.Steps[0].Success && report.Steps[0].Code == "invalid_schema",
        "A 2xx empty object was reported as a healthy Ozon endpoint.");
});
await Check("Ozon errors never expose raw KIZ response bodies", async () =>
{
    const string secret = "010460123456789321SERIAL-SECRET";
    var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/get")
        ? Json("{\"result\":{\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"product_id\":100,\"offer_id\":\"A\",\"quantity\":1}]}}")
        : Json("{\"error\":\"" + secret + "\"}", HttpStatusCode.BadRequest));
    var result = await api.PrepareOzonKizAsync(Store(Marketplace.Ozon), "P",
        new Dictionary<string, IReadOnlyList<string>> { ["A"] = new[] { secret } });
    Expect(!result.Success && !result.Message.Contains(secret, StringComparison.Ordinal), "Raw Ozon body leaked into a user-visible error.");
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
await Check("WB rejects a sticker belonging to another order", async () =>
{
    var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/status")
        ? Json("{\"orders\":[{\"id\":101,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}")
        : Json("{\"stickers\":[{\"orderId\":999,\"file\":\"iVBORw0KGgo=\"}]}"));
    var label = await api.DownloadLabelAsync(Store(Marketplace.Wildberries), "101");
    if (label.FilePath is not null) File.Delete(label.FilePath);
    Expect(!label.Success, "A foreign sticker was accepted for order 101.");
});
await Check("WB new order explains sticker readiness without requesting stickers", async () =>
{
    var stickerRequests = 0;
    var api = Api(r =>
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/status"))
            return Json("{\"orders\":[{\"id\":101,\"supplierStatus\":\"new\",\"wbStatus\":\"waiting\"}]}");
        stickerRequests++; return Json("{\"stickers\":[]}");
    });
    var label = await api.DownloadLabelAsync(Store(Marketplace.Wildberries), "101");
    Expect(!label.Success && label.Message.Contains("new") && stickerRequests == 0, "New orders should be packed before requesting their sticker.");
});
await Check("WB 119 labels use two 100-ID batches and match reversed responses", async () =>
{
    var stickerCalls = 0; var statusCalls = 0;
    var api = Api(r =>
    {
        var ids = System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray().Select(x => x!.GetValue<long>()).ToArray();
        Expect(ids.Length <= 100, "WB batch exceeds 100 integer IDs.");
        if (r.RequestUri!.AbsolutePath.EndsWith("/status"))
        {
            statusCalls++;
            return Json(System.Text.Json.JsonSerializer.Serialize(new { orders = ids.Select(id => new { id, supplierStatus = "confirm", wbStatus = "waiting" }) }));
        }
        stickerCalls++;
        return Json(System.Text.Json.JsonSerializer.Serialize(new { stickers = ids.Reverse().Select(id => new { orderId = id, barcode = "WB-" + id, partA = "12", partB = id.ToString(), file = "" }) }));
    }, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    var ids = Enumerable.Range(1, 119).Select(x => x.ToString()).ToArray();
    var result = await api.DownloadLabelsAsync(Store(Marketplace.Wildberries), ids);
    Expect(stickerCalls == 2 && statusCalls == 2 && result.Count == 119, "119 labels must use 2 sticker calls plus 2 readiness calls.");
    Expect(ids.All(id => result[id].Success && result[id].Barcode == "WB-" + id), "Reordered response swapped stickers.");
    await api.DownloadLabelsAsync(Store(Marketplace.Wildberries), ids);
    Expect(stickerCalls == 2 && statusCalls == 4, "Reprint must reuse sticker data after fresh status validation.");
});
await Check("WB retry waits for the larger retry header and repeats the same batch", async () =>
{
    var calls = 0; var delays = new List<TimeSpan>();
    var api = Api(r =>
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/status"))
            return Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}");
        calls++;
        if (calls == 1)
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-Ratelimit-Retry", "7");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
            return response;
        }
        return Json("{\"stickers\":[{\"orderId\":1,\"barcode\":\"official\",\"partA\":\"1\",\"partB\":\"2\"}]}");
    }, (delay, ct) => { ct.ThrowIfCancellationRequested(); delays.Add(delay); return Task.CompletedTask; });
    var result = await api.DownloadLabelsAsync(Store(Marketplace.Wildberries), new[] { "1" });
    Expect(result["1"].Success && calls == 2 && delays.Any(x => x.TotalSeconds > 10.9), "Retry ignored WB rate-limit headers.");
});
await Check("WB cancellation stops rate-limit retry and later batches", async () =>
{
    using var cancel = new CancellationTokenSource(); var calls = 0;
    var api = Api(_ =>
    {
        calls++; var response = Json("{}", HttpStatusCode.TooManyRequests);
        response.Headers.Add("X-Ratelimit-Retry", "30"); return response;
    }, (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    var stopped = false;
    try { await api.DownloadLabelsAsync(Store(Marketplace.Wildberries), Enumerable.Range(1,119).Select(x => x.ToString()), cancel.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Expect(stopped && calls == 1, "Cancelled job continued issuing WB requests.");
});
await Check("WB unknown status, partial payload and invalid PNG never become labels", async () =>
{
    foreach (var payload in new[] { "{\"stickers\":[]}", "{\"stickers\":[{\"orderId\":1,\"barcode\":\"x\"}]}", "{\"stickers\":[{\"orderId\":1,\"file\":\"e30=\"}]}" })
    {
        var api = Api(r => r.RequestUri!.AbsolutePath.EndsWith("/status")
            ? Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}") : Json(payload), (_,_) => Task.CompletedTask);
        Expect(!(await api.DownloadLabelAsync(Store(Marketplace.Wildberries), "1")).Success, "Incomplete/invalid sticker was accepted.");
    }
    var unknown = Api(_ => Json("{\"orders\":[]}"));
    Expect(!(await unknown.DownloadLabelAsync(Store(Marketplace.Wildberries), "1")).Success, "Unknown status was accepted.");
});
await Check("WB cached sticker is blocked after cancellation and isolated across tokens", async () =>
{
    var cancelled = false; var stickers = 0;
    var api = Api(r =>
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/status")) return Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"" + (cancelled ? "cancel" : "confirm") + "\",\"wbStatus\":\"waiting\"}]}");
        stickers++; return Json("{\"stickers\":[{\"orderId\":1,\"barcode\":\"x\",\"partA\":\"1\",\"partB\":\"2\"}]}");
    }, (_,_) => Task.CompletedTask);
    var store = Store(Marketplace.Wildberries);
    Expect((await api.DownloadLabelAsync(store, "1")).Success, "Fixture failed.");
    await api.DownloadLabelAsync(store with { Token = "another-shop" }, "1");
    Expect(stickers == 2, "Sticker cache leaked across store credentials.");
    cancelled = true;
    Expect(!(await api.DownloadLabelAsync(store, "1")).Success && stickers == 2, "Cancelled order reused a cached sticker.");
});
await Check("WB print KIZ uses batched current metadata and preserves GS", async () =>
{
    const string code = "010460123456789321serial\u001d91ABCD\u001d92proof";
    var api = Api(r =>
    {
        Expect(r.RequestUri!.AbsolutePath == "/api/marketplace/v3/orders/meta", "Print KIZ used deprecated single-order metadata API.");
        return Json(System.Text.Json.JsonSerializer.Serialize(new {orders=new[] {new {id=1,metaDetails=new[] {new {key="sgtin",value=new[]{code},decision="sgtinMaySell"}}}}}));
    }, (_,_) => Task.CompletedTask);
    var result = await api.GetWbPrintKizAsync(Store(Marketplace.Wildberries),new[]{"1"});
    Expect(result["1"].Codes.Single()==code,"Print KIZ differs from the code attached on WB.");
});
await Check("WB fresh required KIZ survives null values and optional empty is ignored", async () =>
{
    var api=Api(_=>Json("{\"orders\":[{\"id\":1,\"metaDetails\":[{\"key\":\"sgtin\",\"value\":null,\"decision\":\"required\"}]},{\"id\":2,\"metaDetails\":[{\"key\":\"sgtin\",\"value\":\"\",\"decision\":\"optional\"}]}]}"));
    var result=await api.GetWbPrintKizAsync(Store(Marketplace.Wildberries),new[]{"1","2"});
    Expect(result["1"].Required && result["1"].Codes.Count==0 && !result["2"].Required && result["2"].Codes.Count==0,"Fresh required/optional KIZ requirements were lost.");
});
await Check("WB invalid or pending KIZ decision blocks print preparation", async () =>
{
    foreach(var decision in new[]{"invalid","pending","unknown-new-status"})
    {
        var api=Api(_=>Json("{\"orders\":[{\"id\":1,\"metaDetails\":[{\"key\":\"sgtin\",\"value\":\"code\",\"decision\":\""+decision+"\"}]}]}"));
        var rejected=false;try{await api.GetWbPrintKizAsync(Store(Marketplace.Wildberries),new[]{"1"});}catch(InvalidDataException){rejected=true;}
        Expect(rejected,"Unvalidated KIZ was allowed to print.");
    }
});
await Check("WB 119 selected orders are added to one shipment in two batches", async () =>
{
    var members=new HashSet<long>();var creates=0;var batches=new List<int>();
    var api=Api(r=>
    {
        var path=r.RequestUri!.AbsolutePath;
        if(path.EndsWith("/status")) {
            var ids=System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray();
            return Json(System.Text.Json.JsonSerializer.Serialize(new{orders=ids.Select(x=>new{id=x!.GetValue<long>(),supplierStatus=members.Contains(x.GetValue<long>())?"confirm":"new",wbStatus="waiting"})}));
        }
        if(r.Method==HttpMethod.Post && path.EndsWith("/supplies")){creates++;return Json("{\"id\":\"WB-GI-TEST\"}",HttpStatusCode.Created);}
        if(r.Method.Method=="PATCH"){
            var ids=System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray().Select(x=>x!.GetValue<long>()).ToArray();
            batches.Add(ids.Length);foreach(var id in ids)members.Add(id);return Json("",HttpStatusCode.NoContent);
        }
        if(path.EndsWith("/order-ids"))return Json(System.Text.Json.JsonSerializer.Serialize(new{orderIds=members}));
        return Json("{\"id\":\"WB-GI-TEST\",\"done\":false,\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}");
    },(_,_)=>Task.CompletedTask);
    var orders=Enumerable.Range(1,119).Select(id=>new FbsOrderRow(1,Marketplace.Wildberries,id.ToString(),"A","A",1,"new",false,"{}")).ToArray();
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),orders);
    Expect(result.Success && creates==1 && batches.SequenceEqual(new[]{100,19}) && members.Count==119,"119-order shipment was rejected, recreated or incomplete: "+result.Message);
});
await Check("WB invalid or foreign-store order IDs never create a shipment", async () =>
{
    foreach(var bad in new[]{new FbsOrderRow(1,Marketplace.Wildberries,"invalid","A","A",1,"new",false,"{}"),new FbsOrderRow(2,Marketplace.Wildberries,"2","A","A",1,"new",false,"{}")}) {
        var creates=0;
        var api=Api(r=> {if(r.Method==HttpMethod.Post){creates++;return Json("{\"id\":\"WB-GI-TEST\"}",HttpStatusCode.Created);}return Json("",HttpStatusCode.NoContent);},(_,_)=>Task.CompletedTask);
        var valid=new FbsOrderRow(1,Marketplace.Wildberries,"1","A","A",1,"new",false,"{}");
        var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),new[]{valid,bad});
        Expect(!result.Success && creates==0,"Invalid/foreign ID was silently dropped or added to this shop.");
    }
});

await Check("WB today shipment picker paginates and uses Moscow day", async () =>
{
    var calls=0;var now=new DateTimeOffset(2026,10,1,21,30,0,TimeSpan.Zero);
    var api=Api(r=>{
        calls++;
        return calls==1 ? Json("{\"next\":42,\"supplies\":[{\"id\":\"TODAY\",\"name\":\"today\",\"done\":false,\"createdAt\":\"2026-10-01T21:05:00Z\"},{\"id\":\"YESTERDAY\",\"done\":false,\"createdAt\":\"2026-10-01T20:59:00Z\"}]}")
            : Json("{\"next\":0,\"supplies\":[{\"id\":\"CLOSED\",\"done\":true,\"createdAt\":\"2026-10-01T22:00:00Z\"},{\"id\":\"TODAY-2\",\"done\":false,\"createdAt\":\"2026-10-01T21:10:00Z\"}]}");
    },(_,_)=>Task.CompletedTask);
    var result=await api.GetWbTodaySuppliesAsync(Store(Marketplace.Wildberries),now:now);
    Expect(calls==2 && result.Select(x=>x.Id).ToHashSet().SetEquals(new[]{"TODAY","TODAY-2"}),"Closed/yesterday supplies included or pagination stopped early.");
});
await Check("WB existing shipment is rechecked before adding and never recreated", async () =>
{
    foreach(var invalid in new[]{false,true}) {
        var creates=0;var adds=0;var members=new HashSet<long>();
        var api=Api(r=>{
            var path=r.RequestUri!.AbsolutePath;
            if(path.EndsWith("/status"))return Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\""+(members.Contains(1)?"confirm":"new")+"\",\"wbStatus\":\"waiting\"}]}");
            if(r.Method==HttpMethod.Post){creates++;return Json("{\"id\":\"NEW\"}",HttpStatusCode.Created);}
            if(r.Method.Method=="PATCH"){adds++;members.Add(1);return Json("",HttpStatusCode.NoContent);}
            if(path.EndsWith("/order-ids"))return Json(System.Text.Json.JsonSerializer.Serialize(new{orderIds=members}));
            return Json("{\"id\":\"EXISTING\",\"done\":"+(invalid?"true":"false")+",\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}");
        },(_,_)=>Task.CompletedTask);
        var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),new[]{new FbsOrderRow(1,Marketplace.Wildberries,"1","A","A",1,"new",false,"{}")},existingSupplyId:"EXISTING");
        Expect(result.Success==!invalid && creates==0 && adds==(invalid?0:1),"Existing supply was recreated or closed supply was used.");
    }
});
await Check("WB partial second batch failure keeps the shipment and first hundred orders", async () =>
{
    var creates=0;var adds=0;var members=new HashSet<long>();
    var api=Api(r=>{
        var path=r.RequestUri!.AbsolutePath;
        if(path.EndsWith("/status")){var ids=System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray();return Json(System.Text.Json.JsonSerializer.Serialize(new{orders=ids.Select(x=>new{id=x!.GetValue<long>(),supplierStatus="new",wbStatus="waiting"})}));}
        if(r.Method==HttpMethod.Post){creates++;return Json("{\"id\":\"PARTIAL\"}",HttpStatusCode.Created);}
        if(r.Method.Method=="PATCH") {
            adds++;if(adds==2)return Json("{\"message\":\"warehouse mismatch\"}",HttpStatusCode.Conflict);
            foreach(var x in System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray())members.Add(x!.GetValue<long>());
            return Json("",HttpStatusCode.NoContent);
        }
        return Json(System.Text.Json.JsonSerializer.Serialize(new{orderIds=members}));
    },(_,_)=>Task.CompletedTask);
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),Enumerable.Range(1,119).Select(id=>new FbsOrderRow(1,Marketplace.Wildberries,id.ToString(),"A","A",1,"new",false,"{}")).ToArray());
    Expect(!result.Success && result.ExternalTaskId=="PARTIAL" && creates==1 && adds==2 && members.Count==100 && result.Message.Contains("100/119"),"Partial shipment lost its checkpoint or continued after failure.");
});
await Check("WB empty membership readback blocks shipment success", async () =>
{
    var api=Api(r=>r.RequestUri!.AbsolutePath.EndsWith("/status")?Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"new\",\"wbStatus\":\"waiting\"}]}"):
        r.Method==HttpMethod.Post?Json("{\"id\":\"READBACK\"}",HttpStatusCode.Created):r.Method.Method=="PATCH"?Json("",HttpStatusCode.NoContent):Json("{\"orderIds\":[]}"),(_,_)=>Task.CompletedTask);
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),new[]{new FbsOrderRow(1,Marketplace.Wildberries,"1","A","A",1,"new",false,"{}")});
    Expect(!result.Success && result.ExternalTaskId=="READBACK","PATCH acceptance was mistaken for verified shipment membership.");
});
await Check("WB cancelled order never creates or modifies a shipment", async () =>
{
    var mutations=0;var api=Api(r=>{if(r.Method!=HttpMethod.Get && !r.RequestUri!.AbsolutePath.EndsWith("/status"))mutations++;return Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"new\",\"wbStatus\":\"canceled\"}]}");},(_,_)=>Task.CompletedTask);
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),new[]{new FbsOrderRow(1,Marketplace.Wildberries,"1","A","A",1,"new",false,"{}")});
    Expect(!result.Success && mutations==0,"Cancelled order was packed.");
});
await Check("WB retry resumes confirmed orders already in the selected supply", async () =>
{
    var mutations=0;var api=Api(r=>{
        var p=r.RequestUri!.AbsolutePath;
        if(p.EndsWith("/status"))return Json("{\"orders\":[{\"id\":1,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}");
        if(r.Method!=HttpMethod.Get)mutations++;
        if(p.EndsWith("/order-ids"))return Json("{\"orderIds\":[1]}");
        return Json("{\"id\":\"RETRY\",\"done\":false,\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}");
    },(_,_)=>Task.CompletedTask);
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),new[]{new FbsOrderRow(1,Marketplace.Wildberries,"1","A","A",1,"confirm",false,"{}")},existingSupplyId:"RETRY");
    Expect(result.Success && mutations==0,"Retry moved/readded confirmed orders or created another supply.");
});

await Check("WB final shipment membership cannot lose an earlier batch", async () =>
{
    var batchNo=0;var members=new HashSet<long>();
    var api=Api(r=>{
        var path=r.RequestUri!.AbsolutePath;
        if(path.EndsWith("/status")) {
            var ids=System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray();
            return Json(System.Text.Json.JsonSerializer.Serialize(new{orders=ids.Select(x=>new{id=x!.GetValue<long>(),supplierStatus=batchNo==0?"new":"confirm",wbStatus="waiting"})}));
        }
        if(r.Method==HttpMethod.Post)return Json("{\"id\":\"LOST-BATCH\"}",HttpStatusCode.Created);
        if(r.Method.Method=="PATCH") {
            batchNo++;members.Clear();
            foreach(var x in System.Text.Json.Nodes.JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray())members.Add(x!.GetValue<long>());
            return Json("",HttpStatusCode.NoContent);
        }
        return Json(System.Text.Json.JsonSerializer.Serialize(new{orderIds=members}));
    },(_,_)=>Task.CompletedTask);
    var result=await api.CreateShipmentAsync(Store(Marketplace.Wildberries),Enumerable.Range(1,119).Select(id=>new FbsOrderRow(1,Marketplace.Wildberries,id.ToString(),"A","A",1,"new",false,"{}")).ToArray());
    Expect(!result.Success && result.ExternalTaskId=="LOST-BATCH" && batchNo==2,"A shipment missing the first hundred orders was reported successful.");
});

Console.WriteLine($"{checks - failures.Count}/{checks} contracts passed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
