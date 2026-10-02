using MarketplaceHub.Core;
using MarketplaceHub.Services.Fbs;
using MarketplaceHub.Services.Suz;
using System.Text.Json.Nodes;
namespace MarketplaceHub.Services;
public sealed record FbsWorkflowDependencies(ISuzClient SuzClient,IKizLegalReader LegalReader,IWorkflowClock Clock,IKizCodeProtector? CodeProtector=null);
public sealed partial class AppServices
{
    public FbsLabelJobCoordinator LabelJobs {get;private set;}=null!;
    public KizEligibilityService WorkflowEligibility {get;private set;}=null!;
    public KizPurchaseCoordinator WorkflowPurchases {get;private set;}=null!;
    private void InitializeLabelWorkflows(FbsWorkflowDependencies? dependencies)
    {
        var clock=dependencies?.Clock??new WorkflowClock();ISuzClient client;IKizLegalReader legal;
        if(dependencies is not null){client=dependencies.SuzClient;legal=dependencies.LegalReader;if(dependencies.CodeProtector is not null)Db.ConfigureWorkflowCodeProtector(dependencies.CodeProtector);}
        else{var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false});var policy=new OperationPolicy(clock,httpClient:http);var suz=new SuzHttpClient(http,new CryptoProSuzSigner(),profile=>{
            if(Db.ZnakCredentialVersion()!=profile.CredentialVersion||Db.SuzProfileForStore(profile.StoreId)!=profile||!Db.Stores().Any(x=>x.Id==profile.StoreId))throw new InvalidOperationException("credential_version_changed");return Db.GetZnakConfig();},policy,clock);client=suz;legal=new TrueApiKizReader(suz,policy,http,clock,Db.CodeProtector);}
        WorkflowPurchases=new(Db,client,clock);WorkflowEligibility=new(Db,legal,clock);LabelJobs=new(Db,m=>new FbsLabelAdapter(this,m),WorkflowPurchases,WorkflowEligibility,License,new(),clock);
    }
    public Task<LabelJobResult> ExportFbsLabelsAsync(LabelTarget target,IProgress<LabelJobResult>? progress=null,CancellationToken ct=default)=>LabelJobs.StartOrResumeAsync(target,progress,ct);
    internal void RequireWorkflowAuthorization(FbsWorkflowContext context,bool requireActive=true)
    {if(!License.CanRunFbsWorkflow().Allowed||!Db.WorkflowAuthorizationMatches(context,requireActive))throw new InvalidOperationException("workflow_authorization_required");}
    internal string ResolveWorkflowMarketplaceGtin(StoreProfile store,MarketplaceFbsItem item)
    {
        var product=Db.Products(store.Id).SingleOrDefault(x=>x.Marketplace==store.Marketplace&&x.Sku==item.Offer);if(product is null)return "";
        var variants=ProductCatalog.Entry(product).Variants;var explicitCodes=ProductCatalog.Strings(item.Raw["barcodes"]).Concat(item.Raw["barcode"] is JsonValue value?new[]{value.ToString()}:Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);var matched=(explicitCodes.Count>0?variants.Where(v=>v.Barcodes.Any(explicitCodes.Contains)):variants).ToArray();
        if(matched.Length==1&&Db.ConfirmedGtinMappings(store).TryGetValue((product.Sku,matched[0].VariantId),out var confirmed))return confirmed;
        var gtins=matched.SelectMany(v=>v.Barcodes).Where(x=>explicitCodes.Count==0||explicitCodes.Contains(x)).Select(ProductCatalog.NormalizeGtin).Where(x=>x!="").Distinct().ToArray();return gtins.Length==1?gtins[0]:"";
    }
    internal string ResolveWorkflowWbGtin(StoreProfile store,FbsOrderRow order)
    {
        var product=ProductCatalog.ResolveOrder(store,order,Db.Products(store.Id));if(product is null)return "";
        var raw=JsonNode.Parse(order.RawJson);var codes=ProductCatalog.Strings(raw?["skus"]).ToHashSet(StringComparer.Ordinal);var chrt=raw?["chrtId"]?.ToString();
        var variants=ProductCatalog.Entry(product).Variants.Where(x=>(string.IsNullOrWhiteSpace(chrt)||x.VariantId==chrt)&&(codes.Count==0||x.Barcodes.Any(codes.Contains))).ToArray();if(variants.Length!=1)return "";
        if(Db.ConfirmedGtinMappings(store).TryGetValue((product.Sku,variants[0].VariantId),out var mapped))return mapped;return ProductCatalog.UniqueGtin(variants[0].Barcodes.Where(x=>codes.Count==0||codes.Contains(x)));
    }
    private async Task<FbsWorkflowContext> PrepareWorkflowContextAsync(LabelTarget target,IFbsLabelAdapter adapter,CancellationToken ct)
    {
        if(!License.CanRunFbsWorkflow().Allowed)throw new InvalidOperationException("license_required");var snapshot=await adapter.ReadSnapshotAsync(target,ct);var job=Db.GetOrCreateLabelJob(snapshot);if(job.Stage==FbsLabelJobStage.SnapshotChanged)throw new InvalidOperationException("snapshot_changed");var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,Db.LabelJobProfile(job.Id));RequireWorkflowAuthorization(context);
        var remote=await adapter.ReadAssignedCodesAsync(context,ct);RequireWorkflowAuthorization(context);Db.BindAssignedKiz(context,remote);var codes=Db.ReserveScopedKiz(context,context.Snapshot.Units);
        var proof=await WorkflowEligibility.VerifyAsync(context,codes,ct);RequireWorkflowAuthorization(context);if(proof.Any(x=>x.Stage!="Ready"))throw new InvalidOperationException("workflow_context_required: chuẩn bị KIZ, trạng thái pháp lý và xác nhận dán mã trong tác vụ xuất nhãn.");return context;
    }
}
