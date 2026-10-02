using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Workflows;
public sealed class WorkflowFixture : IDisposable
{
    public const string Gtin = "04601234567893";
    public const string Raw = "010460123456789321SERIAL0000001\u001d91ABCD\u001d92TAIL-A";
    public const string OtherTail = "010460123456789321SERIAL0000001\u001d91ABCD\u001d92TAIL-B";
    public AppDatabase Db { get; }
    public StoreProfile Store { get; }
    public LabelJobSnapshot Snapshot { get; }
    public SuzProfile Profile { get; }
    public string DbPath => Db.DbPath;
    private WorkflowFixture()
    {
        Db = new(Path.Combine(Path.GetTempPath(), "MarketplaceHub-workflow-" + Guid.NewGuid().ToString("N"), "test.db"));
        Store = Db.SaveStore(new(0, Marketplace.Wildberries, "workflow fixture", "", "", "", "", "", true));
        Profile = new("fixture-profile", Store.Id, 1, "7701234567", "Production", "lp", "PRODUCTION", "UNIT", 10, Db.ZnakCredentialVersion(), false, false,
            new Uri("https://suzgrid.crpt.ru"), new Uri("https://markirovka.crpt.ru"));
        Db.SaveSuzProfile(Profile);
        Snapshot = new(new(Store.Id, Store.Marketplace, LabelTargetKind.WbSupply, "SUPPLY-1"), Db.StoreGeneration(Store.Id),
            new[] { new FbsUnitDemand(new(Store.Id, Store.Marketplace, "101", "A", 0), "A", Gtin, 1, true),
                    new FbsUnitDemand(new(Store.Id, Store.Marketplace, "102", "A", 0), "A", Gtin, 1, true) }, "items-v1", "requirements-v1");
    }
    public static WorkflowFixture Create() => new();
    public AppDatabase Reopen() => new(DbPath);
    public PurchaseIntentRequest Request(int quantity = 2) => new(Db.GetOrCreateLabelJob(Snapshot).Id, 1, Profile, Gtin, quantity, "PAYLOAD-1");
    public object? Sql(string sql)
    {
        using var c = new SqliteConnection("Data Source=" + DbPath); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(DbPath)!, true); } catch { }
    }
}
