using MarketplaceHub.Core;
using MarketplaceHub.Services.Fbs;
using MarketplaceHub.Services.Suz;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class AllocationTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("stock_exact_scope_and_gtin", () => {
            using var f = WorkflowFixture.Create(); var j = f.Db.GetOrCreateLabelJob(f.Snapshot); var context = new FbsWorkflowContext(j.Id, 1, j.Snapshot, f.Profile);
            var scopes = new[] { new KizScope(f.Store.Id, "9999999999", "Production"), new KizScope(f.Store.Id, f.Profile.OwnerInn, "Sandbox"), new KizScope(0, f.Profile.OwnerInn, "Production") };
            for (var i=0;i<scopes.Length;i++) { try { f.Db.ImportScopedKiz(scopes[i], WorkflowFixture.Raw.Replace("SERIAL0000001", "OTHER0000000"+i), "Scan"); } catch(InvalidOperationException) { } }
            Expect(f.Db.ReserveScopedKiz(context, f.Snapshot.Units).Count==0, "Allocation crossed owner/environment/store");
            f.Db.ImportScopedKiz(new(f.Store.Id,f.Profile.OwnerInn,f.Profile.Environment),WorkflowFixture.Raw,"Scan");
            var held=f.Db.ReserveScopedKiz(context,f.Snapshot.Units); Expect(held.Count==1 && held.Values.Single()==WorkflowFixture.Raw,"Correct scoped stock unavailable");
            return Task.CompletedTask;
        });
        await r.CheckAsync("binding_is_atomic_stable_and_prefix_alias_is_single_stock", async () => {
            using var f=WorkflowFixture.Create(); var job=f.Db.GetOrCreateLabelJob(f.Snapshot); var c=new FbsWorkflowContext(job.Id,1,job.Snapshot,f.Profile);
            f.Db.ImportScopedKiz(new(f.Store.Id,f.Profile.OwnerInn,f.Profile.Environment),WorkflowFixture.Raw,"Scan");
            f.Db.ImportScopedKiz(new(f.Store.Id,f.Profile.OwnerInn,f.Profile.Environment),"]d2"+WorkflowFixture.Raw,"Scan");
            var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>f.Db.ReserveScopedKiz(c,f.Snapshot.Units))));
            Expect(results.All(x=>x.Count==1 && x.Keys.Single()==results[0].Keys.Single()),"Concurrent allocation double spent a code");
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM kiz_unit_bindings"))==1,"Alias created two bindings");
        });
        await r.CheckAsync("same_cis_different_tail_is_quarantined", () => {
            using var f=WorkflowFixture.Create(); var j=f.Db.GetOrCreateLabelJob(f.Snapshot);var c=new FbsWorkflowContext(j.Id,1,j.Snapshot,f.Profile);var s=new KizScope(f.Store.Id,f.Profile.OwnerInn,f.Profile.Environment);
            f.Db.ImportScopedKiz(s,WorkflowFixture.Raw,"Scan");f.Db.ImportScopedKiz(s,WorkflowFixture.OtherTail,"Scan");
            Expect(f.Db.ReserveScopedKiz(c,f.Snapshot.Units).Count==0,"Conflicting crypto tails allocated");return Task.CompletedTask;
        });
        await r.CheckAsync("legal_and_physical_are_independent", async () => {
            using var f=WorkflowFixture.Create();var j=f.Db.GetOrCreateLabelJob(f.Snapshot);var c=new FbsWorkflowContext(j.Id,1,j.Snapshot,f.Profile);var clock=new FakeWorkflowClock();
            f.Db.ImportScopedKiz(new(f.Store.Id,f.Profile.OwnerInn,f.Profile.Environment),WorkflowFixture.Raw,"Scan");var held=f.Db.ReserveScopedKiz(c,f.Snapshot.Units);var key=held.Keys.Single();
            f.Db.ConfirmPhysicalMark(new(key,f.Db.CodeProtector.Identity(WorkflowFixture.Raw),clock.UtcNow));
            var reader=new FixtureLegalReader(f.Db.CodeProtector,clock){Status="APPLIED"};var service=new KizEligibilityService(f.Db,reader,clock);
            var results=await service.VerifyAsync(c,held,default);Expect(results.Single(x=>x.Unit==key).Stage=="AwaitingLegalState","Physical checkbox bypassed legal proof");
            reader.Status="INTRODUCED";results=await service.VerifyAsync(c,held,default);Expect(results.Single(x=>x.Unit==key).Stage=="Ready","Exact INTRODUCED + physical did not open gate");
        });
        foreach(var state in new[]{"EMITTED","APPLIED","RETIRED","UNKNOWN"}) await r.CheckAsync("legal_rejects_"+state, async () => {
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Legal.Status=state;var results=await f.Eligibility.VerifyAsync(f.Context,f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units),default);
            Expect(results.All(x=>x.Stage=="AwaitingLegalState"),"Unsupported legal state authorized shipping");
        });
        foreach(var condition in new[]{"owner","environment","gtin","package","special","missing-owner","unavailable"}) await r.CheckAsync("legal_rejects_"+condition, async () => {
            using var f=CoordinatorFixture.Create();f.Stock();f.Physical();f.Legal.Condition=condition;var results=await f.Eligibility.VerifyAsync(f.Context,f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units),default);
            Expect(results.All(x=>x.Stage=="AwaitingLegalState"),"Incomplete/wrong legal scope authorized shipping");
        });
        await r.CheckAsync("physical_mark_rejects_wrong_code_or_unit", () => {
            using var f=CoordinatorFixture.Create();f.Stock();var held=f.Db.ReserveScopedKiz(f.Context,f.Context.Snapshot.Units);
            Throws<InvalidOperationException>(()=>f.Db.ConfirmPhysicalMark(new(held.Keys.First(),"wrong",f.Clock.UtcNow)));
            Throws<InvalidOperationException>(()=>f.Db.ConfirmPhysicalMark(new(held.Keys.First() with{OrderId="foreign"},f.Db.CodeProtector.Identity(held.Values.First()),f.Clock.UtcNow)));return Task.CompletedTask;
        });
    }
}
public sealed class FixtureLegalReader(IKizCodeProtector protector,IWorkflowClock clock) : IKizLegalReader
{
    public string Status="INTRODUCED",Condition="";public int Reads;
    public Task<IReadOnlyList<KizLegalProof>> ReadAsync(SuzProfile profile,IReadOnlyList<string> codes,CancellationToken ct)
    {
        Reads++;if(Condition=="unavailable")throw new HttpRequestException("fixture offline");
        IReadOnlyList<KizLegalProof> proof=codes.Select(raw=>new KizLegalProof(protector.Identity(raw),Condition=="gtin"?"00000000000000":KizCodeIdentity.Gtin(raw),Condition=="owner"?"9999999999":Condition=="missing-owner"?"":profile.OwnerInn,Condition=="environment"?"Sandbox":profile.Environment,Status,Condition=="special"?"BLOCKED":"EMPTY",Condition=="package"?"GROUP":"UNIT",clock.UtcNow,"TrueApi")).ToArray();
        return Task.FromResult(proof);
    }
}
