using MarketplaceHub.Core;
using MarketplaceHub.Services.Fbs;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class CoordinatorTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("complete_stock_means_no_purchase",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var result=await f.Run();
            Expect(result.Stage==FbsLabelJobStage.LabelsReady&&result.Artifacts.Count==2&&f.Suz.CreateCalls==0,"Existing stock did not produce labels without purchase");
            Expect(f.Legal.Reads>=2,"Legal proof not freshly checked before mutation and label");
        });
        await r.CheckAsync("legal_only_awaits_physical",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();var result=await f.Run();Expect(result.Stage==FbsLabelJobStage.AwaitingPhysicalMark&&f.Adapter.MutationCalls==0,"Legal proof implied physical mark");
        });
        await r.CheckAsync("physical_only_awaits_legal",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Legal.Status="APPLIED";var result=await f.Run();Expect(result.Stage==FbsLabelJobStage.AwaitingLegalState&&f.Adapter.MutationCalls==0,"Physical mark implied legal proof");
        });
        await r.CheckAsync("ambiguous_attach_keeps_same_codes",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.LoseAttach=true;var first=await f.Run();var code=f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units).Values.First();
            f.Adapter.LoseAttach=false;var resumed=await f.Coordinator.ResumeAsync(first.JobId,null,default);Expect(resumed.Stage==FbsLabelJobStage.NeedsReconciliation&&f.Adapter.MutationCalls==1&&f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units).Values.First()==code,"Lost response replaced/re-attached code");
            foreach(var pair in f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units))f.Adapter.Assigned[pair.Key]=pair.Value;
            var recovered=await f.Coordinator.ResumeAsync(first.JobId,null,default);Expect(recovered.Stage==FbsLabelJobStage.LabelsReady&&f.Adapter.MutationCalls==1,"Readback did not recover without mutation");
        });
        foreach(var change in new[]{"quantity","mapping","requirement"})await r.CheckAsync("remote_snapshot_change_"+change,async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var s=f.Adapter.Snapshot;
            f.Adapter.Snapshot=change=="quantity"?s with{Units=s.Units.Take(1).ToArray()}:change=="mapping"?s with{Units=s.Units.Select(x=>x with{MappingVersion=2}).ToArray()}:s with{RequirementFingerprint="new"};
            var result=await f.Run();Expect(result.Stage==FbsLabelJobStage.SnapshotChanged&&f.Adapter.MutationCalls==0,"Stale snapshot mutated marketplace");
        });
        await r.CheckAsync("item_reorder_keeps_revision",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.Snapshot=f.Adapter.Snapshot with{Units=f.Adapter.Snapshot.Units.Reverse().ToArray()};var result=await f.Run();Expect(result.Revision==1&&result.Stage==FbsLabelJobStage.LabelsReady,"Order-only change blocked job");
        });
        await r.CheckAsync("label_partial_and_reprint_preserve_manifest",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.OneLabelOnly=true;var partial=await f.Run();Expect(partial.Stage==FbsLabelJobStage.Partial&&partial.Artifacts.Count==1,"Partial label response falsely completed");
            var reprint=await f.Coordinator.ReprintAsync(partial.JobId,1,default);Expect(reprint.Artifacts.Count==1&&f.Suz.CreateCalls==0&&f.Adapter.MutationCalls==1,"Reprint bought or mutated");
        });
        await r.CheckAsync("atomic_posting_missing_unit_has_no_label",async()=>{
            using var f=CoordinatorFixture.Create();var s=f.Context.Snapshot;var units=s.Units.Select(x=>x with{Unit=x.Unit with{OrderId="101"}}).ToArray();
            // Use distinct item IDs for a two-item posting.
            units[1]=units[1] with{Unit=units[1].Unit with{ItemId="B"}};var changed=s with{Target=s.Target with{TargetId="SUPPLY-2"},Units=units};
            f.Adapter.Snapshot=changed;f.Stock(1);var job=f.Db.GetOrCreateLabelJob(changed);var c=new FbsWorkflowContext(job.Id,1,changed,f.Data.Profile);foreach(var pair in f.Db.ReserveScopedKiz(c,units))f.Db.ConfirmPhysicalMark(new(pair.Key,f.Db.CodeProtector.Identity(pair.Value),f.Clock.UtcNow));
            var result=await f.Coordinator.StartOrResumeAsync(changed.Target,null,default);Expect(result.Artifacts.Count==0&&f.Adapter.MutationCalls==0,"Subset of multi-unit posting got label/packing");
        });
        await r.CheckAsync("barcode_metadata_is_not_official_label",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.BarcodeOnly=true;var result=await f.Run();Expect(result.Stage!=FbsLabelJobStage.LabelsReady&&result.Artifacts.Count==0,"Barcode-only result became official artifact");
        });
        foreach(var corrupt in new[]{false,true})await r.CheckAsync("reprint_refetch_"+(corrupt?"corrupt":"missing"),async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var result=await f.Run();var artifact=result.Artifacts.First();if(corrupt)await File.WriteAllTextAsync(artifact.FilePath,"corrupt");else File.Delete(artifact.FilePath);
            var calls=f.Adapter.MutationCalls;var reprint=await f.Coordinator.ReprintAsync(result.JobId,1,default);Expect(reprint.Artifacts.Count==2&&File.Exists(reprint.Artifacts.First().FilePath)&&f.Suz.CreateCalls==0&&f.Adapter.MutationCalls==calls,"Reprint did not restore exact label without acquisition");
        });
        foreach(var tampered in new[]{false,true})await r.CheckAsync("license_blocks_"+(tampered?"tampered":"expired"),async()=>{
            using var f=CoordinatorFixture.Create(expired:!tampered,tampered:tampered);f.Stock();f.Physical();var result=await f.Run();Expect(result.Stage!=FbsLabelJobStage.LabelsReady&&f.Adapter.MutationCalls==0&&f.Suz.TotalCalls==0,"Invalid signed license mutated");
        });
        await r.CheckAsync("store_deleted_during_read_blocks_later_phases",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.BeforeRead=()=>{f.Db.DeleteStore(f.Data.Store.Id);return Task.CompletedTask;};var result=await f.Run();Expect(f.Adapter.MutationCalls==0&&result.Stage!=FbsLabelJobStage.LabelsReady&&Convert.ToInt64(f.Data.Sql("SELECT COUNT(*) FROM fbs_label_jobs"))==0,"Deleted store recreated by late callback");
        });
        await r.CheckAsync("recovery_only_never_attaches_or_creates_labels",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var result=await f.Coordinator.ResumeAsync(f.Context.JobId,null,default,WorkflowRunMode.RecoveryOnly);Expect(f.Adapter.MutationCalls==0&&f.Adapter.Downloads==0&&f.Suz.CreateCalls==0,"Background recovery mutated remote state");
        });
        await r.CheckAsync("revision_requires_confirmation_and_no_unknown_mutation",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.LoseAttach=true;var result=await f.Run();var revision=await f.Coordinator.CreateRevisionAsync(result.JobId,true,default);Expect(revision.Revision==1,"Confirmed revision ignored unknown mutation");
            var rejected=await f.Coordinator.CreateRevisionAsync(result.JobId,false,default);Expect(rejected.Revision==1,"Unconfirmed revision created");
        });
        await r.CheckAsync("confirmed_revision_preserves_previous_artifacts_and_shared_binding",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var completed=await f.Run();var oldPaths=completed.Artifacts.Select(x=>x.FilePath).ToArray();
            f.Adapter.Snapshot=f.Adapter.Snapshot with{RequirementFingerprint="requirements-v2"};var next=await f.Coordinator.CreateRevisionAsync(completed.JobId,true,default);
            Expect(next.Revision==2&&next.JobId!=completed.JobId&&oldPaths.All(File.Exists),"Revision discarded history");
            var old=f.Db.BoundScopedKiz(f.Context);var newJob=f.Db.GetLabelJob(next.JobId)!;var c=new FbsWorkflowContext(newJob.Id,2,newJob.Snapshot,f.Data.Profile);var held=f.Db.ReserveScopedKiz(c,c.Snapshot.Units);Expect(held.Count==2&&held.Values.ToHashSet().SetEquals(old.Values),"Revision replaced shared unit bindings");
        });
        await r.CheckAsync("legal_proof_loss_after_attach_blocks_label_download",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Adapter.BeforeMutation=()=>{f.Legal.Condition="unavailable";return Task.CompletedTask;};var result=await f.Run();
            Expect(result.Stage==FbsLabelJobStage.AwaitingLegalState&&f.Adapter.Downloads==0&&result.Artifacts.Count==0,"Stale legal proof authorized label download");
        });
    }
}
