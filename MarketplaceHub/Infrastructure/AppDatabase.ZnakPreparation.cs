using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Infrastructure;

public sealed partial class AppDatabase
{
    private static void EnsureZnakPreparationTable(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=@"CREATE TABLE IF NOT EXISTS znak_registration_preparation(
store_id INTEGER NOT NULL,sku TEXT NOT NULL,gtin TEXT NOT NULL,stage TEXT NOT NULL,detail TEXT NOT NULL,updated_at TEXT NOT NULL,
PRIMARY KEY(store_id,sku));";cmd.ExecuteNonQuery();
    }
    public void SaveZnakRegistrationPreparation(long storeId,string sku,string gtin,string stage,string detail)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"INSERT INTO znak_registration_preparation VALUES($s,$sku,$g,$stage,$d,$at)
ON CONFLICT(store_id,sku) DO UPDATE SET gtin=$g,stage=$stage,detail=$d,updated_at=$at";
        cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$sku",sku);cmd.Parameters.AddWithValue("$g",gtin);
        cmd.Parameters.AddWithValue("$stage",stage);cmd.Parameters.AddWithValue("$d",detail);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<ZnakPipelineRow> ZnakRegistrationPreparations(long storeId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT sku,gtin,stage,detail,updated_at FROM znak_registration_preparation WHERE store_id=$s ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$s",storeId);using var r=cmd.ExecuteReader();var rows=new List<ZnakPipelineRow>();
        while(r.Read())rows.Add(new(0,storeId,r.GetString(0),r.GetString(1),r.GetString(2),"",r.GetString(3),DateTimeOffset.Parse(r.GetString(4))));return rows;
    }
    // Compare-and-set in one SQLite statement also protects two independent application instances.
    public bool TryBeginSuzPurchase(long storeId,string sku,string gtin,int quantity,DateTimeOffset? expectedUpdatedAt)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"INSERT INTO znak_pipeline(store_id,sku,gtin,stage,external_order_id,detail,updated_at)
VALUES($s,$sku,$g,'BUYING','',$d,$at)
ON CONFLICT(store_id,sku) DO UPDATE SET gtin=$g,stage='BUYING',external_order_id='',detail=$d,updated_at=$at
WHERE znak_pipeline.updated_at=$expected AND (znak_pipeline.stage='CODES_DOWNLOADED' OR (znak_pipeline.external_order_id='' AND znak_pipeline.stage NOT IN('BUYING','CREATE_AMBIGUOUS')))";
        cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$sku",sku);cmd.Parameters.AddWithValue("$g",gtin);
        cmd.Parameters.AddWithValue("$expected",expectedUpdatedAt?.ToString("O")??"");
        cmd.Parameters.AddWithValue("$d",$"Mua {quantity} KIZ");cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));return cmd.ExecuteNonQuery()==1;
    }
}
