using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure;

public sealed record FboPreparationDraft(string Id,IReadOnlyList<FboPreparationRow> Rows);

public sealed partial class AppDatabase
{
    private static void EnsureFboPreparationTables(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=@"
CREATE TABLE IF NOT EXISTS fbo_preparation_jobs(
 id TEXT PRIMARY KEY,store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,
 rows_json TEXT NOT NULL,state TEXT NOT NULL,bundle_path TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ix_fbo_pending_store ON fbo_preparation_jobs(store_id,marketplace) WHERE state='PENDING';";
        cmd.ExecuteNonQuery();
    }

    public FboPreparationDraft? PendingFboPreparation(StoreProfile store)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT id,rows_json FROM fbo_preparation_jobs WHERE store_id=$s AND marketplace=$m AND state='PENDING'";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());
        using var row=cmd.ExecuteReader();return row.Read()?new(row.GetString(0),JsonSerializer.Deserialize<FboPreparationRow[]>(row.GetString(1))!):null;
    }

    public FboPreparationDraft BeginFboPreparation(StoreProfile store,IReadOnlyList<FboPreparationRow> rows)
    {
        var pending=PendingFboPreparation(store);if(pending is not null)return pending;
        var id="FBO:"+Guid.NewGuid().ToString("N");
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO fbo_preparation_jobs(id,store_id,marketplace,rows_json,state,updated_at) VALUES($id,$s,$m,$json,'PENDING',$at)";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());
        cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(rows));cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        return new(id,rows);
    }

    public void CompleteFboPreparation(StoreProfile store,string id,string path)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE fbo_preparation_jobs SET state='PREPARED',bundle_path=$path,updated_at=$at WHERE id=$id AND store_id=$s AND marketplace=$m AND state='PENDING'";
        cmd.Parameters.AddWithValue("$path",path);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lượt chuẩn bị FBO đã thay đổi; mã đã giữ cần được đối soát.");
    }
}
