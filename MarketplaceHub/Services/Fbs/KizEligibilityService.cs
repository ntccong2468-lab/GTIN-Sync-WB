using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services.Suz;
namespace MarketplaceHub.Services.Fbs;
public sealed class KizEligibilityService(AppDatabase db,IKizLegalReader legal,IWorkflowClock clock)
{
    public async Task<IReadOnlyList<UnitWorkflowResult>> VerifyAsync(FbsWorkflowContext context,IReadOnlyDictionary<FbsUnitKey,string> codes,CancellationToken ct)
    {
        IReadOnlyList<KizLegalProof> proofs=Array.Empty<KizLegalProof>();var profile=context.Profile;
        if(profile is not null&&SuzProfileCatalog.Supported(profile)&&db.WorkflowAuthorizationMatches(context,requireActive:false)){
            try{proofs=await legal.ReadAsync(profile,codes.Values.Distinct(StringComparer.Ordinal).ToArray(),ct);}
            catch(Exception e)when(e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException){ }
        }
        var result=new List<UnitWorkflowResult>();
        foreach(var demand in context.Snapshot.Units){
            if(!demand.RequiresKiz){result.Add(new(demand.Unit,"Ready",null,null));continue;}
            if(!codes.TryGetValue(demand.Unit,out var raw)){result.Add(new(demand.Unit,"AwaitingCodes",null,"code_missing"));continue;}
            var hash=db.CodeProtector.Identity(raw);var matched=proofs.Where(x=>x.CodeHash==hash).ToArray();
            var valid=profile is not null&&matched.Length==1&&matched[0].Gtin==demand.Gtin&&matched[0].OwnerInn==profile.OwnerInn&&matched[0].Environment==profile.Environment&&matched[0].RawStatus=="INTRODUCED"&&matched[0].StatusEx is "" or "EMPTY"&&matched[0].PackageType=="UNIT"&&matched[0].ObservedAt<=clock.UtcNow&&matched[0].ObservedAt>=clock.UtcNow.AddMinutes(-5)&&db.WorkflowAuthorizationMatches(context,requireActive:false);
            if(!valid){result.Add(new(demand.Unit,"AwaitingLegalState",hash,"legal_proof_required"));continue;}
            db.SaveLegalProof(new(profile!.StoreId,profile.OwnerInn,profile.Environment),matched[0]);
            result.Add(new(demand.Unit,db.PhysicalMarkMatches(demand.Unit,hash)?"Ready":"AwaitingPhysicalMark",hash,null));
        }return result;
    }
}
