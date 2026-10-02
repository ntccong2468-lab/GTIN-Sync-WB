using MarketplaceHub.Core;
using MarketplaceHub.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

var app=new AppServices(new MarketplaceHub.Infrastructure.AppDatabase(Path.Combine(Path.GetTempPath(),"MarketplaceHub-state-"+Guid.NewGuid().ToString("N"),"test.db")),new MarketplaceGateway(),LicenseAccessService.CreateDefault());var failures=new List<string>();var checks=0;var stores=new List<StoreProfile>();
async Task Check(string name,Func<Task> run){checks++;try{await run();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name);Console.WriteLine("FAIL "+name+": "+ex.GetBaseException().Message);}}
void Expect(bool ok,string message){if(!ok)throw new Exception(message);}
StoreProfile Store(Marketplace m){var store=app.Db.SaveStore(new(0,m,"STATE-FIXTURE-"+Guid.NewGuid().ToString("N"),"123","fixture","456","789","fixture-wb-token",true));stores.Add(store);return store;}
HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
void Http(Func<HttpRequestMessage,HttpResponseMessage> response){typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,new HttpClient(new FixtureHttp(response)));typeof(MarketplaceGateway).GetField("wbLabelDelay",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,(Func<TimeSpan,CancellationToken,Task>)((_,ct)=>{ct.ThrowIfCancellationRequested();return Task.CompletedTask;}));}
string Posting(string status="awaiting_packaging",bool required=false)=>System.Text.Json.JsonSerializer.Serialize(new{result=new{posting_number="P",status,requirements=new{products_requiring_mandatory_mark=required?new[]{100}:Array.Empty<int>()},products=new[]{new{sku=100,offer_id="A",quantity=2,name="A"},new{sku=200,offer_id="B",quantity=1,name="B"}}}});
try {
await Check("WB verified membership survives incomplete refresh and cannot switch shipment",()=>{
    var store=Store(Marketplace.Wildberries);app.Db.StoreWbSupplyMembership(store.Id,"ORIGINAL",new[]{"911"});app.Db.StoreWbSupplyMembership(store.Id,"ORIGINAL",Array.Empty<string>());
    Expect(app.Db.WbReceivedOrderIds(store.Id).Contains("911"),"An incomplete membership refresh released a verified order back to New.");
    var blocked=false;try{app.Db.StoreWbSupplyMembership(store.Id,"OTHER",new[]{"911"});}catch(InvalidOperationException){blocked=true;}
    Expect(blocked&&app.Db.FindWbSupplyForOrder(store.Id,"911")=="ORIGINAL","Membership refresh moved an order to another shipment.");return Task.CompletedTask;
});
await Check("GTIN mapping pages 50 exact variants and preserves seller rules across catalog refresh and restart",()=>{
    var pageMethod=app.Db.GetType().GetMethod("GetGtinMappingPage");var save=app.Db.GetType().GetMethod("UpsertSellerGtinMapping");
    Expect(pageMethod is not null&&save is not null,"Durable variant GTIN mapping repository is missing.");
    var store=Store(Marketplace.Wildberries);var scope=ProductCatalog.Scope(store);
    var entries=Enumerable.Range(0,60).Select(i=>ProductCatalog.Entry(new ProductRow(store.Id,store.Marketplace,(5000+i).ToString(),"MAP-"+i.ToString("D2"),"Áo "+i,10,"",
        System.Text.Json.JsonSerializer.Serialize(new{nmID=5000+i,sizes=new[]{new{chrtID=10000+i*2,techSize="48",skus=new[]{"4601234567893"}},new{chrtID=10001+i*2,techSize="50",skus=new[]{"4601234567893"}}}})))).ToArray();
    app.Db.BeginProductCatalog(store,scope);app.Db.ApplyProductCatalogPage(store,"",scope,new(entries,"",true));
    object Page(MarketplaceHub.Infrastructure.AppDatabase db,int offset=0,string query="")=>pageMethod!.Invoke(db,new object[]{store,offset,50,query,"all"})!;
    object[] Rows(object page)=>((System.Collections.IEnumerable)page.GetType().GetProperty("Rows")!.GetValue(page)!).Cast<object>().ToArray();
    string Value(object row,string property)=>row.GetType().GetProperty(property)!.GetValue(row)?.ToString()??"";
    var first=Page(app.Db);Expect(Rows(first).Length==50&&Convert.ToInt32(first.GetType().GetProperty("Total")!.GetValue(first))==120,"Page size or distinct variants are wrong.");
    save!.Invoke(app.Db,new object[]{store,"MAP-59","10118","04601234567893"});
    save.Invoke(app.Db,new object[]{store,"MAP-59","10119","04601234567893"});
    var mapped=Rows(Page(app.Db));Expect(mapped.Take(2).All(x=>Value(x,"Sku")=="MAP-59"&&Value(x,"Source")=="seller"&&Value(x,"Confirmed")=="True"),"Seller-confirmed variants are not first or were grouped by SKU.");
    Expect(Convert.ToInt32(Page(app.Db).GetType().GetProperty("MappingRuleCount")!.GetValue(Page(app.Db)))==2,"Rule count was confused with GTIN inventory count.");
    var invalid=false;try{save.Invoke(app.Db,new object[]{store,"MAP-59","10118","04601234567894"});}catch(TargetInvocationException ex){invalid=ex.InnerException is InvalidOperationException;}Expect(invalid,"Invalid checksum overwrote seller mapping.");
    app.Db.BeginProductCatalog(store,scope);var changed=entries[^1] with{Variants=entries[^1].Variants.Select(x=>x with{Gtin="",Barcodes=Array.Empty<string>()}).ToArray()};
    app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{changed},"",true));
    var reopened=new MarketplaceHub.Infrastructure.AppDatabase(app.Db.DbPath);
    Expect(Rows(Page(reopened,0,"MAP-59")).All(x=>Value(x,"Gtin")=="04601234567893"&&Value(x,"Source")=="seller"),"Refresh/restart lost confirmed seller GTIN.");
    Expect(Rows(Page(reopened,100)).Length==20,"Final page restarted at zero or returned over 50 rows.");
    return Task.CompletedTask;
});
await Check("GTIN mapping isolates stores and archives without deleting KIZ",()=>{
    var page=app.Db.GetType().GetMethod("GetGtinMappingPage");var save=app.Db.GetType().GetMethod("UpsertSellerGtinMapping");var archive=app.Db.GetType().GetMethod("ArchiveGtinMapping");
    Expect(page is not null&&save is not null&&archive is not null,"Safe GTIN mapping archive is missing.");
    var one=Store(Marketplace.Ozon);var two=Store(Marketplace.Ozon);
    foreach(var store in new[]{one,two}){var scope=ProductCatalog.Scope(store);app.Db.BeginProductCatalog(store,scope);app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{ProductCatalog.Entry(new ProductRow(store.Id,store.Marketplace,"11","A","A",10,"","{\"barcodes\":[\"4601234567893\"]}"))},"",true));}
    save!.Invoke(app.Db,new object[]{one,"A","11","04601234567893"});
    var untouched=page!.Invoke(app.Db,new object[]{two,0,50,"","all"})!;
    Expect(Convert.ToInt32(untouched.GetType().GetProperty("MappingRuleCount")!.GetValue(untouched))==0,"Mapping leaked to another store.");
    var code="010460123456789321mapping-"+Guid.NewGuid().ToString("N");app.Db.UpsertKiz(code,"04601234567893","AVAILABLE");
    archive!.Invoke(app.Db,new object[]{one,"A","11"});var hidden=page.Invoke(app.Db,new object[]{one,0,50,"","all"})!;
    Expect(Convert.ToInt32(hidden.GetType().GetProperty("Total")!.GetValue(hidden))==0&&app.Db.Kiz().Any(x=>x.Code==code),"Archiving deleted KIZ or left the mapping active.");
    using(var c=new SqliteConnection("Data Source="+app.Db.DbPath)){c.Open();using var cleanup=c.CreateCommand();cleanup.CommandText="DELETE FROM kiz_pool WHERE code=$code AND status='AVAILABLE' AND assigned_order=''";cleanup.Parameters.AddWithValue("$code",code);cleanup.ExecuteNonQuery();}
    return Task.CompletedTask;
});
await Check("WB receive journal survives restart and retains its resolved supply",()=>{
    var begin=app.Db.GetType().GetMethod("BeginWbReceiveOperation");
    Expect(begin is not null,"WB receive intent is not durable before the remote write.");
    var store=Store(Marketplace.Wildberries);var operation="op-"+Guid.NewGuid().ToString("N");
    begin!.Invoke(app.Db,new object?[]{store.Id,operation,new[]{"201","202"},null,"MarketplaceHub recovery"});
    app.Db.GetType().GetMethod("SaveWbReceiveOperation")!.Invoke(app.Db,new object[]{store.Id,operation,"SUPPLY-RECOVERY","PATCH_PENDING",Array.Empty<string>()});
    var reopened=new MarketplaceHub.Infrastructure.AppDatabase(app.Db.DbPath);
    var pending=reopened.GetType().GetMethod("RecoverableWbReceive")!.Invoke(reopened,new object[]{store.Id,new[]{"201","202"}});
    Expect(pending is not null&&pending.GetType().GetProperty("SupplyId")!.GetValue(pending)?.ToString()=="SUPPLY-RECOVERY","Restart lost the resolved shipment.");
    return Task.CompletedTask;
});
await Check("WB restart resumes retained shipment without creating or readding verified orders",async()=>{
    var begin=app.Db.GetType().GetMethod("BeginWbReceiveOperation");Expect(begin is not null,"Receive journal is missing.");
    var store=Store(Marketplace.Wildberries);var operation=Guid.NewGuid().ToString("N");
    var rows=new[]{new FbsOrderRow(store.Id,store.Marketplace,"401","A","A",1,"new",false,"{}"),new FbsOrderRow(store.Id,store.Marketplace,"402","B","B",1,"new",false,"{}")};
    app.Db.UpsertOrders(store.Id,store.Marketplace,rows);
    begin!.Invoke(app.Db,new object?[]{store.Id,operation,new[]{"401","402"},"RESTART-SUPPLY","recovery"});
    var members=new HashSet<long>{401};var created=0;var patched=new List<long>();
    Http(r=>{
        var path=r.RequestUri!.AbsolutePath;
        if(path.EndsWith("/status")){var ids=JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray().Select(x=>x!.GetValue<long>());return Json(System.Text.Json.JsonSerializer.Serialize(new{orders=ids.Select(id=>new{id,supplierStatus=members.Contains(id)?"confirm":"new",wbStatus="waiting"})}));}
        if(r.Method.Method=="PATCH"){foreach(var id in JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["orders"]!.AsArray().Select(x=>x!.GetValue<long>())){patched.Add(id);members.Add(id);}return Json("{}");}
        if(r.Method==HttpMethod.Post){created++;throw new Exception("Recovery must not create another supply");}
        if(path.EndsWith("/order-ids"))return Json(System.Text.Json.JsonSerializer.Serialize(new{orderIds=members}));
        if(path.EndsWith("/supplies"))return Json("{\"next\":0,\"supplies\":[{\"id\":\"RESTART-SUPPLY\",\"name\":\"recovery\",\"done\":false,\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}]}");
        return Json("{\"id\":\"RESTART-SUPPLY\",\"name\":\"recovery\",\"done\":false,\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}");
    });
    var result=await app.ReceiveWbOrdersAsync(store,rows,new(null,"new requested"));
    Expect(result.Success&&result.SupplyId=="RESTART-SUPPLY"&&created==0&&patched.SequenceEqual(new long[]{402})&&app.Db.WbReceivedOrderIds(store.Id).SetEquals(new[]{"401","402"}),"Restart recreated a supply or repeated an already verified order.");
});
await Check("Canonical truth counts one posting with several lines once",()=>{
    var store=new StoreProfile(9001,Marketplace.Wildberries,"WB","","","","","",true);
    var rows=new[]{
        new FbsOrderRow(store.Id,store.Marketplace,"101","SKU-S","Áo size S",1,"new",false,"{}"),
        new FbsOrderRow(store.Id,store.Marketplace,"101","SKU-M","Áo size M",1,"new",false,"{}")};
    var remote=new Dictionary<string,OrderRemoteState>(StringComparer.Ordinal){{"101",new("new","waiting",true,DateTimeOffset.UtcNow)}};
    var truth=new OrderTruthService().Build(store,rows,new HashSet<string>(),remote);
    Expect(truth.Orders.Count==1&&truth.NewCount==1&&truth.Orders[0].Lines.Count==2,"Posting nhiều dòng bị đếm thành nhiều đơn.");
    return Task.CompletedTask;
});
await Check("Shipment members are not new",()=>{
    var store=new StoreProfile(9002,Marketplace.Wildberries,"WB","","","","","",true);
    var rows=new[]{new FbsOrderRow(store.Id,store.Marketplace,"102","SKU","Áo",1,"new",false,"{}")};
    var remote=new Dictionary<string,OrderRemoteState>(StringComparer.Ordinal){{"102",new("confirm","waiting",true,DateTimeOffset.UtcNow)}};
    var truth=new OrderTruthService().Build(store,rows,new HashSet<string>{"102"},remote);
    Expect(truth.NewCount==0&&truth.Orders.Single().State==OrderTruthState.InShipment,"Đơn đã thuộc shipment vẫn là đơn mới.");
    return Task.CompletedTask;
});
await Check("Cancelled remote state is not new",()=>{
    var store=new StoreProfile(9003,Marketplace.Wildberries,"WB","","","","","",true);
    var rows=new[]{new FbsOrderRow(store.Id,store.Marketplace,"103","SKU","Áo",1,"new",false,"{}")};
    var remote=new Dictionary<string,OrderRemoteState>(StringComparer.Ordinal){{"103",new("new","canceled_by_client",true,DateTimeOffset.UtcNow)}};
    var truth=new OrderTruthService().Build(store,rows,new HashSet<string>(),remote);
    Expect(truth.NewCount==0&&truth.Orders.Single().State==OrderTruthState.Cancelled,"Trạng thái hủy hiện tại vẫn được chọn.");
    return Task.CompletedTask;
});
await Check("Unknown current state is partial rather than authoritative",()=>{
    var store=new StoreProfile(9004,Marketplace.Wildberries,"WB","","","","","",true);
    var rows=new[]{new FbsOrderRow(store.Id,store.Marketplace,"104","SKU","Áo",1,"new",false,"{}")};
    var truth=new OrderTruthService().Build(store,rows,new HashSet<string>(),new Dictionary<string,OrderRemoteState>());
    Expect(!truth.IsAuthoritative&&truth.NewCount==0&&truth.Orders.Single().State==OrderTruthState.Unknown,"Thiếu readback WB vẫn được trình bày như số liệu chắc chắn.");
    return Task.CompletedTask;
});
await Check("Ozon and Yandex batch membership uses projection without WB semantics",()=>{
    foreach(var marketplace in new[]{Marketplace.Ozon,Marketplace.Yandex}) {
        var store=new StoreProfile(9100+(int)marketplace,marketplace,marketplace.ToString(),"","","","","",true);
        var status=marketplace==Marketplace.Ozon?"awaiting_packaging":"PROCESSING/STARTED";
        var rows=new[]{new FbsOrderRow(store.Id,marketplace,"B-1","SKU","Áo",1,status,false,"{}")};
        var service=new OrderTruthService();
        Expect(service.Build(store,rows,new HashSet<string>(),new Dictionary<string,OrderRemoteState>()).NewCount==1,"Queue mới của "+marketplace+" không được nhận diện.");
        Expect(service.Build(store,rows,new HashSet<string>{"B-1"},new Dictionary<string,OrderRemoteState>()).Orders.Single().State==OrderTruthState.InShipment,"Membership của "+marketplace+" bị áp quy tắc WB.");
    }
    return Task.CompletedTask;
});
await Check("Remote order state persists without replacing order history",()=>{
    var store=Store(Marketplace.Wildberries);var observed=DateTimeOffset.UtcNow;
    app.Db.MarkOrderRemoteState(store.Id,store.Marketplace,"105","new","waiting",true,observed);
    app.Db.MarkOrderRemoteState(store.Id,store.Marketplace,"106","new","canceled_by_client",true,observed);
    var states=app.Db.OrderRemoteStates(store.Id);
    Expect(states.Count==2&&states["105"].Complete&&states["106"].MarketplaceStatus=="canceled_by_client","Readback trạng thái không được lưu đúng theo cửa hàng/đơn.");
    return Task.CompletedTask;
});
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

await Check("Received orders stay hidden and historical open shipments remain visible",()=>{
    var wb=Store(Marketplace.Wildberries);var oz=Store(Marketplace.Ozon);
    app.Db.UpsertOrders(wb.Id,Marketplace.Wildberries,new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"901","WB-A","WB A",1,"new",false,"{}")});
    var old=new WbSupply("OLD-SUPPLY","MarketplaceHub yesterday",DateTimeOffset.UtcNow.AddDays(-1),false);
    app.Db.UpsertWbSupply(wb.Id,old,1);app.Db.StoreWbSupplyMembership(wb.Id,old.Id,new[]{"901"});
    Expect(app.Db.WbReceivedOrderIds(wb.Id).Contains("901")&&app.Db.WbSupplies(wb).Any(x=>x.Id==old.Id&&x.OrderCount==1),"WB receipt mask or older open shipment was lost.");
    app.Db.UpsertOrders(oz.Id,Marketplace.Ozon,new[]{new FbsOrderRow(oz.Id,Marketplace.Ozon,"P-901","OZ-A","Ozon A",2,"awaiting_packaging",false,"{}")});
    var batch=app.Db.CreateMarketplaceFbsBatch(oz,new[]{"P-901"});
    Expect(app.Db.MarketplaceReceivedOrderIds(oz).Contains("P-901"),"Ozon received order can reappear as new.");
    var listed=app.Db.MarketplaceFbsBatches(oz).Single(x=>x.Id==batch.Id);
    Expect(listed.OrderCount==1&&listed.Quantity==2&&app.Db.MarketplaceFbsBatchOrders(oz,batch.Id).Single().Status=="PENDING"&&app.Db.MarketplaceKizReservations(oz,"P-901").Count==0,"Receipt started packing/KIZ or did not create one shipment row.");
    return Task.CompletedTask;
});
await Check("Scanner prefix cannot bypass ownership or reimport a second physical KIZ",()=>{
    var wb=Store(Marketplace.Wildberries);var oz=Store(Marketplace.Ozon);const string gtin="04601234567893";var code="01"+gtin+"21"+Guid.NewGuid().ToString("N")+"\u001d91ABCD\u001d92proof";
    app.Db.UpsertKiz(code,gtin,"AVAILABLE");var held=app.Db.ReserveWbKiz(wb.Id,"ALIAS",gtin);Expect(held==code,"Unexpected test allocation.");
    var blocked=false;try{app.Db.ReserveMarketplaceKiz(oz,"P","100",0,gtin,"\u001d"+code);}catch(InvalidOperationException){blocked=true;}Expect(blocked,"Leading GS changed ownership identity.");
    app.Db.UpsertKiz("\u001d"+code,gtin,"AVAILABLE");app.Db.UpsertKiz(code.Replace("\u001d","<GS>"),gtin,"AVAILABLE");Expect(app.Db.Kiz().Count(x=>MarketplaceFbsPayloads.NormalizeCode(x.Code)==code)==1 && app.Db.Kiz().Single(x=>x.Code==code).Status=="RESERVED","Reimport created an available alias of an owned physical code.");return Task.CompletedTask;
});
await Check("Ozon label task survives restart state and ambiguous create blocks duplicates",()=>{
    var oz=Store(Marketplace.Ozon);var first=app.Db.GetOrCreateOzonLabelJob(oz,"P-LABEL");
    Expect(first.Status=="NEW"&&first.TaskId=="","Initial label checkpoint is invalid.");
    Expect(app.Db.TryBeginOzonLabelCreate(oz,"P-LABEL")&&!app.Db.TryBeginOzonLabelCreate(oz,"P-LABEL"),"Concurrent/restarted create can claim a second Ozon label task.");
    Expect(app.Db.GetOrCreateOzonLabelJob(oz,"P-LABEL").Status=="CREATE_PENDING","Create claim was not durable.");
    var blocked=app.Db.SaveOzonLabelJob(oz,"P-LABEL","","RECONCILE_REQUIRED","create_outcome_unknown");
    Expect(blocked.Status=="RECONCILE_REQUIRED"&&blocked.TaskId=="","Ambiguous label create was not retained.");
    var persisted=app.Db.GetOrCreateOzonLabelJob(oz,"P-LABEL");Expect(persisted.Status=="RECONCILE_REQUIRED","Restart reset ambiguous label job.");
    app.Db.SaveOzonLabelJob(oz,"P-READY","3001","POLLING");var ready=app.Db.SaveOzonLabelJob(oz,"P-READY","","READY");
    Expect(ready.TaskId=="3001"&&ready.Status=="READY","Existing task_id was discarded during recovery.");return Task.CompletedTask;
});
await Check("Ozon exemplar mutation fingerprint survives restart and blocks a second submit",()=>{
    var oz=Store(Marketplace.Ozon);var fingerprint=new string('a',64);
    Expect(app.Db.TryBeginOzonExemplarMutation(oz,"P-EXEMPLAR",fingerprint),"Initial exemplar mutation was not claimed.");
    Expect(!app.Db.TryBeginOzonExemplarMutation(oz,"P-EXEMPLAR",fingerprint),"Second exemplar mutation bypassed durable claim.");
    app.Db.SaveOzonExemplarMutation(oz,"P-EXEMPLAR",fingerprint,"RECONCILE_REQUIRED","submit_outcome_unknown");
    var persisted=app.Db.OzonExemplarMutation(oz,"P-EXEMPLAR");
    Expect(persisted is not null&&persisted.Fingerprint==fingerprint&&persisted.State=="RECONCILE_REQUIRED","Ambiguous exemplar state did not survive restart.");return Task.CompletedTask;
});
await Check("Ozon duplicate offer is rejected before exemplar mutation",async()=>{
    var oz=Store(Marketplace.Ozon);var requests=0;Http(_=>{requests++;return Json("{}");});
    var snapshot=new MarketplaceFbsSnapshot(oz.Id,Marketplace.Ozon,"P-DUP","awaiting_packaging","",new[]{
        new MarketplaceFbsItem("100","SAME","A",1,true,new JsonObject()),
        new MarketplaceFbsItem("200","SAME","B",1,true,new JsonObject())},new JsonObject(),false,"");
    var method=typeof(AppServices).GetMethod("PrepareDurableOzonKizAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var task=(Task<PriceUpdateResult>)method.Invoke(app,new object[]{oz,snapshot,new Dictionary<string,IReadOnlyList<string>>{{"100",new[]{"code-a"}},{"200",new[]{"code-b"}}},CancellationToken.None})!;
    var result=await task;Expect(!result.Success&&requests==0&&app.Db.OzonExemplarMutation(oz,"P-DUP") is null,"Duplicate offer overwrote KIZ or created a mutation checkpoint.");
});
await Check("Ozon rejected preflight does not lock exemplar retry",async()=>{
    var oz=Store(Marketplace.Ozon);Http(r=>r.RequestUri!.AbsolutePath switch{
        "/v3/posting/fbs/get"=>Json("{\"result\":{\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"product_id\":100,\"offer_id\":\"A\",\"quantity\":1}]}}"),
        "/v6/fbs/posting/product/exemplar/create-or-get"=>Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":1}]}]}"),
        "/v5/fbs/posting/product/exemplar/validate"=>Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"status\":\"rejected\"}]}]}"),
        _=>throw new Exception("Mutation set must not run after rejected validation")});
    var snapshot=new MarketplaceFbsSnapshot(oz.Id,Marketplace.Ozon,"P-REJECT","awaiting_packaging","",new[]{new MarketplaceFbsItem("100","A","A",1,true,new JsonObject())},new JsonObject(),false,"");
    var method=typeof(AppServices).GetMethod("PrepareDurableOzonKizAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var result=await (Task<PriceUpdateResult>)method.Invoke(app,new object[]{oz,snapshot,new Dictionary<string,IReadOnlyList<string>>{{"100",new[]{"code-a"}}},CancellationToken.None})!;
    Expect(!result.Success&&app.Db.OzonExemplarMutation(oz,"P-REJECT") is null,"Known preflight rejection permanently locked the posting.");
});
}finally{foreach(var store in stores)app.Db.DeleteStore(store.Id);}
Console.WriteLine($"{checks-failures.Count}/{checks} persistence and FBS workflow checks passed");return failures.Count==0?0:1;
sealed class FixtureHttp(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(response(request));}}
