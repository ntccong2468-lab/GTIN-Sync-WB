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
StoreProfile Store(Marketplace m) => new(1,m,"fixture","123","key","456","789","token",true);
MarketplaceGateway Api(Func<HttpRequestMessage,HttpResponseMessage> responder)
{
    var api=new MarketplaceGateway((_,_)=>Task.CompletedTask);
    typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new FixtureHttp(responder)));
    return api;
}
HttpResponseMessage Json(string body,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
string Ozon(string status="awaiting_packaging",string products="[{\"sku\":100,\"offer_id\":\"A\",\"quantity\":2},{\"sku\":200,\"offer_id\":\"B\",\"quantity\":1}]")=>"{\"result\":{\"posting_number\":\"P\",\"status\":\""+status+"\",\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":"+products+"}}";
string Yandex(string status="PROCESSING",string substatus="STARTED",string more="")=>"{\"order\":{\"id\":7,\"status\":\""+status+"\",\"substatus\":\""+substatus+"\",\"items\":[{\"id\":11,\"offerId\":\"A\",\"count\":2,\"requiredInstanceTypes\":[\"CIS\"]},{\"id\":12,\"offerId\":\"A\",\"count\":1}]"+more+"}}";

await Check("Fresh Ozon detail contains all lines and only mandatory marks",async()=>{
    var snapshot=await Api(_=>Json(Ozon())).ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");
    Expect(snapshot.Items.Count==2 && snapshot.Items[0].Quantity==2 && snapshot.Items[0].RequiresKiz && !snapshot.Items[1].RequiresKiz,"Complete detail/requirements lost.");
});
foreach(var status in new[]{"cancelled","","unknown","awaiting_registration"})
await Check("Ozon cannot pack status "+status,async()=>{
    var snapshot=await Api(_=>Json(Ozon(status))).ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");
    Expect(!snapshot.CanPack,"Invalid status authorizes allocation/ship.");
});
await Check("Missing Ozon requirements cannot authorize packing",async()=>{
    var rejected=false;try {await Api(_=>Json("{\"result\":{\"posting_number\":\"P\",\"status\":\"awaiting_packaging\",\"products\":[{\"sku\":1,\"offer_id\":\"A\",\"quantity\":1}]}}")).ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");}catch(InvalidOperationException){rejected=true;}
    Expect(rejected,"Unknown requirements became optional.");
});
await Check("Detail with wrong platform identity is rejected",async()=>{
    var rejected=false;try{await Api(_=>Json(Yandex())).ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"8");}catch(InvalidOperationException){rejected=true;}
    Expect(rejected,"Another order's detail was trusted.");
});
await Check("Ozon whole posting builder never silently skips invalid products",async()=>{
    var snapshot=await Api(_=>Json(Ozon())).ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");
    var body=MarketplaceFbsPayloads.BuildOzonPackage(snapshot);
    Expect(body["packages"]![0]!["products"]!.AsArray().Count==2,"Partial posting shipped.");
    var rejected=false;try{await Api(_=>Json(Ozon(products:"[{\"offer_id\":\"A\",\"quantity\":1}]"))).ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");}catch(InvalidOperationException){rejected=true;}
    Expect(rejected,"Invalid product silently skipped.");
});
await Check("Yandex two same-SKU item IDs retain separate per-unit codes and full order",async()=>{
    var snapshot=await Api(_=>Json(Yandex())).ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");
    var body=MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,new Dictionary<string,IReadOnlyList<string>>{["11"]=new[]{"c1","c2"}});
    var items=body["boxes"]![0]!["items"]!.AsArray();
    Expect(!body["allowRemove"]!.GetValue<bool>() && items.Count==2 && items[0]!["instances"]!.AsArray().Count==2 && items[1]!["fullCount"]!.GetValue<int>()==1,"Missing full order/per-item identity.");
});
await Check("Yandex preserves explicit multiple box distribution and custom identifiers",async()=>{
    var snapshot=await Api(_=>Json(Yandex())).ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");
    var layout=JsonNode.Parse("[{\"items\":[{\"id\":11,\"fullCount\":1,\"instances\":[{\"countryCode\":\"RU\"}]}]},{\"items\":[{\"id\":11,\"fullCount\":1},{\"id\":12,\"fullCount\":1}]}]")!.AsArray();
    var body=MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,new Dictionary<string,IReadOnlyList<string>>{["11"]=new[]{"c1","c2"}},layout);
    Expect(body["boxes"]!.AsArray().Count==2 && body["boxes"]![0]!["items"]![0]!["instances"]![0]!["countryCode"]!.ToString()=="RU" && body["boxes"]![1]!["items"]![0]!["instances"]![0]!["cis"]!.ToString()=="c2","Boxes or instance metadata overwritten.");
});
await Check("Yandex refuses incomplete distribution or duplicate codes",async()=>{
    var snapshot=await Api(_=>Json(Yandex())).ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");
    foreach(var duplicate in new[]{false,true}){
        var rejected=false;try{MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,new Dictionary<string,IReadOnlyList<string>>{["11"]=duplicate?new[]{"c1","c1"}:new[]{"c1","c2"}},duplicate?null:JsonNode.Parse("[{\"items\":[{\"id\":11,\"fullCount\":2}]}]")!.AsArray());}catch(InvalidOperationException){rejected=true;}
        Expect(rejected,"Incomplete order or duplicate unit codes accepted.");
    }
});
await Check("Yandex API envelope OK never replaces exact code verification",async()=>{
    var snapshot=await Api(_=>Json(Yandex())).ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");
    var codes=new Dictionary<string,IReadOnlyList<string>>{["11"]=new[]{"c1","c2"}};
    Expect(!MarketplaceFbsPayloads.YandexCodesAccepted(JsonNode.Parse("{\"status\":\"OK\",\"result\":{\"items\":[{\"id\":11,\"cis\":[{\"value\":\"unrelated\",\"status\":\"OK\"},{\"value\":\"c1\",\"status\":\"OK\"}]}]}}"),codes),"Unrelated/envelope OK passed missing code.");
    Expect(MarketplaceFbsPayloads.YandexCodesAccepted(JsonNode.Parse("{\"status\":\"OK\",\"result\":{\"items\":[{\"id\":11,\"cis\":[{\"value\":\"c2\",\"status\":\"OK\"},{\"value\":\"c1\",\"status\":\"OK\"}]}]}}"),codes),"All exact codes should pass.");
});
await Check("Yandex READY_TO_SHIP requires platform readback",async()=>{
    foreach(var ready in new[]{false,true}){
        var api=Api(r=>Json(r.Method==HttpMethod.Put?"{\"status\":\"OK\"}":Yandex(substatus:ready?"READY_TO_SHIP":"STARTED")));
        var snapshot=await api.ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");
        var result=await api.ConfirmMarketplaceFbsAsync(Store(Marketplace.Yandex),snapshot);
        Expect(result.Success==ready,"200 without READY_TO_SHIP readback passed.");
    }
});

await Check("Ozon mark proof needs ship_available and stable full codes",async()=>{
    const string full="010460123456789321serial\u001d91ABCD\u001d92proof";
    foreach(var state in new[]{"ship_available","validation_in_process"}) {
        var details=new JsonObject{["products"]=new JsonArray(new JsonObject{["product_id"]=100,["exemplars"]=new JsonArray(new JsonObject{["exemplar_id"]=11,["marks"]=new JsonArray(new JsonObject{["mark_type"]="mandatory_mark",["mark"]=full})},new JsonObject{["exemplar_id"]=12,["marks"]=new JsonArray(new JsonObject{["mark_type"]="mandatory_mark",["mark"]=full+"2"})})})};
        var status=JsonNode.Parse("{\"status\":\""+state+"\",\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":11,\"marks\":[{\"check_status\":\"passed\"}]},{\"exemplar_id\":12,\"marks\":[{\"check_status\":\"passed\"}]}]}]}")!;
        var api=Api(r=>Json(r.RequestUri!.AbsolutePath.EndsWith("/get")?Ozon():r.RequestUri.AbsolutePath.EndsWith("/status")?status.ToJsonString():details.ToJsonString()));
        var snapshot=await api.ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");var proof=await api.ReadMarketplaceKizAsync(Store(Marketplace.Ozon),snapshot);
        Expect(proof.Verified==(state=="ship_available") && proof.Codes["100"][0]==full,"Pending status was trusted or crypto tail was lost.");
    }
});
await Check("Ozon rejected mark, wrong reported code and changing exemplar block proof",async()=>{
    foreach(var mode in new[]{"error","wrong-code","changing"}) {
        var count=0;var details="{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":11,\"marks\":[{\"mark\":\"code1\"}]},{\"exemplar_id\":12,\"marks\":[{\"mark\":\"code2\"}]}]}]}";
        var status=JsonNode.Parse("{\"status\":\"ship_available\",\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":11,\"marks\":[{\"check_status\":\"passed\"}]},{\"exemplar_id\":12,\"marks\":[{\"check_status\":\"passed\"}]}]}]}")!;
        if(mode=="error")status["error_codes"]=new JsonArray("REJECTED");
        if(mode=="wrong-code")status["products"]![0]!["exemplars"]![0]!["marks"]![0]!["mark"]="unrelated";
        var api=Api(r=>{if(r.RequestUri!.AbsolutePath.EndsWith("/get"))return Json(Ozon());if(r.RequestUri.AbsolutePath.EndsWith("/status"))return Json(status.ToJsonString());count++;return Json(mode=="changing" && count>1?details.Replace("code1","different"):details);});
        var snapshot=await api.ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P");Expect(!(await api.ReadMarketplaceKizAsync(Store(Marketplace.Ozon),snapshot)).Verified,"Unsafe proof passed: "+mode);
    }
});
await Check("Ozon multibox and third-party delivery are not collapsed into one package",async()=>{
    foreach(var field in new[]{"\"is_multibox\":true,","\"tpl_integration_type\":\"external\","}) {
        var api=Api(_=>Json(Ozon().Replace("\"posting_number\"",field+"\"posting_number\"")));
        Expect(!(await api.ReadMarketplaceFbsAsync(Store(Marketplace.Ozon),"P")).CanPack,"Unsupported packing shape was silently simplified.");
    }
});
await Check("Yandex rejects mixing two partial products in one box",async()=>{
    var api=Api(_=>Json("{\"order\":{\"id\":7,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":11,\"offerId\":\"A\",\"count\":1},{\"id\":12,\"offerId\":\"B\",\"count\":1}]}}"));
    var snapshot=await api.ReadMarketplaceFbsAsync(Store(Marketplace.Yandex),"7");var rejected=false;
    var layout=JsonNode.Parse("[{\"items\":[{\"id\":11,\"partialCount\":{\"current\":1,\"total\":2}},{\"id\":12,\"partialCount\":{\"current\":1,\"total\":2}}]},{\"items\":[{\"id\":11,\"partialCount\":{\"current\":2,\"total\":2}}]},{\"items\":[{\"id\":12,\"partialCount\":{\"current\":2,\"total\":2}}]}]")!.AsArray();
    try{MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,new Dictionary<string,IReadOnlyList<string>>(),layout);}catch(InvalidOperationException){rejected=true;}Expect(rejected,"Yandex partial-box rule was ignored.");
});
await Check("Scanner prefix normalization keeps internal separators and crypto tail",()=>{
    const string code="010460123456789321serial\u001d91ABCD\u001d92proof";Expect(MarketplaceFbsPayloads.NormalizeCode("\u001d"+code)==code && MarketplaceFbsPayloads.NormalizeCode(code.Replace("\u001d","<GS>"))==code,"Full SGTIN changed.");return Task.CompletedTask;
});
Console.WriteLine($"{checks-failures.Count}/{checks} passed");
return failures.Count==0?0:1;
sealed class FixtureHttp(Func<HttpRequestMessage,HttpResponseMessage> responder):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(responder(request));
}
