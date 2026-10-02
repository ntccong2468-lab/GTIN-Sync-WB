using MarketplaceHub.TestSupport;
using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

var app=WorkflowTestSupport.App(new MarketplaceHub.Infrastructure.AppDatabase(Path.Combine(Path.GetTempPath(),"MarketplaceHub-mock-"+Guid.NewGuid().ToString("N"),"test.db")),new MarketplaceGateway());
var mock=new OzonYandexMockApi();
typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,new HttpClient(mock));
var suffix=Guid.NewGuid().ToString("N");
var ozon=app.Db.SaveStore(new StoreProfile(0,Marketplace.Ozon,"MOCK-OZON-"+suffix,"10001","mock-secret","","","",true));
var yandex=app.Db.SaveStore(new StoreProfile(0,Marketplace.Yandex,"MOCK-YANDEX-"+suffix,"","mock-secret","20001","30001","",true));
const string gtin="04601234567893";
string Kiz(string serial)=>"01"+gtin+"21"+serial+"\u001d91ABCD\u001d92MOCKPROOF";
var failures=new List<string>();var checks=0;
void Expect(bool ok,string message){checks++;if(!ok){failures.Add(message);Console.WriteLine("FAIL "+message);}else Console.WriteLine("PASS "+message);}
async Task GtinMocks(){
    var queue=typeof(AppServices).GetMethod("QueueWbGtinWriteback");var resume=typeof(AppServices).GetMethod("ResumeWbGtinWritebackAsync");
    if(queue is null||resume is null)return;
    var path=Path.Combine(Path.GetTempPath(),"MarketplaceHub-gtin-"+Guid.NewGuid().ToString("N"),"test.db");
    var db=new MarketplaceHub.Infrastructure.AppDatabase(path);var handler=new GtinMockApi();var api=new MarketplaceGateway((_,ct)=>{ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
    typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,new HttpClient(handler));
    var target=WorkflowTestSupport.App(db,api);
    var store=db.SaveStore(new(0,Marketplace.Wildberries,"GTIN-MOCK","","","","","fake-wb-token",true));
    string NewGtin(int i){var prefix="460"+i.ToString("D9");var sum=prefix.Reverse().Select((c,n)=>(c-'0')*(n%2==0?3:1)).Sum();return "0"+prefix+((10-sum%10)%10);}
    var entries=Enumerable.Range(1,51).Select(i=>{
        var card=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new{nmID=i,vendorCode="GTIN-"+i,title="Áo",sizes=new[]{new{chrtID=i*10,techSize="48",skus=new[]{"4601234567893"}},new{chrtID=i*10+1,techSize="50",skus=new[]{"4601234567893"}}}}))!.AsObject();handler.Cards[i]=card;return ProductCatalog.Entry(new(store.Id,store.Marketplace,i.ToString(),"GTIN-"+i,"Áo",null,"",card.ToJsonString()));
    }).ToArray();var scope=ProductCatalog.Scope(store);db.BeginProductCatalog(store,scope);db.ApplyProductCatalogPage(store,"",scope,new(entries,"",true));
    foreach(var entry in entries)db.UpsertSellerGtinMapping(store,entry.Product.Sku,entry.Variants[0].VariantId,NewGtin(int.Parse(entry.Product.ExternalId)));
    queue.Invoke(target,new object[]{store});
    var first=await (Task<PriceUpdateResult>)resume.Invoke(target,new object[]{store,CancellationToken.None})!;
    Expect(!first.Success&&handler.Writes.Select(x=>x.Length).SequenceEqual(new[]{50,1}),"WB sends at most 50 cards and stops second batch on 429");
    var states=db.GetGtinMappingPage(store,0,50,"","all").Rows.Concat(db.GetGtinMappingPage(store,50,50,"","all").Rows).ToArray();
    Expect(states.Count(x=>x.WbStage=="VERIFIED")==50&&states.Where(x=>x.Confirmed).All(x=>x.Source=="seller"),"Failed second WB batch retains first 50 confirmed seller GTIN mappings");
    var paused=await (Task<PriceUpdateResult>)resume.Invoke(target,new object[]{store,CancellationToken.None})!;
    Expect(!paused.Success&&handler.Writes.Count==2,"WB retry before Retry-After performs no further write");
    using(var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+db.DbPath)){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE gtin_sync_jobs SET retry_at='2000-01-01T00:00:00+00:00' WHERE endpoint='WB_WRITEBACK'";cmd.ExecuteNonQuery();}
    var restarted=WorkflowTestSupport.App(new MarketplaceHub.Infrastructure.AppDatabase(path),api);
    var final=await (Task<PriceUpdateResult>)resume.Invoke(restarted,new object[]{store,CancellationToken.None})!;
    Expect(final.Success&&handler.Writes.Count==3&&handler.Writes[2].SequenceEqual(new[]{51})&&handler.PreservedSizes,"WB restart resumes only failed card and preserves every existing size/barcode");
    handler.HideLastReadback=true;var current=handler.Cards[51]["sizes"]![0]!["skus"]!.AsArray();current.RemoveAt(current.Count-1);
    db.UpsertSellerGtinMapping(store,"GTIN-51","510",NewGtin(501));queue.Invoke(target,new object[]{store});
    var mismatch=await (Task<PriceUpdateResult>)resume.Invoke(target,new object[]{store,CancellationToken.None})!;
    Expect(!mismatch.Success&&db.GetGtinMappingPage(store,0,50,"GTIN-51","all").Rows.Single(x=>x.VariantId=="510").WbStage!="VERIFIED","Wrong WB readback keeps changed variant pending");
    var nk=new NationalCatalogMockApi();var service=new GtinMappingSyncService(db,api,new HttpClient(nk),(_,ct)=>{ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
    var access=new NationalCatalogAccess("fake-national-secret","",false);var wbCheckpoint=db.GetGtinSyncJob(store,"WB_WRITEBACK");
    nk.MismatchGtin=NewGtin(1);
    var limited=await service.SyncZnackGtinAsync(store,access);
    Expect(!limited.Success&&db.GetGtinSyncJob(store,"ZNACK_PRODUCT") is {Cursor:25,RetryAt:not null},"National Catalog persists first 25-GTIN batch and stops on endpoint quota");
    Expect(db.GetGtinSyncJob(store,"WB_WRITEBACK")==wbCheckpoint,"National Catalog quota does not reset independent WB checkpoint");
    using(var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+db.DbPath)){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE gtin_sync_jobs SET retry_at='2000-01-01T00:00:00+00:00' WHERE endpoint='ZNACK_PRODUCT'";cmd.ExecuteNonQuery();}
    var nkDone=await new GtinMappingSyncService(new MarketplaceHub.Infrastructure.AppDatabase(path),api,new HttpClient(nk),(_,_)=>Task.CompletedTask).SyncZnackGtinAsync(store,access);
    Expect(!nkDone.Success&&nk.Batches.Count==4&&nk.Batches[0].SequenceEqual(nk.Batches[1])==false&&nk.Batches[1].SequenceEqual(nk.Batches[2]),"National Catalog restart retains earlier size mismatch without re-fetching completed 25 GTINs");
    var proof=typeof(GtinMappingSyncService).GetMethod("IsZnackTargetVerified");Expect(proof is not null,"Live GTIN gate requires exact National Catalog evidence");
    if(proof is not null){
        var good=new GtinSyncTarget("GTIN-4","40","4","48",NewGtin(4));
        Expect((bool)proof.Invoke(service,new object[]{store,access,good})!,"Exact verified target can pass the live gate despite another variant mismatch");
        Expect(!(bool)proof.Invoke(service,new object[]{store,new NationalCatalogAccess("different-credential","",false),good})!,"Changing National Catalog credential invalidates earlier target proof");
        var wrong=new GtinSyncTarget("GTIN-1","10","1","48",NewGtin(1));
        Expect(!(bool)proof.Invoke(service,new object[]{store,access,wrong})!,"Earlier mismatched target cannot pass live write gate after resume");
        var absent=new GtinSyncTarget("GTIN-1","10","1","48",NewGtin(999));
        db.UpsertSellerGtinMapping(store,absent.Sku,absent.VariantId,absent.Gtin);
        Expect(!(bool)proof.Invoke(service,new object[]{store,access,absent})!,"A new target absent from checkpoint cannot unlock the live gate");
    }
    nk.BoxGtin=NewGtin(2);nk.TechnicalUnknownGtin=NewGtin(3);nk.MismatchGtin="";
    var packaging=await service.SyncZnackGtinAsync(store,access);
    Expect(!packaging.Success&&db.GetGtinMappingPage(store,0,50,"GTIN-2","all").Rows.Single(x=>x.VariantId=="20").ZnackStage!="PUBLISHED"&&db.GetGtinMappingPage(store,0,50,"GTIN-3","all").Rows.Single(x=>x.VariantId=="30").ZnackStage!="PUBLISHED","Box GTIN and missing technical status cannot confirm a consumer variant");
    using(var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+db.DbPath)){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM gtin_mapping WHERE metadata_json LIKE '%fake-national-secret%'";Expect(Convert.ToInt32(cmd.ExecuteScalar())==0,"National Catalog metadata redacts credentials before persistence");}
    Expect(db.GetGtinMappingPage(store,0,50,"GTIN-51","all").Rows.Single(x=>x.VariantId=="510").Source=="seller","Znack suggestions never replace confirmed seller source");
    var single=typeof(GtinMappingSyncService).GetMethod("QueueWbGtinWritebackForVariant");
    if(single is not null){
        var blocked=false;try{single.Invoke(service,new object[]{store,"GTIN-1","10"});}catch(TargetInvocationException ex){blocked=ex.InnerException is InvalidOperationException;}
        Expect(blocked,"Single-target live probe refuses another pending writeback operation");
        var probe=db.SaveStore(new(0,Marketplace.Wildberries,"LIVE-PROBE-FIXTURE","","","","","fake-wb-token",true));var probeScope=ProductCatalog.Scope(probe);
        db.BeginProductCatalog(probe,probeScope);db.ApplyProductCatalogPage(probe,"",probeScope,new(entries.Take(3).Select(x=>ProductCatalog.Entry(x.Product with{StoreId=probe.Id})).ToArray(),"",true));
        foreach(var entry in entries.Take(3))db.UpsertSellerGtinMapping(probe,entry.Product.Sku,entry.Variants[0].VariantId,NewGtin(int.Parse(entry.Product.ExternalId)));
        single.Invoke(service,new object[]{probe,"GTIN-2","20"});var snapshot=JsonNode.Parse(db.GetGtinSyncJob(probe,"WB_WRITEBACK")!.SnapshotJson)!.AsArray();
        handler.ReadIds.Clear();var probeDone=await service.ResumeWbGtinWritebackAsync(probe);
        Expect(snapshot.Count==1&&snapshot[0]!["Sku"]!.ToString()=="GTIN-2"&&probeDone.Success&&handler.ReadIds.All(x=>x==2),"Live GTIN probe reads/verifies only the explicitly selected variant");db.DeleteStore(probe.Id);
    }
    db.DeleteStore(store.Id);
}
try
{
    Expect(typeof(AppServices).GetMethod("SyncZnackGtinAsync") is not null&&typeof(AppServices).GetMethod("QueueWbGtinWriteback") is not null&&typeof(AppServices).GetMethod("ResumeWbGtinWritebackAsync") is not null,"Znack/WB durable GTIN pipeline is available for end-to-end mock validation");
    Expect(typeof(GtinMappingSyncService).GetMethod("QueueWbGtinWritebackForVariant") is not null,"Live GTIN probe can queue only one explicitly selected variant");
    foreach(var store in new[]{ozon,yandex})
    {
        var sku=store.Marketplace==Marketplace.Ozon?"OZ-SKU":"YA-SKU";
        app.Db.ReplaceProducts(store.Id,store.Marketplace,new[]{new ProductRow(store.Id,store.Marketplace,sku,sku,"Mock marked product",1000,"","{}")});
        app.Db.UpsertKiz(Kiz(store.Marketplace==Marketplace.Ozon?"OZON0001":"YANDEX01"),gtin,"AVAILABLE");
        var rows=await app.Api.SyncFbsAsync(store);app.Db.UpsertOrders(store.Id,store.Marketplace,rows);
        Expect(rows.Count==1,store.Marketplace+" mock queue returns one complete posting");
        var orderId=rows.Single().ExternalOrderId;
        var received=await app.ReceiveMarketplaceFbsAsync(store,new[]{orderId});
        Expect(received.Success&&received.ExternalTaskId is not null,store.Marketplace+" receives posting into a durable shipment");
        Expect(app.Db.MarketplaceReceivedOrderIds(store).Contains(orderId),store.Marketplace+" received posting leaves New queue");
        var context=await WorkflowTestSupport.PreparedContext(app,new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,received.ExternalTaskId!),new[]{Kiz(store.Marketplace==Marketplace.Ozon?"OZON0001":"YANDEX01")},marketGtin:_=>gtin);
        var packed=await app.PackMarketplaceFbsAsync(store,Array.Empty<string>(),_=>gtin,true,batchId:received.ExternalTaskId,workflowContext:context);
        Expect(packed.Success,store.Marketplace+" automatically reserves, submits and verifies KIZ before packing: "+packed.Message);
        var reservations=app.Db.MarketplaceKizReservations(store,orderId);
        Expect(reservations.Count==1&&reservations[0].Status=="ASSIGNED",store.Marketplace+" owns exactly one accepted KIZ for one physical unit");
        var label=await app.ExportVerifiedMarketplaceLabelAsync(store,orderId,_=>gtin,workflowContext:context);
        Expect(label.Success&&label.FilePath is not null&&File.Exists(label.FilePath),store.Marketplace+" returns a verified official PDF label: "+label.Message);
        if(label.FilePath is not null&&File.Exists(label.FilePath))File.Delete(label.FilePath);
    }
    Expect(mock.OzonLabelCreates==1,"Ozon creates one label task and never uses the obsolete direct PDF endpoint");
    Expect(mock.OzonShips==1&&mock.YandexReadyMutations==1,"Both mock platforms receive exactly one final packing mutation");
    Expect(mock.UnexpectedMutations.Count==0,"Mock API observed no duplicate or out-of-order mutation");
    await GtinMocks();
}
finally
{
    app.Db.DeleteStore(ozon.Id);app.Db.DeleteStore(yandex.Id);
}
Console.WriteLine($"{checks-failures.Count}/{checks} Ozon/Yandex mock API end-to-end checks passed");
return failures.Count==0?0:1;

sealed class GtinMockApi:HttpMessageHandler
{
    public Dictionary<int,JsonObject> Cards{get;}=new();public List<int[]> Writes{get;}=new();public List<int> ReadIds{get;}=new();
    public bool PreservedSizes{get;private set;}=true;public bool HideLastReadback{get;set;}
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
        ct.ThrowIfCancellationRequested();var body=JsonNode.Parse(r.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult())!;
        if(r.RequestUri!.AbsolutePath.EndsWith("/update")){
            var array=body.AsArray();var ids=array.Select(x=>x!["nmID"]!.GetValue<int>()).ToArray();Writes.Add(ids);
            if(Writes.Count==2){var limited=Json("{}",HttpStatusCode.TooManyRequests);limited.Headers.TryAddWithoutValidation("Retry-After","3600");return Task.FromResult(limited);}
            foreach(var card in array){var id=card!["nmID"]!.GetValue<int>();var sizes=card["sizes"]!.AsArray();PreservedSizes&=sizes.Count==2&&sizes[0]!["skus"]!.AsArray().Any(x=>x!.ToString()=="4601234567893")&&sizes[1]!["skus"]![0]!.ToString()=="4601234567893";
                if(!(HideLastReadback&&id==51))Cards[id]=card.DeepClone().AsObject();}
            return Task.FromResult(Json("{\"error\":false}"));
        }
        var search=body["settings"]!["filter"]!["textSearch"]!.ToString();var nm=int.Parse(search);ReadIds.Add(nm);
        return Task.FromResult(Json(new JsonObject{["cards"]=new JsonArray(Cards[nm].DeepClone()),["cursor"]=new JsonObject{["total"]=1}}.ToJsonString()));
    }
    static HttpResponseMessage Json(string text,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(text,Encoding.UTF8,"application/json")};
}

sealed class NationalCatalogMockApi:HttpMessageHandler
{
    public List<string[]> Batches{get;}=new();public string MismatchGtin{get;set;}="";public string BoxGtin{get;set;}="";public string TechnicalUnknownGtin{get;set;}="";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        ct.ThrowIfCancellationRequested();var query=request.RequestUri!.Query.TrimStart('?').Split('&');
        var gtins=Uri.UnescapeDataString(query.Single(x=>x.StartsWith("gtins="))[6..]).Split(';');Batches.Add(gtins);
        if(Batches.Count==2){var result=Json("{}",HttpStatusCode.TooManyRequests);result.Headers.TryAddWithoutValidation("Retry-After","3600");return Task.FromResult(result);}
        var cards=new JsonArray(gtins.Select(g=>(JsonNode?)new JsonObject{["good_id"]=100,["good_status"]="published",["is_tech_gtin"]=g==TechnicalUnknownGtin?(JsonNode?)null:JsonValue.Create(false),["good_name"]="Áo fake-national-secret",
            ["identified_by"]=new JsonArray(new JsonObject{["type"]="gtin",["value"]=g,["level"]=g==BoxGtin?"box":"trade-unit",["multiplier"]=g==BoxGtin?28:1}),
            ["good_attrs"]=new JsonArray(new JsonObject{["attr_id"]=35,["attr_value"]=g==MismatchGtin||g=="04601234567893"?"50":"48"})}).ToArray());
        return Task.FromResult(Json(new JsonObject{["result"]=cards}.ToJsonString()));
    }
    static HttpResponseMessage Json(string text,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(text,Encoding.UTF8,"application/json")};
}

sealed class OzonYandexMockApi:HttpMessageHandler
{
    private string? ozonCode;private string? yandexCode;private bool ozonPacked;private bool yandexPacked;
    public int OzonLabelCreates{get;private set;}public int OzonShips{get;private set;}public int YandexReadyMutations{get;private set;}
    public List<string> UnexpectedMutations{get;}=new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var path=request.RequestUri!.AbsolutePath;var host=request.RequestUri.Host;var body=request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult()??"";
        if(host.EndsWith("ozon.ru",StringComparison.OrdinalIgnoreCase))return Task.FromResult(Ozon(request,path,body));
        if(host.EndsWith("yandex.ru",StringComparison.OrdinalIgnoreCase))return Task.FromResult(Yandex(request,path,body));
        return Task.FromResult(Json("{}",HttpStatusCode.NotFound));
    }
    private HttpResponseMessage Ozon(HttpRequestMessage request,string path,string body)
    {
        if(path=="/v4/posting/fbs/unfulfilled/list")return Json("{\"result\":{\"postings\":["+OzonPosting()+"],\"has_next\":false,\"cursor\":\"\"}}");
        if(path=="/v3/posting/fbs/get")return Json("{\"result\":"+OzonPosting()+"}");
        if(path=="/v6/fbs/posting/product/exemplar/create-or-get")return Json(Exemplars());
        if(path=="/v5/fbs/posting/product/exemplar/validate")return Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"status\":\"valid\"}]}]}");
        if(path=="/v6/fbs/posting/product/exemplar/set")
        {
            var root=JsonNode.Parse(body)!;ozonCode=root["products"]![0]!["exemplars"]![0]!["marks"]![0]!["mark"]!.ToString();return Json("{}");
        }
        if(path=="/v5/fbs/posting/product/exemplar/status")return Json(ozonCode is null?"{\"status\":\"validation_in_process\"}":"{\"status\":\"ship_available\",\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":5001,\"marks\":[{\"mark\":"+JsonValue.Create(ozonCode).ToJsonString()+",\"check_status\":\"passed\"}]}]}]}");
        if(path=="/v4/posting/fbs/ship")
        {
            if(ozonPacked)UnexpectedMutations.Add("duplicate Ozon ship");OzonShips++;ozonPacked=true;return Json("{}");
        }
        if(path=="/v2/posting/fbs/package-label/create")
        {
            if(!ozonPacked)UnexpectedMutations.Add("label before Ozon ship");OzonLabelCreates++;return Json("{\"result\":{\"task_id\":9001}}");
        }
        if(path=="/v1/posting/fbs/package-label/get")return Json("{\"result\":{\"status\":\"completed\",\"file_url\":\"https://cdn.ozon.ru/mock/9001.pdf\"}}");
        if(path=="/mock/9001.pdf")return Pdf();
        return Json("{}",HttpStatusCode.NotFound);
    }
    private string OzonPosting()=>"{\"posting_number\":\"OZ-1\",\"status\":\""+(ozonPacked?"awaiting_deliver":"awaiting_packaging")+"\",\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"sku\":100,\"product_id\":100,\"offer_id\":\"OZ-SKU\",\"name\":\"Mock Ozon product\",\"quantity\":1}]}";
    private string Exemplars()=>"{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":5001"+(ozonCode is null?"":",\"marks\":[{\"mark_type\":\"mandatory_mark\",\"mark\":"+JsonValue.Create(ozonCode).ToJsonString()+"}]")+"}]}]}";
    private HttpResponseMessage Yandex(HttpRequestMessage request,string path,string body)
    {
        if(path=="/v2/campaigns/30001/orders"&&request.Method==HttpMethod.Get)return Json("{\"orders\":["+YandexOrder()+"],\"paging\":{}}");
        if(path=="/v2/campaigns/30001/orders/7001"&&request.Method==HttpMethod.Get)return Json("{\"order\":"+YandexOrder()+"}");
        if(path.EndsWith("/identifiers/status"))return Json(yandexCode is null?"{\"status\":\"OK\",\"result\":{\"items\":[]}}":"{\"status\":\"OK\",\"result\":{\"items\":[{\"id\":11,\"cis\":[{\"value\":"+JsonValue.Create(yandexCode).ToJsonString()+",\"status\":\"OK\"}]}]}}");
        if(path.EndsWith("/boxes")&&request.Method==HttpMethod.Put)
        {
            var root=JsonNode.Parse(body)!;yandexCode=root["boxes"]![0]!["items"]![0]!["instances"]![0]!["cis"]!.ToString();return Json("{\"status\":\"OK\"}");
        }
        if(path=="/v2/campaigns/30001/orders/7001/status"&&request.Method==HttpMethod.Put)
        {
            if(yandexPacked)UnexpectedMutations.Add("duplicate Yandex READY_TO_SHIP");YandexReadyMutations++;yandexPacked=true;return Json("{\"status\":\"OK\"}");
        }
        if(path.EndsWith("/delivery/labels"))return Pdf();
        return Json("{}",HttpStatusCode.NotFound);
    }
    private string YandexOrder()=>"{\"id\":7001,\"status\":\"PROCESSING\",\"substatus\":\""+(yandexPacked?"READY_TO_SHIP":"STARTED")+"\",\"items\":[{\"id\":11,\"offerId\":\"YA-SKU\",\"offerName\":\"Mock Yandex product\",\"count\":1,\"requiredInstanceTypes\":[\"CIS\"]}]}";
    private static HttpResponseMessage Json(string body,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Pdf()=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\nmock official marketplace label"))};
}
