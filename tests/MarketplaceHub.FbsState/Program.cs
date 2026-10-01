using MarketplaceHub.Core;
using MarketplaceHub.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

var app=new AppServices();var failures=new List<string>();var checks=0;var stores=new List<StoreProfile>();
async Task Check(string name,Func<Task> run){checks++;try{await run();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name);Console.WriteLine("FAIL "+name+": "+ex.GetBaseException().Message);}}
void Expect(bool ok,string message){if(!ok)throw new Exception(message);}
StoreProfile Store(Marketplace m){var store=app.Db.SaveStore(new(0,m,"STATE-FIXTURE-"+Guid.NewGuid().ToString("N"),"123","","456","789","",true));stores.Add(store);return store;}
HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
void Http(Func<HttpRequestMessage,HttpResponseMessage> response){typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,new HttpClient(new FixtureHttp(response)));typeof(MarketplaceGateway).GetField("wbLabelDelay",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,(Func<TimeSpan,CancellationToken,Task>)((_,ct)=>{ct.ThrowIfCancellationRequested();return Task.CompletedTask;}));}
string Posting(string status="awaiting_packaging",bool required=false)=>System.Text.Json.JsonSerializer.Serialize(new{result=new{posting_number="P",status,requirements=new{products_requiring_mandatory_mark=required?new[]{100}:Array.Empty<int>()},products=new[]{new{sku=100,offer_id="A",quantity=2,name="A"},new{sku=200,offer_id="B",quantity=1,name="B"}}}});
try {
await Check("Per-unit reservations survive retry and cannot steal WB or legacy assignments",()=>{
    var oz=Store(Marketplace.Ozon);var wb=Store(Marketplace.Wildberries);const string gtin="04601234567893";
    var a="01"+gtin+"21"+Guid.NewGuid().ToString("N");var b="01"+gtin+"21"+Guid.NewGuid().ToString("N");var legacy="01"+gtin+"21"+Guid.NewGuid().ToString("N");
    app.Db.UpsertKiz(a,gtin,"AVAILABLE");app.Db.UpsertKiz(b,gtin,"AVAILABLE");app.Db.UpsertKiz(legacy,gtin,"ASSIGNED","LEGACY-YANDEX");
    var one=app.Db.ReserveMarketplaceKiz(oz,"P","100",0,gtin);var retry=app.Db.ReserveMarketplaceKiz(oz,"P","100",0,gtin);var two=app.Db.ReserveMarketplaceKiz(oz,"P","100",1,gtin);
    Expect(one==retry && one!=two,"Unit ownership changed or code reused.");app.Db.UpsertKiz(one!,gtin,"AVAILABLE");Expect(app.Db.Kiz().Single(x=>x.Code==one).Status=="RESERVED","Import released reserved code.");
    var blocked=false;try{app.Db.ConfirmWbKiz(wb.Id,"1",gtin,one!);}catch(InvalidOperationException){blocked=true;}Expect(blocked,"WB stole Ozon reservation.");
    blocked=false;try{app.Db.ReserveMarketplaceKiz(oz,"Q","100",0,gtin,legacy);}catch(InvalidOperationException){blocked=true;}Expect(blocked && app.Db.Kiz().Single(x=>x.Code==legacy).Assigned=="LEGACY-YANDEX","Legacy owner was overwritten.");return Task.CompletedTask;
});
await Check("Catalog terminal totals preserve checkpoint and prior products on partial response",()=>{
    var store=Store(Marketplace.Ozon);var scope=ProductCatalog.Scope(store);app.Db.BeginProductCatalog(store,scope);
    var first=ProductCatalog.Entry(new(store.Id,store.Marketplace,"1","A","A",10,"","{\"barcodes\":[\"4601234567893\"]}"));
    app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{first},"next",false,3));
    var second=ProductCatalog.Entry(new(store.Id,store.Marketplace,"2","B","B",20,"","{}"));var rejected=false;
    try{app.Db.ApplyProductCatalogPage(store,"next",scope,new(new[]{second},"",true,3));}catch(InvalidDataException){rejected=true;}
    Expect(rejected && app.Db.ProductCatalogCheckpoint(store)!.Cursor=="next" && app.Db.Products(store.Id).Count==1,"Incomplete terminal page committed or erased prior catalog.");return Task.CompletedTask;
});
await Check("Yandex mapping evolution keeps stable offer identity and seller data",()=>{
    var store=Store(Marketplace.Yandex);var scope=ProductCatalog.Scope(store);app.Db.BeginProductCatalog(store,scope);
    app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{ProductCatalog.Entry(new(store.Id,store.Marketplace,"A","A","A",10,"","{\"offer\":{\"barcodes\":[\"4601234567893\"]}}"))},"",true));
    app.Db.BeginProductCatalog(store,scope);app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{ProductCatalog.Entry(new(store.Id,store.Marketplace,"A","A","A updated",null,"","{\"offer\":{\"barcodes\":[\"4601234567893\"]},\"mapping\":{\"marketSku\":123}}"))},"",true));
    var row=app.Db.Products(store.Id).Single();Expect(row.Price==10 && row.RawJson.Contains("123") && row.ExternalId=="A","Mapping evolution broke catalog or lost price.");return Task.CompletedTask;
});
await Check("Ozon receives the whole current posting before shipping",async()=>{
    var store=Store(Marketplace.Ozon);var shipped=false;var ships=0;
    Http(r=>{if(r.RequestUri!.AbsolutePath.EndsWith("/ship")){ships++;var payload=JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!;var products=payload["packages"]![0]!["products"]!.AsArray();Expect(products.Count==2 && products.Sum(x=>x!["quantity"]!.GetValue<int>())==3,"Partial posting mutation.");shipped=true;return Json("{}");}return Json(Posting(shipped?"awaiting_deliver":"awaiting_packaging"));});
    var result=await app.PackMarketplaceFbsAsync(store,new[]{"P"},_=>"",true);Expect(result.Success && ships==1 && app.Db.Orders(store.Id).Count==2,"Whole posting was not confirmed: "+result.Message);
});
await Check("An ambiguous ship cannot be resent through a new batch",async()=>{
    var store=Store(Marketplace.Ozon);var ships=0;
    Http(r=>{if(r.RequestUri!.AbsolutePath.EndsWith("/ship")){ships++;throw new HttpRequestException("lost mutation response");}return Json(Posting());});
    var first=await app.PackMarketplaceFbsAsync(store,new[]{"P"},_=>"",true);var second=await app.PackMarketplaceFbsAsync(store,new[]{"P"},_=>"",true);
    Expect(!first.Success && !second.Success && ships==1,"New batch resent ambiguous ship.");
});
await Check("Freshly added mandatory KIZ blocks ship before mutation",async()=>{
    var store=Store(Marketplace.Ozon);var reads=0;var ships=0;
    Http(r=>{if(r.RequestUri!.AbsolutePath.EndsWith("/ship")){ships++;return Json("{}");}if(r.RequestUri.AbsolutePath.EndsWith("/get")){reads++;return Json(Posting(required:reads>1));}return Json("{\"products\":[]}");});
    var result=await app.PackMarketplaceFbsAsync(store,new[]{"P"},_=>"",true);Expect(!result.Success && ships==0,"Fresh requirement was ignored.");
});
await Check("Historical unfinished batches resume without adding tomorrow's orders",()=>{
    var store=Store(Marketplace.Ozon);var batch=app.Db.CreateMarketplaceFbsBatch(store,new[]{"P"});
    using(var c=new SqliteConnection("Data Source="+app.Db.DbPath)){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE marketplace_fbs_batches SET created_at=$at WHERE id=$id";cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));cmd.Parameters.AddWithValue("$id",batch.Id);cmd.ExecuteNonQuery();}
    var resumed=app.Db.CreateMarketplaceFbsBatch(store,Array.Empty<string>(),batch.Id);Expect(resumed.Id==batch.Id && app.Db.TodayMarketplaceFbsBatches(store).Any(x=>x.Id==batch.Id),"Interrupted historical batch was hidden or refused.");
    var blocked=false;try{app.Db.CreateMarketplaceFbsBatch(store,new[]{"Q"},batch.Id);}catch(InvalidOperationException){blocked=true;}Expect(blocked,"Added new orders to yesterday's batch.");return Task.CompletedTask;
});
await Check("Cancelled Yandex order never sends layout or status",async()=>{
    var store=Store(Marketplace.Yandex);var puts=0;
    Http(r=>{if(r.Method==HttpMethod.Put)puts++;return Json("{\"order\":{\"id\":7,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"cancelRequested\":true,\"items\":[{\"id\":11,\"offerId\":\"A\",\"count\":1}]}}");});
    var result=await app.PackMarketplaceFbsAsync(store,new[]{"7"},_=>"",true);Expect(!result.Success && puts==0,"Cancelled order was changed.");
});

await Check("Yandex layout retry retains every unit code after a lost response",async()=>{
    var store=Store(Marketplace.Yandex);const string gtin="04601234567893";
    var originals=Enumerable.Range(0,2).Select(_=>"01"+gtin+"21"+Guid.NewGuid().ToString("N")+"\u001d91ABCD\u001d92proof").ToArray();foreach(var c in originals)app.Db.UpsertKiz(c,gtin,"AVAILABLE");
    var received=Array.Empty<string>();var boxPuts=0;var statusPuts=0;var ready=false;
    Http(r=>{
        var path=r.RequestUri!.AbsolutePath;
        if(path.EndsWith("/boxes")) {
            boxPuts++;var payload=JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!;var items=payload["boxes"]![0]!["items"]!.AsArray();Expect(items.Count==2 && items[0]!["fullCount"]!.GetValue<int>()==2 && items[1]!["fullCount"]!.GetValue<int>()==1 && !payload["allowRemove"]!.GetValue<bool>(),"Full order/layout was not retained.");
            received=items[0]!["instances"]!.AsArray().Select(x=>x!["cis"]!.ToString()).ToArray();if(boxPuts==1)throw new HttpRequestException("lost layout response");return Json("{\"status\":\"OK\"}");
        }
        if(path.EndsWith("/identifiers/status"))return Json(new JsonObject{["status"]="OK",["result"]=new JsonObject{["items"]=new JsonArray(new JsonObject{["id"]=11,["cis"]=new JsonArray(received.Reverse().Select(c=>(JsonNode)new JsonObject{["value"]=c,["status"]="OK"}).ToArray())})}}.ToJsonString());
        if(r.Method==HttpMethod.Put){statusPuts++;ready=true;return Json("{\"status\":\"OK\"}");}
        return Json("{\"order\":{\"id\":7,\"status\":\"PROCESSING\",\"substatus\":\""+(ready?"READY_TO_SHIP":"STARTED")+"\",\"items\":[{\"id\":11,\"offerId\":\"A\",\"count\":2,\"requiredInstanceTypes\":[\"CIS\"]},{\"id\":12,\"offerId\":\"B\",\"count\":1}]}}");
    });
    var first=await app.PackMarketplaceFbsAsync(store,new[]{"7"},_=>gtin,true);var reserved=app.Db.MarketplaceKizReservations(store,"7").Select(x=>x.Code).ToHashSet(StringComparer.Ordinal);
    Expect(!first.Success && statusPuts==0 && reserved.SetEquals(originals),"Lost layout response changed marks or prematurely closed order.");
    var second=await app.PackMarketplaceFbsAsync(store,Array.Empty<string>(),_=>gtin,true,batchId:first.ExternalTaskId);
    Expect(second.Success && statusPuts==1 && boxPuts==2 && app.Db.MarketplaceKizReservations(store,"7").All(x=>x.Status=="ASSIGNED") && received.ToHashSet(StringComparer.Ordinal).SetEquals(reserved),"Retry allocated replacement codes or did not confirm the complete order: "+second.Message);
});
await Check("A remote KIZ for another GTIN prevents Yandex layout and ship",async()=>{
    var store=Store(Marketplace.Yandex);var mutations=0;
    Http(r=>{if(r.Method==HttpMethod.Put)mutations++;if(r.RequestUri!.AbsolutePath.EndsWith("/identifiers/status"))return Json("{\"status\":\"OK\",\"result\":{\"items\":[{\"id\":11,\"cis\":[{\"value\":\"010460123456788621serial\",\"status\":\"OK\"}]}]}}");return Json("{\"order\":{\"id\":7,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":11,\"offerId\":\"A\",\"count\":1,\"hasCis\":true}]}}");});
    var result=await app.PackMarketplaceFbsAsync(store,new[]{"7"},_=>"04601234567893",true);Expect(!result.Success && mutations==0 && app.Db.MarketplaceKizReservations(store,"7").Count==0,"Wrong variant KIZ was adopted or shipped.");
});
}finally{foreach(var store in stores)app.Db.DeleteStore(store.Id);}
Console.WriteLine($"{checks-failures.Count}/{checks} persistence and FBS workflow checks passed");return failures.Count==0?0:1;
sealed class FixtureHttp(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(response(request));}}
