using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.TestSupport;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class ServiceGateTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("wb_delivery_requires_physical_and_legal_workflow_proof",async()=>{
            using var f=WorkflowFixture.Create();var store=f.Db.SaveStore(new(0,Marketplace.Wildberries,"delivery gate","","","","","fixture",true));f.Db.SaveSuzProfile(f.Profile with{Id="delivery-profile",StoreId=store.Id});f.Db.UpsertOrders(store.Id,store.Marketplace,new[]{new FbsOrderRow(store.Id,store.Marketplace,"1","A","A",1,"confirm",true,"{}")});f.Db.StoreWbSupplyMembership(store.Id,"DELIVER",new[]{"1"});var deliveries=0;
            var api=new MarketplaceGateway();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new RequestHttp(request=>{
                var path=request.RequestUri!.AbsolutePath;object body=path.EndsWith("/order-ids")?new{orderIds=new[]{1}}:path.EndsWith("/status")?new{orders=new[]{new{id=1,supplierStatus="confirm",wbStatus="waiting"}}}:path.EndsWith("/meta")?new{orders=new[]{new{id=1,metaDetails=new[]{new{key="sgtin",value=new[]{WorkflowFixture.Raw},decision="filled"}}}}}:new{id="DELIVER",done=false,createdAt=DateTimeOffset.UtcNow.ToString("O")};if(path.EndsWith("/deliver"))deliveries++;
                return new(System.Net.HttpStatusCode.OK){Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(body),System.Text.Encoding.UTF8,"application/json")};
            })));
            var app=WorkflowTestSupport.App(f.Db,api);var gtins=new Dictionary<string,string>{{"1",WorkflowFixture.Gtin}};var blocked=await app.DeliverVerifiedWbSupplyAsync(store,"DELIVER",gtins);Expect(!blocked.Success&&deliveries==0,"Delivery bypassed physical/legal workflow proof");
            var job=f.Db.ActiveLabelJobs(store.Id).Single();var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,f.Db.LabelJobProfile(job.Id));var held=f.Db.BoundScopedKiz(context);foreach(var pair in held)f.Db.ConfirmPhysicalMark(new(pair.Key,f.Db.CodeProtector.Identity(pair.Value),DateTimeOffset.UtcNow));var ready=await app.DeliverVerifiedWbSupplyAsync(store,"DELIVER",gtins);Expect(ready.Success&&deliveries==1,"Verified physical/legal shipment could not be delivered");
        });
        await r.CheckAsync("deleted_store_rejects_direct_remote_state_write",()=>{
            using var f=WorkflowFixture.Create();f.Db.DeleteStore(f.Store.Id);f.Db.MarkOrderRemoteState(f.Store.Id,f.Store.Marketplace,"P","PACKED","");
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM order_remote_states WHERE store_id="+f.Store.Id))==0,"Deleted store accepted a state write");return Task.CompletedTask;
        });
        await r.CheckAsync("missing_variant_gtin_blocks_snapshot",async()=>{
            using var f=WorkflowFixture.Create();var store=f.Db.SaveStore(new(0,Marketplace.Yandex,"missing GTIN","","fixture-api","456","789","fixture-token",true));var batch=f.Db.CreateMarketplaceFbsBatch(store,new[]{"1"});
            var api=new MarketplaceGateway();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new AwaitHttp(()=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"order\":{\"id\":1,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":1,\"hasCis\":true}]}}",System.Text.Encoding.UTF8,"application/json")}))));
            var rejected=false;try{await new MarketplaceHub.Services.Fbs.FbsLabelAdapter(WorkflowTestSupport.App(f.Db,api),store.Marketplace,marketGtin:_=>"").ReadSnapshotAsync(new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,batch.Id),default);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("gtin_variant_required");}
            Expect(rejected,"Empty GTIN created a marked-unit snapshot");
        });
        await r.CheckAsync("late_wb_membership_read_cannot_recreate_deleted_store",async()=>{
            using var f=WorkflowFixture.Create();var store=f.Db.SaveStore(new(0,Marketplace.Wildberries,"WB delete","","","","","fixture-token",true));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var api=new MarketplaceGateway();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new AwaitHttp(async()=>{entered.SetResult();await release.Task;return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"orderIds\":[1]}",System.Text.Encoding.UTF8,"application/json")};})));
            var task=WorkflowTestSupport.App(f.Db,api).ReadWbSupplyOrdersAsync(store,"S");await entered.Task;f.Db.DeleteStore(store.Id);release.SetResult();try{await task;}catch{ }
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM wb_supply_orders WHERE store_id="+store.Id))==0,"Late membership read recreated deleted store");
        });
        await r.CheckAsync("legacy_available_is_not_scoped_verified_stock",async()=>{
            using var f=WorkflowFixture.Create();f.Db.UpsertKiz(WorkflowFixture.Raw,WorkflowFixture.Gtin,"AVAILABLE");
            var app=new AppServices(f.Db,new MarketplaceGateway(),WorkflowTestSupport.SignedLicense(()=>DateTimeOffset.UtcNow));var result=await app.EnsureKizQuantityAsync(f.Store.Id,"A",WorkflowFixture.Gtin,1);
            Expect(!result.Ok&&result.Message.Contains("workflow_context_required",StringComparison.Ordinal),"Unscoped legacy AVAILABLE was treated as verified stock");
        });
        await r.CheckAsync("pack_service_requires_signed_license_before_mutation",async()=>{
            using var f=WorkflowFixture.Create();var store=f.Db.SaveStore(new(0,Marketplace.Ozon,"gate fixture","123","fixture-api","456","789","fixture-token",true));
            var api=new MarketplaceGateway();var handler=new GateHttp();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(handler));
            var app=new AppServices(f.Db,api,WorkflowTestSupport.SignedLicense(()=>DateTimeOffset.UtcNow,expired:true));var result=await app.PackMarketplaceFbsAsync(store,new[]{"P"},_=>"",true);
            Expect(!result.Success&&handler.Calls==0,"Invalid license reached remote packing boundary");
        });
        await r.CheckAsync("late_marketplace_read_cannot_recreate_deleted_store_state",async()=>{
            using var f=WorkflowFixture.Create();var store=f.Db.SaveStore(new(0,Marketplace.Ozon,"delete fixture","123","fixture-api","","","",true));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const string body="{\"result\":{\"posting_number\":\"P\",\"status\":\"awaiting_packaging\",\"requirements\":{\"products_requiring_mandatory_mark\":[]},\"products\":[{\"sku\":100,\"offer_id\":\"A\",\"quantity\":1,\"name\":\"A\"}]}}";var calls=0;
            var api=new MarketplaceGateway();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new AwaitHttp(async()=>{if(++calls>1){entered.SetResult();await release.Task;}return new(System.Net.HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")};})));
            var app=WorkflowTestSupport.App(f.Db,api);var before=await app.ReadFreshMarketplaceFbsAsync(store,"P");Expect(before.Items.Count==1&&f.Db.OrderRemoteStates(store.Id).Count==1,"Positive fixture did not reach state persistence");
            var read=app.ReadFreshMarketplaceFbsAsync(store,"P");await entered.Task;f.Db.DeleteStore(store.Id);release.SetResult();try{await read;}catch(InvalidOperationException){ }
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM order_remote_states WHERE store_id="+store.Id))==0,"Late remote read recreated deleted store state");
        });
    }
    private sealed class RequestHttp(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));}
    private sealed class AwaitHttp(Func<Task<HttpResponseMessage>> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>response();}
    private sealed class GateHttp:HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;throw new Exception("Unauthorized request");}}
}
