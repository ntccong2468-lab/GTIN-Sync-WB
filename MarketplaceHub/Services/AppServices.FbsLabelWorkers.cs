using MarketplaceHub.Core;
using System.Security.Cryptography;
namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    private sealed class LabelWorker(LabelTarget target,CancellationTokenSource stop)
    {
        public LabelTarget Target=target;
        public CancellationTokenSource Stop=stop;
        public Task<LabelJobResult> Task=null!;
    }
    private sealed class JobProgress(Action<LabelJobResult> report):IProgress<LabelJobResult>
    {public void Report(LabelJobResult value)=>report(value);}
    private readonly Dictionary<LabelTarget,LabelWorker> labelWorkers=new();
    private readonly CancellationTokenSource labelShutdown=new();
    private IWorkflowClock workflowClock=null!;
    public event Action<LabelJobResult>? FbsLabelJobChanged;
    public FbsLabelJob? GetFbsLabelJob(string jobId)=>Db.GetLabelJob(jobId);
    public LabelJobResult? CurrentFbsLabelResult(LabelTarget target)
    {var job=Db.ActiveLabelJobs(target.StoreId).SingleOrDefault(x=>x.Snapshot.Target==target);return job is null?null:ProjectLabelJob(job.Id);}
    public LabelJobResult ProjectLabelJob(string jobId,string? error=null)
    {
        var job=Db.GetLabelJob(jobId)??throw new InvalidOperationException("job_deleted");var units=Db.LabelJobUnits(jobId);
        return new(job.Id,job.Revision,job.Stage,units.Count(x=>x.Stage=="LabelsReady"),job.Snapshot.Units.Count,units,Db.LabelJobArtifacts(jobId),error);
    }
    private void NotifyLabelJob(LabelJobResult result)
    {
        foreach(var observer in FbsLabelJobChanged?.GetInvocationList()??Array.Empty<Delegate>())
            try{((Action<LabelJobResult>)observer)(result);}catch(ObjectDisposedException){}catch(InvalidOperationException){}
    }
    private Task<LabelJobResult> RunLabelWorker(LabelTarget target,Func<IProgress<LabelJobResult>,CancellationToken,Task<LabelJobResult>> run,IProgress<LabelJobResult>? progress,CancellationToken ct)
    {
        lock(labelWorkers){
            if(labelWorkers.TryGetValue(target,out var current)&&!current.Task.IsCompleted)return current.Task;
            var worker=new LabelWorker(target,CancellationTokenSource.CreateLinkedTokenSource(ct,labelShutdown.Token));labelWorkers[target]=worker;
            worker.Task=Task.Run(async()=>{
                try{var result=await run(new JobProgress(value=>{NotifyLabelJob(value);progress?.Report(value);}),worker.Stop.Token).ConfigureAwait(false);NotifyLabelJob(result);return result;}
                finally{lock(labelWorkers){if(labelWorkers.TryGetValue(target,out var active)&&ReferenceEquals(active,worker))labelWorkers.Remove(target);}worker.Stop.Dispose();}
            });return worker.Task;
        }
    }
    public void PauseFbsLabelJob(string jobId)
    {
        var job=Db.GetLabelJob(jobId);if(job is null)return;
        lock(labelWorkers){if(labelWorkers.TryGetValue(job.Snapshot.Target,out var worker))worker.Stop.Cancel();}
        var current=Db.GetLabelJob(jobId);if(current is not null){Db.TrySaveLabelJob(jobId,current.Version,FbsLabelJobStage.Paused,Db.LabelJobUnits(jobId));NotifyLabelJob(ProjectLabelJob(jobId,"operation_paused"));}
    }
    public void PauseStoreFbsLabelJobs(long storeId)
    {lock(labelWorkers){foreach(var worker in labelWorkers.Values.Where(x=>x.Target.StoreId==storeId))worker.Stop.Cancel();}foreach(var job in Db.ActiveLabelJobs(storeId))PauseFbsLabelJob(job.Id);}
    public void StopFbsLabelWorkers()=>labelShutdown.Cancel();
    public Task<UnitWorkflowResult> ScanJobKizAsync(string jobId,FbsUnitKey unit,string raw,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var job=Db.GetLabelJob(jobId)??throw new InvalidOperationException("job_deleted");var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,Db.LabelJobProfile(jobId));RequireWorkflowAuthorization(context);
        var demand=job.Snapshot.Units.SingleOrDefault(x=>x.Unit==unit&&x.RequiresKiz)??throw new InvalidOperationException("scan_unit_outside_job");var profile=context.Profile??throw new InvalidOperationException("explicit_profile_required");
        raw=KizCodeIdentity.Canonical(raw);if(KizCodeIdentity.Gtin(raw)!=demand.Gtin)throw new InvalidOperationException("scan_gtin_mismatch");var held=Db.BoundScopedKiz(context);var hash=Db.CodeProtector.Identity(raw);
        if(held.TryGetValue(unit,out var existing)&&Db.CodeProtector.Identity(existing)!=hash)throw new InvalidOperationException("scan_binding_mismatch");
        Db.ImportScopedKiz(new(profile.StoreId,profile.OwnerInn,profile.Environment),raw,"Scan");RequireWorkflowAuthorization(context);Db.BindAssignedKiz(context,new Dictionary<FbsUnitKey,string>{{unit,raw}});
        var result=new UnitWorkflowResult(unit,"AwaitingLegalState",hash,"legal_check_required");var current=Db.GetLabelJob(jobId)!;Db.TrySaveLabelJob(jobId,current.Version,current.Stage,Db.LabelJobUnits(jobId).Select(x=>x.Unit==unit?result:x).ToArray());NotifyLabelJob(ProjectLabelJob(jobId));return Task.FromResult(result);
    }
    public async Task<IReadOnlyList<LabelJobResult>> ResumePendingFbsJobsAsync(CancellationToken ct)
    {
        var results=new List<LabelJobResult>();foreach(var job in Db.ActiveLabelJobs().Where(x=>x.Stage is not(FbsLabelJobStage.LabelsReady or FbsLabelJobStage.SnapshotChanged or FbsLabelJobStage.Cancelled))){
            ct.ThrowIfCancellationRequested();results.Add(await RunLabelWorker(job.Snapshot.Target,(progress,token)=>LabelJobs.ResumeAsync(job.Id,progress,token,WorkflowRunMode.RecoveryOnly),null,ct).ConfigureAwait(false));
        }return results;
    }
    public async Task<LabelJobResult> ConfirmJobPhysicalMarksAsync(string jobId,IReadOnlyList<PhysicalMarkEvidence> units,CancellationToken ct)
    {
        var job=Db.GetLabelJob(jobId)??throw new InvalidOperationException("job_deleted");var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,Db.LabelJobProfile(jobId));RequireWorkflowAuthorization(context);var held=Db.BoundScopedKiz(context);
        if(units.Count==0||units.Select(x=>x.Unit).Distinct().Count()!=units.Count||units.Any(x=>!held.TryGetValue(x.Unit,out var raw)||Db.CodeProtector.Identity(raw)!=x.CodeHash))throw new InvalidOperationException("physical_scan_mismatch");
        foreach(var evidence in units){ct.ThrowIfCancellationRequested();RequireWorkflowAuthorization(context);Db.ConfirmPhysicalMark(evidence with{ConfirmedAt=workflowClock.UtcNow});}
        var result=await LabelJobs.ResumeAsync(jobId,null,ct,WorkflowRunMode.RecoveryOnly).ConfigureAwait(false);NotifyLabelJob(result);return result;
    }
    public async Task<IReadOnlyList<LabelArtifact>> ExportJobDataMatrixAsync(string jobId,CancellationToken ct)
    {
        var job=Db.GetLabelJob(jobId)??throw new InvalidOperationException("job_deleted");var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,Db.LabelJobProfile(jobId));RequireWorkflowAuthorization(context);var held=Db.BoundScopedKiz(context);if(held.Count==0)throw new InvalidOperationException("held_codes_required");
        var folder=Path.Combine(WbPrintBundleService.HistoryDirectory,Guid.ParseExact(job.Id,"N").ToString("N"),"revision-"+job.Revision,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var rows=held.Select(x=>{var unit=job.Snapshot.Units.Single(d=>d.Unit==x.Key);return new WbPrintOrder(unit.Unit.OrderId,unit.Sku,unit.Sku,"","","",unit.Gtin,1,true,new[]{x.Value},new(false,"Tem chuẩn bị KIZ"));}).ToArray();
        var path=await Task.Run(()=>MarketplaceKizPdfService.Write(job.Snapshot.Target.Marketplace.ToString(),rows,folder,ct),ct).ConfigureAwait(false);RequireWorkflowAuthorization(context);
        var artifact=new LabelArtifact(Guid.NewGuid().ToString("N"),jobId,job.Revision,"DataMatrix",path,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,ct))),held.Keys.ToArray(),false);RequireWorkflowAuthorization(context);
        if(!Db.SaveLabelJobArtifacts(context,Db.LabelJobArtifacts(jobId).Where(x=>x.Kind!="DataMatrix").Append(artifact).ToArray()))throw new InvalidOperationException("authorization_changed");NotifyLabelJob(ProjectLabelJob(jobId));return new[]{artifact};
    }
}
