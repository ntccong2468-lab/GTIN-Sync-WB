using Microsoft.Data.Sqlite;
using MarketplaceHub.Core;

namespace MarketplaceHub.Infrastructure;

public sealed record WbSupplySummary(string Id,string Name,DateTimeOffset CreatedAt,bool Done,int? OrderCount);
public sealed record WbReceiveOperation(string OperationId,string? SupplyId,string Name,string State,IReadOnlySet<string> OrderIds,DateTimeOffset? RetryAt=null,string RetryEndpoint="");

public sealed partial class AppDatabase
{
    private static void InitializeWbShipmentTables(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();
        cmd.CommandText=@"
CREATE TABLE IF NOT EXISTS wb_supply_orders(store_id INTEGER NOT NULL,order_id TEXT NOT NULL,supply_id TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(store_id,order_id));
CREATE INDEX IF NOT EXISTS ix_wb_supply_orders_supply ON wb_supply_orders(store_id,supply_id);
CREATE TABLE IF NOT EXISTS wb_supplies(store_id INTEGER NOT NULL,supply_id TEXT NOT NULL,name TEXT NOT NULL,created_at TEXT NOT NULL,done INTEGER NOT NULL,order_count INTEGER NULL,updated_at TEXT NOT NULL,PRIMARY KEY(store_id,supply_id));
CREATE INDEX IF NOT EXISTS ix_wb_supplies_open ON wb_supplies(store_id,done,created_at DESC);
CREATE TABLE IF NOT EXISTS wb_receive_journal(
 store_id INTEGER NOT NULL,operation_id TEXT NOT NULL,order_id TEXT NOT NULL,
 supply_id TEXT NULL,name TEXT NOT NULL,state TEXT NOT NULL,verified INTEGER NOT NULL DEFAULT 0,
 disposition TEXT NOT NULL DEFAULT '',detail TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL,
 PRIMARY KEY(store_id,operation_id,order_id));
CREATE INDEX IF NOT EXISTS ix_wb_receive_journal_pending ON wb_receive_journal(store_id,state,order_id);
CREATE TABLE IF NOT EXISTS wb_kiz_reservations(code TEXT PRIMARY KEY,store_id INTEGER NOT NULL,order_id TEXT NOT NULL,gtin TEXT NOT NULL,status TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(store_id,order_id));";
        cmd.ExecuteNonQuery();
        var columns=new HashSet<string>();using(var info=c.CreateCommand()){info.CommandText="PRAGMA table_info(wb_receive_journal)";using var rows=info.ExecuteReader();while(rows.Read())columns.Add(rows.GetString(1));}
        foreach(var column in new[]{("retry_at","TEXT NULL"),("retry_endpoint","TEXT NOT NULL DEFAULT ''")})if(!columns.Contains(column.Item1)){
            using var add=c.CreateCommand();add.CommandText="ALTER TABLE wb_receive_journal ADD COLUMN "+column.Item1+" "+column.Item2;add.ExecuteNonQuery();
        }
    }

    public void BeginWbReceiveOperation(long storeId,string operationId,IEnumerable<string> orderIds,string? supplyId,string name)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        foreach(var id in orderIds.Distinct(StringComparer.Ordinal)) {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=@"INSERT INTO wb_receive_journal(store_id,operation_id,order_id,supply_id,name,state,updated_at)
VALUES($s,$op,$id,$supply,$name,'INTENT',$at) ON CONFLICT(store_id,operation_id,order_id) DO NOTHING";
            cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$op",operationId);cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$supply",(object?)supplyId??DBNull.Value);cmd.Parameters.AddWithValue("$name",name);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public WbReceiveOperation? RecoverableWbReceive(long storeId,IEnumerable<string> orderIds)
    {
        var selected=orderIds.ToHashSet(StringComparer.Ordinal);
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT operation_id,order_id,supply_id,name,state,retry_at,retry_endpoint FROM wb_receive_journal WHERE store_id=$s AND state!='DONE' ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$s",storeId);
        var operations=new Dictionary<string,(string? Supply,string Name,string State,HashSet<string> Ids,DateTimeOffset? RetryAt,string Endpoint)>(StringComparer.Ordinal);
        using var row=cmd.ExecuteReader();while(row.Read()) {
            var op=row.GetString(0);
            if(!operations.TryGetValue(op,out var data))data=(row.IsDBNull(2)?null:row.GetString(2),row.GetString(3),row.GetString(4),new(StringComparer.Ordinal),row.IsDBNull(5)?null:DateTimeOffset.Parse(row.GetString(5)),row.GetString(6));
            data.Ids.Add(row.GetString(1));operations[op]=data;
        }
        var matches=operations.Where(x=>x.Value.Ids.Overlaps(selected)).ToArray();
        if(matches.Length>1)throw new InvalidOperationException("Các đơn thuộc nhiều thao tác nhận chưa hoàn tất. Mở từng shipment để đối soát riêng.");
        if(matches.Length==0)return null;
        var match=matches[0];return new(match.Key,match.Value.Supply,match.Value.Name,match.Value.State,match.Value.Ids,match.Value.RetryAt,match.Value.Endpoint);
    }

    public void SaveWbReceiveOperation(long storeId,string operationId,string? supplyId,string state,IEnumerable<string> verifiedIds)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        using(var conflict=c.CreateCommand()) {
            conflict.Transaction=tx;conflict.CommandText="SELECT COUNT(*) FROM wb_receive_journal WHERE store_id=$s AND operation_id=$op AND supply_id IS NOT NULL AND supply_id!=$supply";
            conflict.Parameters.AddWithValue("$s",storeId);conflict.Parameters.AddWithValue("$op",operationId);conflict.Parameters.AddWithValue("$supply",(object?)supplyId??DBNull.Value);
            if(Convert.ToInt64(conflict.ExecuteScalar())>0)throw new InvalidOperationException("Không đổi shipment của thao tác nhận đang phục hồi.");
        }
        using(var update=c.CreateCommand()) {
            update.Transaction=tx;update.CommandText="UPDATE wb_receive_journal SET supply_id=COALESCE($supply,supply_id),state=$state,updated_at=$at WHERE store_id=$s AND operation_id=$op";
            update.Parameters.AddWithValue("$s",storeId);update.Parameters.AddWithValue("$op",operationId);update.Parameters.AddWithValue("$supply",(object?)supplyId??DBNull.Value);update.Parameters.AddWithValue("$state",state);update.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));update.ExecuteNonQuery();
        }
        foreach(var id in verifiedIds.Distinct(StringComparer.Ordinal)) {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE wb_receive_journal SET verified=1 WHERE store_id=$s AND operation_id=$op AND order_id=$id";
            cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$op",operationId);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
            if(supplyId is not null) {
                using var member=c.CreateCommand();member.Transaction=tx;
                member.CommandText=@"INSERT INTO wb_supply_orders(store_id,order_id,supply_id,updated_at) VALUES($s,$id,$supply,$at)
ON CONFLICT(store_id,order_id) DO UPDATE SET updated_at=$at WHERE supply_id=$supply";
                member.Parameters.AddWithValue("$s",storeId);member.Parameters.AddWithValue("$id",id);member.Parameters.AddWithValue("$supply",supplyId);member.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));member.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public void FinishWbReceiveOperation(long storeId,string operationId,WbReceiveResult result)
    {
        var terminal=result.Success||result.SupplyId is null&&result.Orders.Count>0&&result.Orders.All(x=>x.RemoteStatus is not null&&(x.Disposition is WbReceiveDisposition.Cancelled or WbReceiveDisposition.Rejected));
        SaveWbReceiveOperation(storeId,operationId,result.SupplyId,terminal?"DONE":"RECONCILE_REQUIRED",result.Orders.Where(x=>x.Verified).Select(x=>x.OrderId));
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        using(var retry=c.CreateCommand()){
            retry.Transaction=tx;retry.CommandText="UPDATE wb_receive_journal SET retry_at=$retry,retry_endpoint=$endpoint WHERE store_id=$s AND operation_id=$op";
            retry.Parameters.AddWithValue("$s",storeId);retry.Parameters.AddWithValue("$op",operationId);retry.Parameters.AddWithValue("$retry",(object?)result.RetryAt?.ToString("O")??DBNull.Value);retry.Parameters.AddWithValue("$endpoint",result.RetryEndpoint);retry.ExecuteNonQuery();
        }
        foreach(var order in result.Orders) {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE wb_receive_journal SET disposition=$d,detail=$detail WHERE store_id=$s AND operation_id=$op AND order_id=$id";
            cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$op",operationId);cmd.Parameters.AddWithValue("$id",order.OrderId);cmd.Parameters.AddWithValue("$d",order.Disposition.ToString());cmd.Parameters.AddWithValue("$detail",order.Message);cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlySet<string> WbReceivedOrderIds(long storeId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT order_id FROM wb_supply_orders WHERE store_id=$s";cmd.Parameters.AddWithValue("$s",storeId);
        var result=new HashSet<string>(StringComparer.Ordinal);using var row=cmd.ExecuteReader();while(row.Read())result.Add(row.GetString(0));return result;
    }

    public void UpsertWbSupply(long storeId,WbSupply supply,int? orderCount=null)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"INSERT INTO wb_supplies(store_id,supply_id,name,created_at,done,order_count,updated_at) VALUES($s,$id,$n,$at,$done,$count,$now)
ON CONFLICT(store_id,supply_id) DO UPDATE SET name=$n,created_at=$at,done=$done,order_count=COALESCE($count,order_count),updated_at=$now";
        cmd.Parameters.AddWithValue("$s",storeId);cmd.Parameters.AddWithValue("$id",supply.Id);cmd.Parameters.AddWithValue("$n",supply.Name);
        cmd.Parameters.AddWithValue("$at",supply.CreatedAt.ToString("O"));cmd.Parameters.AddWithValue("$done",supply.Done?1:0);
        cmd.Parameters.AddWithValue("$count",(object?)orderCount??DBNull.Value);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<WbSupplySummary> WbSupplies(StoreProfile store,bool done=false)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"SELECT s.supply_id,s.name,s.created_at,s.done,
COALESCE(s.order_count,(SELECT COUNT(*) FROM wb_supply_orders o WHERE o.store_id=s.store_id AND o.supply_id=s.supply_id))
FROM wb_supplies s WHERE s.store_id=$store AND s.done=$done ORDER BY s.created_at DESC";
        cmd.Parameters.AddWithValue("$store",store.Id);cmd.Parameters.AddWithValue("$done",done?1:0);var result=new List<WbSupplySummary>();
        using var row=cmd.ExecuteReader();while(row.Read())result.Add(new(row.GetString(0),row.GetString(1),DateTimeOffset.Parse(row.GetString(2)),row.GetInt32(3)!=0,row.IsDBNull(4)?null:row.GetInt32(4)));return result;
    }

    public void StoreWbSupplyMembership(long storeId,string supplyId,IEnumerable<string> ids)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        foreach(var id in ids.Distinct()) {
            using(var conflict=c.CreateCommand()){
                conflict.Transaction=tx;conflict.CommandText="SELECT COUNT(*) FROM wb_supply_orders WHERE store_id=$s AND order_id=$o AND supply_id!=$p";
                conflict.Parameters.AddWithValue("$s",storeId);conflict.Parameters.AddWithValue("$o",id);conflict.Parameters.AddWithValue("$p",supplyId);
                if(Convert.ToInt32(conflict.ExecuteScalar())>0)throw new InvalidOperationException("WB trả đơn đã có shipment khác trong dữ liệu đã xác minh. Dừng để đối soát; không di chuyển membership.");
            }
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=@"INSERT INTO wb_supply_orders(store_id,order_id,supply_id,updated_at) VALUES($s,$o,$p,$at)
ON CONFLICT(store_id,order_id) DO UPDATE SET updated_at=$at WHERE wb_supply_orders.supply_id=$p";
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
        GuardKizAliasOwnership(c,tx,code);
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
        GuardKizAliasOwnership(c,tx,code);
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
