using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services;
using MarketplaceHub.Services.Fbs;
using MarketplaceHub.Services.Suz;
using MarketplaceHub.TestSupport;
using SkiaSharp;
namespace MarketplaceHub.Workflows;
public sealed class FakeFbsLabelAdapter : IFbsLabelAdapter
{
    public LabelJobSnapshot Snapshot;
    public Dictionary<FbsUnitKey,string> Assigned=new();
    public int MutationCalls,Downloads,Reads;
    public bool LoseAttach,OneLabelOnly,BarcodeOnly;
    public Func<Task>? BeforeMutation,BeforeRead;
    private readonly string folder;
    public FakeFbsLabelAdapter(LabelJobSnapshot snapshot,string folder){Snapshot=snapshot;this.folder=folder;}
    public async Task<LabelJobSnapshot> ReadSnapshotAsync(LabelTarget target,CancellationToken ct){Reads++;if(BeforeRead is not null)await BeforeRead();if(target!=Snapshot.Target)throw new Exception("Wrong exact target");return Snapshot;}
    public Task<IReadOnlyDictionary<FbsUnitKey,string>> ReadAssignedCodesAsync(FbsWorkflowContext c,CancellationToken ct)=>Task.FromResult<IReadOnlyDictionary<FbsUnitKey,string>>(new Dictionary<FbsUnitKey,string>(Assigned));
    public async Task<IReadOnlyList<UnitWorkflowResult>> EnsureMarkedAndPackedAsync(FbsWorkflowContext c,IReadOnlyDictionary<FbsUnitKey,string> codes,CancellationToken ct)
    {
        MutationCalls++;if(BeforeMutation is not null)await BeforeMutation();if(LoseAttach)throw new HttpRequestException("lost attach response");
        foreach(var pair in codes)Assigned[pair.Key]=pair.Value;
        return c.Snapshot.Units.Select(x=>new UnitWorkflowResult(x.Unit,"Verified",null,null)).ToArray();
    }
    public Task<IReadOnlyList<VerifiedLabel>> DownloadLabelsAsync(FbsWorkflowContext c,IReadOnlyList<FbsUnitKey> eligible,CancellationToken ct)
    {
        Downloads++;var result=new List<VerifiedLabel>();foreach(var group in c.Snapshot.Units.GroupBy(x=>x.Unit.OrderId)){
            if(group.Any(x=>!eligible.Contains(x.Unit)))continue;
            var path=Path.Combine(folder,"remote-"+group.Key+".pdf");
            using(var doc=SKDocument.CreatePdf(path)){var canvas=doc.BeginPage(120,160);canvas.Clear(SKColors.White);doc.EndPage();doc.Close();}
            result.Add(new(group.Key,new(true,"official fixture",BarcodeOnly?null:path),group.Select(x=>x.Unit).ToArray(),"fixture.labels.get","remote-task-"+group.Key));if(OneLabelOnly)break;
        }return Task.FromResult<IReadOnlyList<VerifiedLabel>>(result);
    }
}
public sealed class CoordinatorFixture : IDisposable
{
    public WorkflowFixture Data=WorkflowFixture.Create();public AppDatabase Db=>Data.Db;
    public FakeWorkflowClock Clock=new();public FakeSuzClient Suz=new();public FixtureLegalReader Legal;
    public FakeFbsLabelAdapter Adapter;public KizEligibilityService Eligibility;public FbsLabelJobCoordinator Coordinator;public FbsWorkflowContext Context;
    private CoordinatorFixture(bool expired,bool tampered){
        var job=Db.GetOrCreateLabelJob(Data.Snapshot);Context=new(job.Id,1,job.Snapshot,Data.Profile);
        Legal=new(Db.CodeProtector,Clock);Eligibility=new(Db,Legal,Clock);Adapter=new(Data.Snapshot,Path.GetDirectoryName(Db.DbPath)!);
        Coordinator=new(Db,_=>Adapter,new(Db,Suz,Clock),Eligibility,WorkflowTestSupport.SignedLicense(()=>Clock.UtcNow,expired,tampered),new(Path.Combine(Path.GetDirectoryName(Db.DbPath)!,"artifacts")),Clock);
    }
    public static CoordinatorFixture Create(bool expired=false,bool tampered=false)=>new(expired,tampered);
    public void Stock(int quantity=2){for(var i=0;i<quantity;i++)Db.ImportScopedKiz(new(Data.Store.Id,Data.Profile.OwnerInn,Data.Profile.Environment),WorkflowFixture.Raw.Replace("SERIAL0000001","SERIAL000000"+(i+1)),"Scan");}
    public void Physical(){foreach(var pair in Db.ReserveScopedKiz(Context,Context.Snapshot.Units))Db.ConfirmPhysicalMark(new(pair.Key,Db.CodeProtector.Identity(pair.Value),Clock.UtcNow));}
    public Task<LabelJobResult> Run()=>Coordinator.StartOrResumeAsync(Context.Snapshot.Target,null,default);
    public void Dispose()=>Data.Dispose();
}
