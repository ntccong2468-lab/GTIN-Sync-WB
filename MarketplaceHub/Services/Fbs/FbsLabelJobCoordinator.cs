using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services.Suz;
namespace MarketplaceHub.Services.Fbs;
public interface IFbsLabelAdapter
{
    Task<LabelJobSnapshot> ReadSnapshotAsync(LabelTarget target,CancellationToken ct);
    Task<IReadOnlyDictionary<FbsUnitKey,string>> ReadAssignedCodesAsync(FbsWorkflowContext context,CancellationToken ct);
    Task<IReadOnlyList<FbsUnitKey>> ReadCompletedUnitsAsync(FbsWorkflowContext context,CancellationToken ct);
    Task<IReadOnlyList<UnitWorkflowResult>> EnsureMarkedAndPackedAsync(FbsWorkflowContext context,IReadOnlyDictionary<FbsUnitKey,string> codes,CancellationToken ct);
    Task<IReadOnlyList<VerifiedLabel>> DownloadLabelsAsync(FbsWorkflowContext context,IReadOnlyList<FbsUnitKey> eligibleUnits,CancellationToken ct);
}
public sealed record VerifiedLabel(string RemoteOrderId,LabelResult Label,IReadOnlyList<FbsUnitKey> Units,string SourceOperation,string SourceEvidenceHash);
public sealed class FbsLabelJobCoordinator(AppDatabase db,Func<Marketplace,IFbsLabelAdapter> adapters,KizPurchaseCoordinator purchases,KizEligibilityService eligibility,LicenseAccessService license,LabelArtifactStore artifacts,IWorkflowClock clock)
{
    public async Task<LabelJobResult> StartOrResumeAsync(LabelTarget target,IProgress<LabelJobResult>? progress,CancellationToken ct)
    {
        try{var snapshot=await adapters(target.Marketplace).ReadSnapshotAsync(target,ct);var job=db.GetOrCreateLabelJob(snapshot);return await ResumeAsync(job.Id,progress,ct);}
        catch(OperationCanceledException){return Empty(FbsLabelJobStage.Paused,"operation_paused");}
        catch(Exception e)when(e is InvalidOperationException or HttpRequestException or IOException){return Empty(FbsLabelJobStage.NeedsReconciliation,"target_read_failed");}
    }
    public async Task<LabelJobResult> ResumeAsync(string jobId,IProgress<LabelJobResult>? progress,CancellationToken ct,WorkflowRunMode mode=WorkflowRunMode.UserRequested)
    {
        var job=db.GetLabelJob(jobId);if(job is null)return Empty(FbsLabelJobStage.NeedsReconciliation,"job_deleted");
        if(!db.TryClaimLabelJob(jobId,job.Version))return Result(job,"job_busy");
        var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,db.LabelJobProfile(jobId));
        try{
            ct.ThrowIfCancellationRequested();
            if(job.Stage==FbsLabelJobStage.SnapshotChanged)return Result(job,"snapshot_changed");
            if(!Allowed(context))return Save(context,FbsLabelJobStage.NeedsReconciliation,db.LabelJobUnits(jobId),"authorization_required",progress);
            var adapter=adapters(job.Snapshot.Target.Marketplace);
            if(!await SnapshotMatches(context,adapter,ct))return Save(context,FbsLabelJobStage.SnapshotChanged,db.LabelJobUnits(jobId),"snapshot_changed",progress);
            var remote=await adapter.ReadAssignedCodesAsync(context,ct);
            ct.ThrowIfCancellationRequested();
            if(!Allowed(context))return Result(job,"authorization_changed");
            db.BindAssignedKiz(context,remote);
            // All durable intents are recovered first, before calculating a new deficit.
            foreach(var intent in db.LabelJobPurchases(jobId)){
                if(!Allowed(context))return Result(job,"authorization_changed");
                await purchases.ResumeAsync(intent.Id,ct,mode);
            }
            var codes=db.ReserveScopedKiz(context,context.Snapshot.Units);
            var pendingPurchase=false;
            foreach(var group in context.Snapshot.Units.Where(x=>x.RequiresKiz&&!codes.ContainsKey(x.Unit)).GroupBy(x=>x.Gtin)){
                if(mode==WorkflowRunMode.RecoveryOnly||context.Profile is null){pendingPurchase=true;continue;}
                var existing=db.LabelJobPurchases(jobId).FirstOrDefault(x=>x.Request.Gtin==group.Key);
                if(existing is not null){pendingPurchase=true;continue;} // Recovered-but-partial is evidence, never a top-up trigger.
                if(!Allowed(context)||!await SnapshotMatches(context,adapter,ct))return Save(context,FbsLabelJobStage.SnapshotChanged,db.LabelJobUnits(jobId),"snapshot_changed",progress);
                var request=new PurchaseIntentRequest(jobId,job.Revision,context.Profile,group.Key,group.Count(),"");request=request with{PayloadHash=SuzHttpClient.PayloadHash(request)};
                Save(context,FbsLabelJobStage.Purchasing,db.LabelJobUnits(jobId),null,progress);var purchased=await purchases.EnsureAsync(request,ct);pendingPurchase|=purchased.Stage!=PurchaseStage.CodesRecovered;
            }
            if(!Allowed(context))return Result(job,"authorization_changed");
            codes=db.ReserveScopedKiz(context,context.Snapshot.Units);
            var verified=await eligibility.VerifyAsync(context,codes,ct);
            if(!Allowed(context))return Result(job,"authorization_changed");
            var old=db.LabelJobUnits(jobId);var uncertain=old.Where(x=>x.Stage is "Attaching" or "WbAttachSending" or "AttachUnknown").Select(x=>x.Unit).ToHashSet();
            var completed=(await adapter.ReadCompletedUnitsAsync(context,ct)).ToHashSet();
            if(uncertain.Any(u=>!completed.Contains(u)))return Save(context,FbsLabelJobStage.NeedsReconciliation,old,"packing_readback_required",progress);
            if(uncertain.Any(u=>context.Snapshot.Units.Single(x=>x.Unit==u).RequiresKiz&&(!remote.TryGetValue(u,out var actual)||!codes.TryGetValue(u,out var held)||db.CodeProtector.Identity(actual)!=db.CodeProtector.Identity(held))))
                return Save(context,FbsLabelJobStage.NeedsReconciliation,old.Select(x=>uncertain.Contains(x.Unit)?x with{Stage="AttachUnknown",ErrorCode="attach_readback_required"}:x).ToArray(),"attach_readback_required",progress);
            // A no-KIZ pack request also has an uncertain checkpoint; the adapter owns its pack journal.
            var ready=verified.Where(x=>x.Stage=="Ready").Select(x=>x.Unit).ToHashSet();
            var eligible=context.Snapshot.Units.GroupBy(x=>x.Unit.OrderId).Where(g=>g.All(x=>ready.Contains(x.Unit))).SelectMany(g=>g.Select(x=>x.Unit)).ToArray();
            if(mode==WorkflowRunMode.RecoveryOnly)return Save(context,WaitingStage(verified,pendingPurchase),old.Count==0?verified:old,"user_requested_phase_required",progress);
            if(eligible.Length==0)return Save(context,WaitingStage(verified,pendingPurchase),verified,null,progress);
            var already=uncertain.Count>0&&uncertain.All(u=>!context.Snapshot.Units.Single(x=>x.Unit==u).RequiresKiz||remote.TryGetValue(u,out var raw)&&codes.TryGetValue(u,out var held)&&db.CodeProtector.Identity(raw)==db.CodeProtector.Identity(held));
            if(!already){
                ct.ThrowIfCancellationRequested();
                if(!Allowed(context)||!await SnapshotMatches(context,adapter,ct))return Save(context,FbsLabelJobStage.SnapshotChanged,verified,"snapshot_changed",progress);
                var checkpoint=verified.Select(x=>eligible.Contains(x.Unit)?x with{Stage="Attaching"}:x).ToArray();
                if(!SaveCheckpoint(context,FbsLabelJobStage.Attaching,checkpoint))return Result(job,"job_version_changed");
                try{
                    var attached=await adapter.EnsureMarkedAndPackedAsync(context,codes.Where(x=>eligible.Contains(x.Key)).ToDictionary(x=>x.Key,x=>x.Value),ct);
                    if(attached.Where(x=>eligible.Contains(x.Unit)).Count(x=>x.Stage=="Verified")!=eligible.Length)
                        return Save(context,FbsLabelJobStage.NeedsReconciliation,checkpoint.Select(x=>x.Stage=="Attaching"?x with{Stage="AttachUnknown"}:x).ToArray(),"remote_verification_required",progress);
                }catch(Exception e)when(e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException){return Save(context,FbsLabelJobStage.NeedsReconciliation,checkpoint.Select(x=>x.Stage=="Attaching"?x with{Stage="AttachUnknown"}:x).ToArray(),"attach_readback_required",progress);}
            }
            if(!Allowed(context)||!await SnapshotMatches(context,adapter,ct))return Save(context,FbsLabelJobStage.SnapshotChanged,verified,"snapshot_changed",progress);
            var assigned=await adapter.ReadAssignedCodesAsync(context,ct);
            if(eligible.Any(u=>context.Snapshot.Units.Single(x=>x.Unit==u).RequiresKiz&&(!assigned.TryGetValue(u,out var raw)||!codes.TryGetValue(u,out var held)||db.CodeProtector.Identity(raw)!=db.CodeProtector.Identity(held))))return Save(context,FbsLabelJobStage.NeedsReconciliation,verified,"remote_code_mismatch",progress);
            verified=await eligibility.VerifyAsync(context,codes,ct);
            eligible=context.Snapshot.Units.GroupBy(x=>x.Unit.OrderId).Where(g=>g.All(x=>verified.Any(v=>v.Unit==x.Unit&&v.Stage=="Ready"))).SelectMany(g=>g.Select(x=>x.Unit)).ToArray();
            if(!Allowed(context))return Result(job,"authorization_changed");
            if(eligible.Length==0)return Save(context,WaitingStage(verified,pendingPurchase),verified,null,progress);
            var manifest=db.LabelJobArtifacts(jobId).ToList();
            ct.ThrowIfCancellationRequested();
            foreach(var label in await adapter.DownloadLabelsAsync(context,eligible,ct)){
                if(!Allowed(context))return Result(job,"authorization_changed");
                var whole=context.Snapshot.Units.Where(x=>x.Unit.OrderId==label.RemoteOrderId).Select(x=>x.Unit).ToHashSet();
                if(!whole.SetEquals(label.Units)||whole.Any(u=>!eligible.Contains(u)))continue;
                try{var artifact=await artifacts.PersistAsync(context,label,ct);manifest.RemoveAll(x=>x.OfficialMarketplaceLabel&&x.Units.Any(whole.Contains));manifest.Add(artifact);if(!db.SaveLabelJobArtifacts(context,manifest))return Result(job,"authorization_changed");}
                catch(InvalidOperationException){ }
            }
            var covered=manifest.Where(x=>x.OfficialMarketplaceLabel).SelectMany(x=>x.Units).ToHashSet();var final=verified.Select(x=>covered.Contains(x.Unit)?x with{Stage="LabelsReady"}:x).ToArray();
            return Save(context,covered.Count==context.Snapshot.Units.Count?FbsLabelJobStage.LabelsReady:manifest.Any(x=>x.OfficialMarketplaceLabel)?FbsLabelJobStage.Partial:WaitingStage(verified,pendingPurchase),final,null,progress);
        }
        catch(OperationCanceledException){return Save(context,FbsLabelJobStage.Paused,db.LabelJobUnits(jobId),"operation_paused",progress);}
        catch(Exception e)when(e is InvalidOperationException or HttpRequestException or IOException or TimeoutException){return Save(context,FbsLabelJobStage.NeedsReconciliation,db.LabelJobUnits(jobId),"workflow_reconciliation_required",progress);}
        finally{db.ReleaseLabelJob(jobId);}
    }
    public async Task<LabelJobResult> ReprintAsync(string jobId,int revision,CancellationToken ct)
    {
        var job=db.GetLabelJob(jobId);if(job is null||job.Revision!=revision)return Empty(FbsLabelJobStage.NeedsReconciliation,"revision_not_found");
        var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,db.LabelJobProfile(job.Id));if(!Allowed(context,requireActive:false))return Result(job,"authorization_required");
        var manifest=db.LabelJobArtifacts(jobId).ToList();var broken=new List<LabelArtifact>();foreach(var item in manifest)if(!await artifacts.ValidateAsync(item,ct))broken.Add(item);
        if(broken.Count>0){var units=broken.SelectMany(x=>x.Units).Distinct().ToArray();foreach(var label in await adapters(job.Snapshot.Target.Marketplace).DownloadLabelsAsync(context,units,ct)){
            if(!Allowed(context,requireActive:false))return Result(job,"authorization_changed");var original=broken.FirstOrDefault(x=>x.Units.ToHashSet().SetEquals(label.Units));if(original is null)continue;
            var restored=await artifacts.PersistAsync(context,label,ct);manifest.Remove(original);manifest.Add(restored);
        }if(!db.SaveLabelJobArtifacts(context,manifest))return Result(job,"authorization_changed");}
        return Result(job,broken.Any(x=>manifest.Contains(x))?"label_refetch_pending":null);
    }
    public async Task<LabelJobResult> CreateRevisionAsync(string jobId,bool sellerConfirmed,CancellationToken ct)
    {
        var job=db.GetLabelJob(jobId);if(job is null)return Empty(FbsLabelJobStage.NeedsReconciliation,"job_deleted");
        if(!sellerConfirmed||db.LabelJobUnits(jobId).Any(x=>x.Stage is "Attaching" or "WbAttachSending" or "AttachUnknown")||db.LabelJobPurchases(jobId).Any(x=>x.Stage is not (PurchaseStage.CodesRecovered or PurchaseStage.Rejected)))return Result(job,"reconcile_before_revision");
        var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,db.LabelJobProfile(job.Id));if(!Allowed(context))return Result(job,"authorization_required");
        var fresh=await adapters(job.Snapshot.Target.Marketplace).ReadSnapshotAsync(job.Snapshot.Target,ct);if(!Allowed(context))return Result(job,"authorization_changed");return Result(db.NewLabelRevision(jobId,fresh));
    }
    private bool Allowed(FbsWorkflowContext context,bool requireActive=true)=>license.CanRunFbsWorkflow().Allowed&&db.WorkflowAuthorizationMatches(context,requireActive);
    private async Task<bool> SnapshotMatches(FbsWorkflowContext context,IFbsLabelAdapter adapter,CancellationToken ct){var fresh=await adapter.ReadSnapshotAsync(context.Snapshot.Target,ct);ct.ThrowIfCancellationRequested();return WorkflowIdentity.Snapshot(fresh)==WorkflowIdentity.Snapshot(context.Snapshot);}
    private bool SaveCheckpoint(FbsWorkflowContext context,FbsLabelJobStage stage,IReadOnlyList<UnitWorkflowResult> units){var current=db.GetLabelJob(context.JobId);return current is not null&&db.TrySaveLabelJob(current.Id,current.Version,stage,units);}
    private LabelJobResult Save(FbsWorkflowContext context,FbsLabelJobStage stage,IReadOnlyList<UnitWorkflowResult> units,string? error,IProgress<LabelJobResult>? progress){SaveCheckpoint(context,stage,units);var job=db.GetLabelJob(context.JobId);var result=job is null?Empty(FbsLabelJobStage.NeedsReconciliation,"job_deleted"):Result(job,error);progress?.Report(result);return result;}
    private LabelJobResult Result(FbsLabelJob job,string? error=null){var units=db.LabelJobUnits(job.Id);return new(job.Id,job.Revision,job.Stage,units.Count(x=>x.Stage=="LabelsReady"),job.Snapshot.Units.Count,units,db.LabelJobArtifacts(job.Id),error);}
    private static FbsLabelJobStage WaitingStage(IReadOnlyList<UnitWorkflowResult> units,bool purchase)=>units.Any(x=>x.Stage=="AwaitingLegalState")?FbsLabelJobStage.AwaitingLegalState:units.Any(x=>x.Stage=="AwaitingPhysicalMark")?FbsLabelJobStage.AwaitingPhysicalMark:purchase?FbsLabelJobStage.AwaitingPurchase:FbsLabelJobStage.AwaitingCodes;
    private static LabelJobResult Empty(FbsLabelJobStage stage,string error)=>new("",0,stage,0,0,Array.Empty<UnitWorkflowResult>(),Array.Empty<LabelArtifact>(),error);
}
