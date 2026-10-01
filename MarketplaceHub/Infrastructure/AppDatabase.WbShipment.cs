using Microsoft.Data.Sqlite;
using MarketplaceHub.Core;

namespace MarketplaceHub.Infrastructure;

public sealed partial class AppDatabase
{
    private static void InitializeWbShipmentTables(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();
        cmd.CommandText=@"
CREATE TABLE IF NOT EXISTS wb_supply_orders(store_id INTEGER NOT NULL,order_id TEXT NOT NULL,supply_id TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(store_id,order_id));
CREATE INDEX IF NOT EXISTS ix_wb_supply_orders_supply ON wb_supply_orders(store_id,supply_id);
CREATE TABLE IF NOT EXISTS wb_kiz_reservations(code TEXT PRIMARY KEY,store_id INTEGER NOT NULL,order_id TEXT NOT NULL,gtin TEXT NOT NULL,status TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(store_id,order_id));";
        cmd.ExecuteNonQuery();
    }

    public void StoreWbSupplyMembership(long storeId,string supplyId,IEnumerable<string> ids)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        using(var clear=c.CreateCommand()) {
            clear.Transaction=tx;clear.CommandText="DELETE FROM wb_supply_orders WHERE store_id=$s AND supply_id=$supply";
            clear.Parameters.AddWithValue("$s",storeId);clear.Parameters.AddWithValue("$supply",supplyId);clear.ExecuteNonQuery();
        }
        foreach(var id in ids.Distinct()) {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=@"INSERT INTO wb_supply_orders(store_id,order_id,supply_id,updated_at) VALUES($s,$o,$p,$at)
ON CONFLICT(store_id,order_id) DO UPDATE SET supply_id=$p,updated_at=$at";
            cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$o",id);cmd.Parameters.AddWithValue("$p",supplyId);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public string? FindWbSupplyForOrder(long storeId,string orderId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT supply_id FROM wb_supply_orders WHERE store_id=$s AND order_id=$o";
        cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$o",orderId);return cmd.ExecuteScalar()?.ToString();
    }

    public string? WbReservedKizForOrder(long storeId,string orderId,string gtin)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT code,gtin FROM wb_kiz_reservations WHERE store_id=$s AND order_id=$o";
        cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$o",orderId);
        using var row=cmd.ExecuteReader();if(!row.Read())return null;
        if(row.GetString(1)!=gtin)throw new InvalidOperationException($"{orderId}: KIZ đã giữ cho GTIN khác. Đối soát trước khi đổi biến thể.");
        return row.GetString(0);
    }

    public string? ReserveWbKiz(long storeId,string orderId,string gtin)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        using(var read=c.CreateCommand()) {
            read.Transaction=tx;read.CommandText="SELECT code,gtin FROM wb_kiz_reservations WHERE store_id=$s AND order_id=$o";
            read.Parameters.AddWithValue("$s",storeId);read.Parameters.AddWithValue("$o",orderId);
            using var row=read.ExecuteReader();if(row.Read()) {
                if(row.GetString(1)!=gtin)throw new InvalidOperationException("Đơn đã có KIZ được giữ cho GTIN khác.");
                return row.GetString(0);
            }
        }
        string? code;
        using(var select=c.CreateCommand()) {
            select.Transaction=tx;select.CommandText=@"SELECT code FROM kiz_pool WHERE gtin=$g AND status='AVAILABLE' AND code NOT IN(SELECT code FROM wb_kiz_reservations) ORDER BY updated_at LIMIT 1";
            select.Parameters.AddWithValue("$g",gtin);code=select.ExecuteScalar()?.ToString();
        }
        if(code is null)return null;
        using(var reserve=c.CreateCommand()) {
            reserve.Transaction=tx;reserve.CommandText="INSERT INTO wb_kiz_reservations(code,store_id,order_id,gtin,status,updated_at) VALUES($c,$s,$o,$g,'RESERVED',$at)";
            reserve.Parameters.AddWithValue("$c",code);reserve.Parameters.AddWithValue("$s",storeId);reserve.Parameters.AddWithValue("$o",orderId);reserve.Parameters.AddWithValue("$g",gtin);reserve.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));reserve.ExecuteNonQuery();
        }
        using(var update=c.CreateCommand()) {
            update.Transaction=tx;update.CommandText="UPDATE kiz_pool SET status='RESERVED',assigned_order=$o,updated_at=$at WHERE code=$c AND status='AVAILABLE'";
            update.Parameters.AddWithValue("$c",code);update.Parameters.AddWithValue("$o","WB:"+storeId+":"+orderId);update.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
            if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("KIZ đã được tác vụ khác sử dụng. Chưa gửi mã tới WB.");
        }
        tx.Commit();return code;
    }

    public void ConfirmWbKiz(long storeId,string orderId,string gtin,string code)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        ValidateWbKizOwnership(c,tx,storeId,orderId,gtin,code);
        using(var reserve=c.CreateCommand()) {
            reserve.Transaction=tx;reserve.CommandText=@"INSERT INTO wb_kiz_reservations(code,store_id,order_id,gtin,status,updated_at) VALUES($c,$s,$o,$g,'ASSIGNED',$at)
ON CONFLICT(code) DO UPDATE SET status='ASSIGNED',updated_at=$at";
            reserve.Parameters.AddWithValue("$c",code);reserve.Parameters.AddWithValue("$s",storeId);reserve.Parameters.AddWithValue("$o",orderId);reserve.Parameters.AddWithValue("$g",gtin);reserve.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));reserve.ExecuteNonQuery();
        }
        using(var update=c.CreateCommand()) {
            update.Transaction=tx;update.CommandText="UPDATE kiz_pool SET status='ASSIGNED',assigned_order=$o,updated_at=$at WHERE code=$c";
            update.Parameters.AddWithValue("$c",code);update.Parameters.AddWithValue("$o",orderId);update.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ValidateWbKizOwnership(long storeId,string orderId,string gtin,string code)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();
        ValidateWbKizOwnership(c,null,storeId,orderId,gtin,code);
    }

    private static void ValidateWbKizOwnership(SqliteConnection c,SqliteTransaction? tx,long storeId,string orderId,string gtin,string code)
    {
        bool owned=false;
        using(var read=c.CreateCommand()) {
            read.Transaction=tx;read.CommandText="SELECT code,store_id,order_id,gtin FROM wb_kiz_reservations WHERE code=$c OR (store_id=$s AND order_id=$o)";
            read.Parameters.AddWithValue("$c",code);read.Parameters.AddWithValue("$s",storeId);read.Parameters.AddWithValue("$o",orderId);
            using var row=read.ExecuteReader();while(row.Read()) {
                if(row.GetString(0)!=code || row.GetInt64(1)!=storeId || row.GetString(2)!=orderId || row.GetString(3)!=gtin)
                    throw new InvalidOperationException("KIZ đang thuộc đơn/cửa hàng hoặc GTIN khác. Dừng để đối soát.");
                owned=true;
            }
        }
        using(var table=c.CreateCommand()) {
            table.Transaction=tx;table.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='marketplace_kiz_reservations'";
            if(Convert.ToInt64(table.ExecuteScalar())>0) {
                using var other=c.CreateCommand();other.Transaction=tx;other.CommandText="SELECT COUNT(*) FROM marketplace_kiz_reservations WHERE code=$c";other.Parameters.AddWithValue("$c",code);
                if(Convert.ToInt64(other.ExecuteScalar())>0)throw new InvalidOperationException("KIZ đã giữ/gán cho Ozon hoặc Yandex. Không chuyển chủ sở hữu sang WB.");
            }
        }
        using(var pool=c.CreateCommand()) {
            pool.Transaction=tx;pool.CommandText="SELECT gtin,status,assigned_order FROM kiz_pool WHERE code=$c";pool.Parameters.AddWithValue("$c",code);
            using var row=pool.ExecuteReader();if(!row.Read())return;
            var status=row.GetString(1);var owner=row.GetString(2);
            if(row.GetString(0)!=gtin ||
               (status=="AVAILABLE" ? owner.Length>0 : !owned || status is not ("RESERVED" or "ASSIGNED") || (owner!="WB:"+storeId+":"+orderId && owner!=orderId)))
                throw new InvalidOperationException("Kho KIZ đã ghi nhận mã thuộc đơn khác hoặc chưa được đối soát. Giữ nguyên mã và chủ sở hữu.");
        }
    }
}
