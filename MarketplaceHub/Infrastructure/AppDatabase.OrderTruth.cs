using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Infrastructure;

public sealed partial class AppDatabase
{
    private static void InitializeOrderTruthTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS order_remote_states(
 store_id INTEGER NOT NULL,
 marketplace TEXT NOT NULL,
 external_order_id TEXT NOT NULL,
 supplier_status TEXT NOT NULL DEFAULT '',
 marketplace_status TEXT NOT NULL DEFAULT '',
 is_complete INTEGER NOT NULL DEFAULT 0,
 observed_at TEXT NOT NULL,
 PRIMARY KEY(store_id,marketplace,external_order_id)
);
CREATE INDEX IF NOT EXISTS ix_order_remote_states_store_observed
ON order_remote_states(store_id,marketplace,observed_at DESC);";
        cmd.ExecuteNonQuery();
    }

    public void UpsertOrderRemoteStates(
        long storeId,
        Marketplace marketplace,
        IReadOnlyDictionary<string, OrderRemoteState> states)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        foreach (var pair in states)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO order_remote_states(
store_id,marketplace,external_order_id,supplier_status,marketplace_status,is_complete,observed_at)
VALUES($s,$m,$o,$supplier,$platform,$complete,$at)
ON CONFLICT(store_id,marketplace,external_order_id) DO UPDATE SET
 supplier_status=$supplier,marketplace_status=$platform,is_complete=$complete,observed_at=$at";
            cmd.Parameters.AddWithValue("$s", storeId);
            cmd.Parameters.AddWithValue("$m", marketplace.ToString());
            cmd.Parameters.AddWithValue("$o", pair.Key);
            cmd.Parameters.AddWithValue("$supplier", pair.Value.SupplierStatus);
            cmd.Parameters.AddWithValue("$platform", pair.Value.MarketplaceStatus);
            cmd.Parameters.AddWithValue("$complete", pair.Value.Complete ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", pair.Value.ObservedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyDictionary<string, OrderRemoteState> OrderRemoteStates(long storeId)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT external_order_id,supplier_status,marketplace_status,is_complete,observed_at
FROM order_remote_states WHERE store_id=$s";
        cmd.Parameters.AddWithValue("$s", storeId);
        var result = new Dictionary<string, OrderRemoteState>(StringComparer.Ordinal);
        using var row = cmd.ExecuteReader();
        while (row.Read())
            result[row.GetString(0)] = new(row.GetString(1), row.GetString(2), row.GetInt32(3) != 0, DateTimeOffset.Parse(row.GetString(4)));
        return result;
    }

    public void MarkOrderRemoteState(
        long storeId,
        Marketplace marketplace,
        string externalOrderId,
        string supplierStatus,
        string marketplaceStatus,
        bool complete = true,
        DateTimeOffset? observedAt = null) =>
        UpsertOrderRemoteStates(storeId, marketplace,
            new Dictionary<string, OrderRemoteState>(StringComparer.Ordinal)
            {
                [externalOrderId] = new(supplierStatus, marketplaceStatus, complete, observedAt ?? DateTimeOffset.UtcNow)
            });
}
