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
    }
    private sealed class GateHttp:HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;throw new Exception("Unauthorized request");}}
}
