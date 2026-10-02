using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services.Suz;
namespace MarketplaceHub.Workflows;
public sealed class PurchaseFixture : IDisposable
{
    public WorkflowFixture Data { get; } = WorkflowFixture.Create();
    public AppDatabase Db => Data.Db;
    public FakeSuzClient Suz { get; } = new();
    public FakeWorkflowClock Clock { get; } = new();
    public SuzProfile Profile { get; }
    public PurchaseIntentRequest Request { get; }
    public KizPurchaseCoordinator Coordinator { get; }
    private PurchaseFixture()
    {
        Profile = Data.Profile with { Version = 2, AutoPurchaseEnabled = true, ContractEnabled = true }; Db.SaveSuzProfile(Profile);
        var request = Data.Request() with { Profile = Profile }; Request = request with { PayloadHash = SuzHttpClient.PayloadHash(request) };
        Coordinator = new(Db, Suz, Clock);
    }
    public static PurchaseFixture Create() => new();
    public KizPurchaseCoordinator Reopen() => new(Data.Reopen(), Suz, Clock);
    public void Dispose() => Data.Dispose();
}
