using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.TestSupport;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class ServiceGateTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
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
            var api=new MarketplaceGateway();typeof(MarketplaceGateway).GetField("http",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(api,new HttpClient(new AwaitHttp(async()=>{entered.SetResult();await release.Task;return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"result\":{\"posting_number\":\"P\",\"status\":\"awaiting_packaging\",\"products\":[{\"sku\":100,\"offer_id\":\"A\",\"quantity\":1,\"name\":\"A\"}]}}",System.Text.Encoding.UTF8,"application/json")};})));
            var app=WorkflowTestSupport.App(f.Db,api);var read=app.ReadFreshMarketplaceFbsAsync(store,"P");await entered.Task;f.Db.DeleteStore(store.Id);release.SetResult();try{await read;}catch(InvalidOperationException){ }
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM order_remote_states WHERE store_id="+store.Id))==0,"Late remote read recreated deleted store state");
        });
    }
    private sealed class AwaitHttp(Func<Task<HttpResponseMessage>> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>response();}
    private sealed class GateHttp:HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;throw new Exception("Unauthorized request");}}
}
