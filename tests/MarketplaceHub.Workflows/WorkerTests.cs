using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.Services.Suz;
using MarketplaceHub.TestSupport;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class WorkerTests
{
    static AppServices App(CoordinatorFixture f){var app=new AppServices(f.Db,new MarketplaceGateway(),WorkflowTestSupport.SignedLicense(()=>f.Clock.UtcNow),new(f.Suz,f.Legal,f.Clock));typeof(AppServices).GetProperty("LabelJobs")!.SetValue(app,f.Coordinator);return app;}
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("scan_binds_selected_unit_without_legal_or_physical_shortcut",async()=>{
            using var f=CoordinatorFixture.Create();var app=App(f);var method=typeof(AppServices).GetMethod("ScanJobKizAsync");Expect(method is not null,"Workflow has no exact-unit scan import");var selected=f.Context.Snapshot.Units.Last().Unit;var other=f.Context.Snapshot.Units.First().Unit;
            await (Task<UnitWorkflowResult>)method!.Invoke(app,new object[]{f.Context.JobId,selected,"]d2"+WorkflowFixture.Raw,CancellationToken.None})!;var held=f.Db.BoundScopedKiz(f.Context);Expect(held.Count==1&&held.Single().Key==selected&&held.Single().Value==WorkflowFixture.Raw,"Scan allocated to a different unit or changed payload");Expect(!f.Db.PhysicalMarkMatches(selected,f.Db.CodeProtector.Identity(WorkflowFixture.Raw))&&f.Legal.Reads==0&&f.Suz.TotalCalls==0&&f.Adapter.MutationCalls==0,"Scan conferred readiness or issued a remote mutation");
            var rejected=false;try{await (Task<UnitWorkflowResult>)method.Invoke(app,new object[]{f.Context.JobId,other,WorkflowFixture.Raw,CancellationToken.None})!;}catch(InvalidOperationException){rejected=true;}catch(System.Reflection.TargetInvocationException ex)when(ex.InnerException is InvalidOperationException){rejected=true;}Expect(rejected&&f.Db.BoundScopedKiz(f.Context).Count==1,"One scanned code bound to two units");
        });
        await r.CheckAsync("pause_active_worker_blocks_next_mutation",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Adapter.BeforeRead=async()=>{entered.TrySetResult();await release.Task;};var app=App(f);var task=app.ExportFbsLabelsAsync(f.Context.Snapshot.Target);await entered.Task;app.PauseFbsLabelJob(f.Context.JobId);release.SetResult();var result=await task;
            Expect(f.Adapter.MutationCalls==0&&result.Stage==FbsLabelJobStage.Paused,"Paused worker still attached remote codes");
        });
        await r.CheckAsync("pause_keeps_remote_id_and_reservation",()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var request=new PurchaseIntentRequest(f.Context.JobId,1,f.Context.Profile!,WorkflowFixture.Gtin,2,"");request=request with{PayloadHash=SuzHttpClient.PayloadHash(request)};var intent=f.Db.GetOrCreatePurchaseIntent(request);f.Db.SavePurchaseOutcome(intent.Id,PurchaseStage.Polling,"SUZ-PAUSED",null,null);var before=f.Db.BoundScopedKiz(f.Context);var app=App(f);app.PauseFbsLabelJob(f.Context.JobId);
            Expect(f.Db.GetPurchaseIntent(intent.Id)!.RemoteOrderId=="SUZ-PAUSED"&&f.Db.BoundScopedKiz(f.Context).OrderBy(x=>x.Key.OrderId).SequenceEqual(before.OrderBy(x=>x.Key.OrderId)),"Pause released paid evidence or unit bindings");Expect(app.GetFbsLabelJob(f.Context.JobId)!.Stage==FbsLabelJobStage.Paused,"Pause was not persistent");return Task.CompletedTask;
        });
        await r.CheckAsync("physical_confirmation_requires_exact_selected_units",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Legal.Status="APPLIED";var held=f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units);var selected=held.First();var other=held.Last();var app=App(f);
            var result=await app.ConfirmJobPhysicalMarksAsync(f.Context.JobId,new[]{new PhysicalMarkEvidence(selected.Key,f.Db.CodeProtector.Identity(selected.Value),f.Clock.UtcNow)},default);
            Expect(f.Db.PhysicalMarkMatches(selected.Key,f.Db.CodeProtector.Identity(selected.Value))&&!f.Db.PhysicalMarkMatches(other.Key,f.Db.CodeProtector.Identity(other.Value))&&result.Stage==FbsLabelJobStage.AwaitingLegalState&&f.Adapter.MutationCalls==0,"Physical acknowledgement expanded selection or opened legal gate");
            var rejected=false;try{await app.ConfirmJobPhysicalMarksAsync(f.Context.JobId,new[]{new PhysicalMarkEvidence(other.Key,"wrong-code",f.Clock.UtcNow)},default);}catch(InvalidOperationException){rejected=true;}Expect(rejected,"Wrong scan matched selected unit");
        });
        await r.CheckAsync("data_matrix_is_not_an_official_shipping_label",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units);var app=App(f);var matrix=await app.ExportJobDataMatrixAsync(f.Context.JobId,default);
            Expect(matrix.Count>0&&matrix.All(x=>x.Kind=="DataMatrix"&&!x.OfficialMarketplaceLabel&&File.Exists(x.FilePath)),"Preparation codes were presented as official labels");
            f.Physical();f.Adapter.BarcodeOnly=true;var result=await app.ExportFbsLabelsAsync(f.Context.Snapshot.Target);Expect(result.Stage!=FbsLabelJobStage.LabelsReady&&result.VerifiedUnits==0,"Data Matrix manifest falsely covered shipping labels");Expect(f.Suz.TotalCalls==0,"Printing held KIZ acquired new codes");
        });
        await r.CheckAsync("reprint_does_not_refetch_preparation_artifacts_as_shipping_labels",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units);var app=App(f);await app.ExportJobDataMatrixAsync(f.Context.JobId,default);var result=await app.LabelJobs.ReprintAsync(f.Context.JobId,1,default);
            Expect(f.Adapter.Downloads==0&&result.Artifacts.All(x=>!x.OfficialMarketplaceLabel)&&f.Suz.TotalCalls==0,"Reprint converted preparation codes into shipping labels");
        });
        await r.CheckAsync("export_and_reprint_share_job",async()=>{
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();var app=App(f);var first=await app.ExportFbsLabelsAsync(f.Context.Snapshot.Target);var again=await app.ExportFbsLabelsAsync(f.Context.Snapshot.Target);var print=await app.LabelJobs.ReprintAsync(first.JobId,first.Revision,default);
            Expect(first.Stage==FbsLabelJobStage.LabelsReady&&first.JobId==again.JobId&&print.JobId==first.JobId&&f.Suz.TotalCalls==0,"Export/reprint replaced job or purchased codes");
        });
        await r.CheckAsync("startup_recovery_does_not_recover_paid_blocks",async()=>{
            using var f=CoordinatorFixture.Create();var request=new PurchaseIntentRequest(f.Context.JobId,1,f.Context.Profile!,WorkflowFixture.Gtin,2,"");request=request with{PayloadHash=SuzHttpClient.PayloadHash(request)};var i=f.Db.GetOrCreatePurchaseIntent(request);f.Db.SavePurchaseOutcome(i.Id,PurchaseStage.DownloadUnknown,"SUZ-OLD",null,null);i=f.Db.GetPurchaseIntent(i.Id)!;f.Suz.Blocks=new[]{FakeSuzClient.FullBlock(i) with{Codes=Array.Empty<string>()}};await App(f).ResumePendingFbsJobsAsync(default);
            Expect(f.Suz.RecoverCalls==0&&f.Suz.ReceiveCalls==0&&f.Suz.CreateCalls==0&&f.Db.PurchaseBlocks(i.Id).Any(x=>x.BlockId=="BLOCK-1"),"Startup used a stateful recovery request or lost known block identity");
        });
        await r.CheckAsync("startup_recovery_does_not_receive_or_attach",async()=>{
            using var f=CoordinatorFixture.Create();var request=new PurchaseIntentRequest(f.Context.JobId,1,f.Context.Profile!,WorkflowFixture.Gtin,2,"");request=request with{PayloadHash=SuzHttpClient.PayloadHash(request)};var i=f.Db.GetOrCreatePurchaseIntent(request);f.Db.SavePurchaseOutcome(i.Id,PurchaseStage.Polling,"SUZ-OLD",null,null);var results=await App(f).ResumePendingFbsJobsAsync(default);
            Expect(results.Count==1&&f.Suz.CreateCalls==0&&f.Suz.ReceiveCalls==0&&f.Adapter.MutationCalls==0&&f.Adapter.Downloads==0&&f.Db.GetPurchaseIntent(i.Id)!.RemoteOrderId=="SUZ-OLD","Startup recovery acquired codes or expanded workflow");
        });
    }
}
