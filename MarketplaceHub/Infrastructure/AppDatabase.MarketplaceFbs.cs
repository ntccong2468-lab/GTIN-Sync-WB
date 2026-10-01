using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Infrastructure;

public sealed record MarketplaceFbsBatch(string Id,long StoreId,string Name,DateTimeOffset CreatedAt,string Status);
public sealed record MarketplaceUnitKiz(string ItemId,int Unit,string Gtin,string Code,string Status);
public sealed record MarketplaceFbsBatchSummary(string Id,string Name,DateTimeOffset CreatedAt,string Status,int OrderCount,int Quantity);
public sealed record OzonLabelJob(long StoreId,string PostingNumber,string TaskId,string Status,string SafeError,DateTimeOffset UpdatedAt);

public sealed partial class AppDatabase
{
    private static void InitializeMarketplaceFbsTables(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=@"
CREATE TABLE IF NOT EXISTS marketplace_kiz_reservations(code TEXT PRIMARY KEY,store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,order_id TEXT NOT NULL,item_id TEXT NOT NULL,unit_index INTEGER NOT NULL,gtin TEXT NOT NULL,status TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(store_id,marketplace,order_id,item_id,unit_index));
CREATE TABLE IF NOT EXISTS marketplace_fbs_batches(id TEXT PRIMARY KEY,store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,name TEXT NOT NULL,created_at TEXT NOT NULL,status TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS marketplace_fbs_actions(store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,order_id TEXT NOT NULL,state TEXT NOT NULL,PRIMARY KEY(store_id,marketplace,order_id));
CREATE TABLE IF NOT EXISTS marketplace_fbs_batch_orders(batch_id TEXT NOT NULL,order_id TEXT NOT NULL,status TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',layout_json TEXT NOT NULL DEFAULT '',PRIMARY KEY(batch_id,order_id));
CREATE TABLE IF NOT EXISTS ozon_label_jobs(store_id INTEGER NOT NULL,posting_number TEXT NOT NULL,task_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL,safe_error TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL,PRIMARY KEY(store_id,posting_number));";cmd.ExecuteNonQuery();
    }

    public IReadOnlySet<string> MarketplaceReceivedOrderIds(StoreProfile store)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"SELECT o.order_id FROM marketplace_fbs_batch_orders o JOIN marketplace_fbs_batches b ON b.id=o.batch_id WHERE b.store_id=$s AND b.marketplace=$m";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());var result=new HashSet<string>(StringComparer.Ordinal);
        using var row=cmd.ExecuteReader();while(row.Read())result.Add(row.GetString(0));return result;
    }

    public IReadOnlyList<MarketplaceFbsBatchSummary> MarketplaceFbsBatches(StoreProfile store,bool shipping=false)
    {
        var result=new List<MarketplaceFbsBatchSummary>();
        foreach(var batch in TodayMarketplaceFbsBatches(store)) {
            var members=MarketplaceFbsBatchOrders(store,batch.Id);if(members.Count==0)continue;
            var complete=members.All(x=>x.Status=="LABELS_READY");if(complete!=shipping)continue;
            var ids=members.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
            var quantity=Orders(store.Id).Where(x=>ids.Contains(x.ExternalOrderId)).Sum(x=>Math.Max(1,x.Quantity));
            result.Add(new(batch.Id,batch.Name,batch.CreatedAt,complete?"Đang giao":"Đang đóng gói",members.Count,quantity));
        }
        return result.OrderByDescending(x=>x.CreatedAt).ToArray();
    }

    public MarketplaceFbsBatch CreateMarketplaceFbsBatch(StoreProfile store,IEnumerable<string> orderIds,string? existingId=null)
    {
        var ids=orderIds.Distinct(StringComparer.Ordinal).ToArray();if(ids.Length==0 && existingId is null)throw new InvalidOperationException("Chưa chọn đơn FBS.");
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        MarketplaceFbsBatch batch;
        if(existingId is null) {
            var now=DateTimeOffset.UtcNow;var id=Guid.NewGuid().ToString("N");var name=store.Marketplace+" "+now.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm");
            using var insert=c.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO marketplace_fbs_batches VALUES($id,$s,$m,$n,$at,'OPEN')";
            insert.Parameters.AddWithValue("$id",id);insert.Parameters.AddWithValue("$s",store.Id);insert.Parameters.AddWithValue("$m",store.Marketplace.ToString());insert.Parameters.AddWithValue("$n",name);insert.Parameters.AddWithValue("$at",now.ToString("O"));insert.ExecuteNonQuery();batch=new(id,store.Id,name,now,"OPEN");
        } else {
            using var read=c.CreateCommand();read.Transaction=tx;read.CommandText="SELECT name,created_at,status FROM marketplace_fbs_batches WHERE id=$id AND store_id=$s AND marketplace=$m";
            read.Parameters.AddWithValue("$id",existingId);read.Parameters.AddWithValue("$s",store.Id);read.Parameters.AddWithValue("$m",store.Marketplace.ToString());
            using var row=read.ExecuteReader();if(!row.Read())throw new InvalidOperationException("Lượt FBS không thuộc cửa hàng đang chọn.");
            batch=new(existingId,store.Id,row.GetString(0),DateTimeOffset.Parse(row.GetString(1)),row.GetString(2));
            if(ids.Length>0 && MarketplaceHub.Services.MarketplaceGateway.WbBusinessDay(batch.CreatedAt)!=MarketplaceHub.Services.MarketplaceGateway.WbBusinessDay(DateTimeOffset.UtcNow))throw new InvalidOperationException("Chỉ thêm đơn vào lượt FBS của hôm nay theo giờ Moscow.");
        }
        foreach(var id in ids) {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO marketplace_fbs_batch_orders(batch_id,order_id,status) VALUES($b,$o,'PENDING')";cmd.Parameters.AddWithValue("$b",batch.Id);cmd.Parameters.AddWithValue("$o",id);cmd.ExecuteNonQuery();
        }
        tx.Commit();return batch;
    }

    public IReadOnlyList<MarketplaceFbsBatch> TodayMarketplaceFbsBatches(StoreProfile store)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,name,created_at,status FROM marketplace_fbs_batches WHERE store_id=$s AND marketplace=$m ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());var result=new List<MarketplaceFbsBatch>();
        using var row=cmd.ExecuteReader();while(row.Read()) {var at=DateTimeOffset.Parse(row.GetString(2));if(MarketplaceHub.Services.MarketplaceGateway.WbBusinessDay(at)==MarketplaceHub.Services.MarketplaceGateway.WbBusinessDay(DateTimeOffset.UtcNow) || HasUnfinishedMarketplaceBatch(c,row.GetString(0)))result.Add(new(row.GetString(0),store.Id,row.GetString(1),at,row.GetString(3)));}return result;
    }

    private static bool HasUnfinishedMarketplaceBatch(SqliteConnection c,string id)
    {using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM marketplace_fbs_batch_orders WHERE batch_id=$b AND status NOT IN('PACKED','LABELS_READY')";cmd.Parameters.AddWithValue("$b",id);return Convert.ToInt64(cmd.ExecuteScalar())>0;}

    public IReadOnlyList<(string Id,string Status,string Error,string Layout)> MarketplaceFbsBatchOrders(StoreProfile store,string batchId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT o.order_id,o.status,o.error,o.layout_json FROM marketplace_fbs_batch_orders o JOIN marketplace_fbs_batches b ON b.id=o.batch_id WHERE b.id=$b AND b.store_id=$s AND b.marketplace=$m ORDER BY o.order_id";
        cmd.Parameters.AddWithValue("$b",batchId);cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());var result=new List<(string,string,string,string)>();using var row=cmd.ExecuteReader();while(row.Read())result.Add((row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3)));return result;
    }

    public void SaveMarketplaceFbsOrder(StoreProfile store,string batchId,string orderId,string status,string error="",string? layout=null)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE marketplace_fbs_batch_orders SET status=$status,error=$e,layout_json=COALESCE($layout,layout_json) WHERE batch_id=$b AND order_id=$o AND EXISTS(SELECT 1 FROM marketplace_fbs_batches WHERE id=$b AND store_id=$s AND marketplace=$m)";
        cmd.Parameters.AddWithValue("$status",status);cmd.Parameters.AddWithValue("$e",error);cmd.Parameters.AddWithValue("$layout",(object?)layout??DBNull.Value);cmd.Parameters.AddWithValue("$b",batchId);cmd.Parameters.AddWithValue("$o",orderId);cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Đơn không thuộc lượt FBS này.");
    }

    public IReadOnlyList<MarketplaceUnitKiz> MarketplaceKizReservations(StoreProfile store,string orderId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT item_id,unit_index,gtin,code,status FROM marketplace_kiz_reservations WHERE store_id=$s AND marketplace=$m AND order_id=$o ORDER BY item_id,unit_index";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());cmd.Parameters.AddWithValue("$o",orderId);var result=new List<MarketplaceUnitKiz>();using var r=cmd.ExecuteReader();while(r.Read())result.Add(new(r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetString(4)));return result;
    }

    // Scanner FNC1 prefixes and textual GS escapes represent the same physical code.
    private const string CanonicalKizCodeSql=@"replace(replace(replace(ltrim(code,char(29)),'<GS>',char(29)),'\u001d',char(29)),'\u001D',char(29))";
    private static void InitializeKizIdentityIndexes(SqliteConnection c)
    {
        foreach(var table in new[]{"kiz_pool","wb_kiz_reservations","marketplace_kiz_reservations"}) {
            using var cmd=c.CreateCommand();cmd.CommandText="CREATE INDEX IF NOT EXISTS idx_"+table+"_canonical ON "+table+"("+CanonicalKizCodeSql+")";cmd.ExecuteNonQuery();
        }
    }
    private static void GuardKizAliasOwnership(SqliteConnection c,SqliteTransaction? tx,string code)
    {
        using var query=c.CreateCommand();query.Transaction=tx;
        query.CommandText="SELECT code FROM (SELECT code FROM wb_kiz_reservations UNION ALL SELECT code FROM marketplace_kiz_reservations UNION ALL SELECT code FROM kiz_pool WHERE status IN('RESERVED','ASSIGNED') OR assigned_order!='') WHERE code!=$code AND "+CanonicalKizCodeSql+"=$canonical LIMIT 1";
        query.Parameters.AddWithValue("$code",code);query.Parameters.AddWithValue("$canonical",MarketplaceHub.Services.MarketplaceFbsPayloads.NormalizeCode(code).TrimStart('\u001d'));
        if(query.ExecuteScalar() is not null)throw new InvalidOperationException("Cùng một KIZ có tiền tố scanner/GS khác nhưng đã thuộc đơn khác. Dừng để đối soát, không cấp lại mã.");
    }

    public string? ReserveMarketplaceKiz(StoreProfile store,string orderId,string itemId,int unit,string gtin,string? remoteCode=null)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();var owner=$"{store.Marketplace}:{store.Id}:{orderId}:{itemId}:{unit}";
        using(var existing=c.CreateCommand()) {
            existing.Transaction=tx;existing.CommandText="SELECT code,gtin FROM marketplace_kiz_reservations WHERE store_id=$s AND marketplace=$m AND order_id=$o AND item_id=$i AND unit_index=$u";
            UnitParameters(existing,store,orderId,itemId,unit);using var row=existing.ExecuteReader();if(row.Read()) {
                if(row.GetString(1)!=gtin || remoteCode is not null && MarketplaceHub.Services.MarketplaceFbsPayloads.NormalizeCode(row.GetString(0))!=MarketplaceHub.Services.MarketplaceFbsPayloads.NormalizeCode(remoteCode))throw new InvalidOperationException("Đơn vị hàng đã giữ KIZ/GTIN khác. Đối soát trước khi đổi mã.");
                var retained=row.GetString(0);row.Close();ValidateMarketplaceKizOwner(c,tx,store,orderId,itemId,unit,gtin,retained);return retained;
            }
        }
        var code=remoteCode;
        if(code is null) {
            using var select=c.CreateCommand();select.Transaction=tx;select.CommandText="SELECT code FROM kiz_pool WHERE gtin=$g AND status='AVAILABLE' AND assigned_order='' AND code NOT IN(SELECT code FROM wb_kiz_reservations) AND code NOT IN(SELECT code FROM marketplace_kiz_reservations) ORDER BY updated_at LIMIT 1";select.Parameters.AddWithValue("$g",gtin);code=select.ExecuteScalar()?.ToString();if(code is null)return null;
        }
        GuardKizAliasOwnership(c,tx,code);
        using(var conflict=c.CreateCommand()) {
            conflict.Transaction=tx;conflict.CommandText="SELECT (SELECT COUNT(*) FROM wb_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM marketplace_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM kiz_pool WHERE code=$c AND (gtin!=$g OR status!='AVAILABLE' OR assigned_order!=''))";
            conflict.Parameters.AddWithValue("$c",code);conflict.Parameters.AddWithValue("$g",gtin);if(Convert.ToInt64(conflict.ExecuteScalar())>0)throw new InvalidOperationException("KIZ thuộc đơn khác/cửa hàng khác hoặc chưa đối soát. Không ghi đè chủ sở hữu.");
        }
        using(var reserve=c.CreateCommand()) {
            reserve.Transaction=tx;reserve.CommandText="INSERT INTO marketplace_kiz_reservations VALUES($c,$s,$m,$o,$i,$u,$g,'RESERVED',$at)";UnitParameters(reserve,store,orderId,itemId,unit);reserve.Parameters.AddWithValue("$c",code);reserve.Parameters.AddWithValue("$g",gtin);reserve.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));reserve.ExecuteNonQuery();
        }
        using(var update=c.CreateCommand()) {
            update.Transaction=tx;update.CommandText="UPDATE kiz_pool SET status='RESERVED',assigned_order=$o WHERE code=$c AND status='AVAILABLE' AND assigned_order=''";update.Parameters.AddWithValue("$o",owner);update.Parameters.AddWithValue("$c",code);update.ExecuteNonQuery();
        }
        tx.Commit();return code;
    }

    public void ConfirmMarketplaceKiz(StoreProfile store,string orderId,string itemId,int unit,string gtin,string code)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        ValidateMarketplaceKizOwner(c,tx,store,orderId,itemId,unit,gtin,code);
        using(var confirm=c.CreateCommand()) {
            confirm.Transaction=tx;confirm.CommandText="UPDATE marketplace_kiz_reservations SET status='ASSIGNED',updated_at=$at WHERE code=$c AND store_id=$s AND marketplace=$m AND order_id=$o AND item_id=$i AND unit_index=$u AND gtin=$g";UnitParameters(confirm,store,orderId,itemId,unit);confirm.Parameters.AddWithValue("$c",code);confirm.Parameters.AddWithValue("$g",gtin);confirm.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));if(confirm.ExecuteNonQuery()!=1)throw new InvalidOperationException("KIZ không thuộc đơn vị hàng đang xác nhận.");
        }
        using(var update=c.CreateCommand()) {update.Transaction=tx;update.CommandText="UPDATE kiz_pool SET status='ASSIGNED' WHERE code=$c AND assigned_order=$owner AND status IN('RESERVED','ASSIGNED')";update.Parameters.AddWithValue("$c",code);update.Parameters.AddWithValue("$owner",$"{store.Marketplace}:{store.Id}:{orderId}:{itemId}:{unit}");update.ExecuteNonQuery();}tx.Commit();
    }
    private static void ValidateMarketplaceKizOwner(SqliteConnection c,SqliteTransaction tx,StoreProfile store,string order,string item,int unit,string gtin,string code)
    {
        GuardKizAliasOwnership(c,tx,code);
        using var conflict=c.CreateCommand();conflict.Transaction=tx;
        conflict.CommandText="SELECT (SELECT COUNT(*) FROM wb_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM marketplace_kiz_reservations WHERE code=$c AND (store_id!=$s OR marketplace!=$m OR order_id!=$o OR item_id!=$i OR unit_index!=$u OR gtin!=$g))+(SELECT COUNT(*) FROM kiz_pool WHERE code=$c AND (gtin!=$g OR status NOT IN('RESERVED','ASSIGNED') OR assigned_order!=$owner))";
        UnitParameters(conflict,store,order,item,unit);conflict.Parameters.AddWithValue("$c",code);conflict.Parameters.AddWithValue("$g",gtin);conflict.Parameters.AddWithValue("$owner",$"{store.Marketplace}:{store.Id}:{order}:{item}:{unit}");
        if(Convert.ToInt64(conflict.ExecuteScalar())>0)throw new InvalidOperationException("Chủ sở hữu KIZ đã thay đổi. Dừng để đối soát; không xác nhận hoặc ghi đè mã.");
    }

    private static void UnitParameters(SqliteCommand cmd,StoreProfile s,string order,string item,int unit)
    {cmd.Parameters.AddWithValue("$s",s.Id);cmd.Parameters.AddWithValue("$m",s.Marketplace.ToString());cmd.Parameters.AddWithValue("$o",order);cmd.Parameters.AddWithValue("$i",item);cmd.Parameters.AddWithValue("$u",unit);}

    public bool MarketplaceShipAlreadySubmitted(StoreProfile store,string orderId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM marketplace_fbs_actions WHERE store_id=$s AND marketplace=$m AND order_id=$o";cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());cmd.Parameters.AddWithValue("$o",orderId);return Convert.ToInt64(cmd.ExecuteScalar())>0;
    }
    public bool TryBeginMarketplaceShip(StoreProfile store,string orderId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO marketplace_fbs_actions VALUES($s,$m,$o,'SUBMITTED')";cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());cmd.Parameters.AddWithValue("$o",orderId);return cmd.ExecuteNonQuery()==1;
    }

    public OzonLabelJob GetOrCreateOzonLabelJob(StoreProfile store,string postingNumber)
    {
        if(store.Marketplace!=Marketplace.Ozon)throw new InvalidOperationException("Job nhãn này chỉ dành cho Ozon.");
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();var now=DateTimeOffset.UtcNow;
        using(var insert=c.CreateCommand()){insert.Transaction=tx;insert.CommandText="INSERT OR IGNORE INTO ozon_label_jobs(store_id,posting_number,status,updated_at) VALUES($s,$p,'NEW',$at)";insert.Parameters.AddWithValue("$s",store.Id);insert.Parameters.AddWithValue("$p",postingNumber);insert.Parameters.AddWithValue("$at",now.ToString("O"));insert.ExecuteNonQuery();}
        OzonLabelJob job;using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT task_id,status,safe_error,updated_at FROM ozon_label_jobs WHERE store_id=$s AND posting_number=$p";read.Parameters.AddWithValue("$s",store.Id);read.Parameters.AddWithValue("$p",postingNumber);using var row=read.ExecuteReader();if(!row.Read())throw new InvalidOperationException("Không tạo được checkpoint nhãn Ozon.");job=new(store.Id,postingNumber,row.GetString(0),row.GetString(1),row.GetString(2),DateTimeOffset.Parse(row.GetString(3)));}
        tx.Commit();return job;
    }

    public OzonLabelJob SaveOzonLabelJob(StoreProfile store,string postingNumber,string taskId,string status,string safeError="")
    {
        if(store.Marketplace!=Marketplace.Ozon)throw new InvalidOperationException("Job nhãn này chỉ dành cho Ozon.");
        var allowed=new[]{"NEW","CREATE_PENDING","POLLING","READY","FAILED","RECONCILE_REQUIRED"};if(!allowed.Contains(status))throw new InvalidOperationException("Trạng thái job nhãn Ozon không hợp lệ.");
        if(taskId.Length>256 || safeError.Length>512)throw new InvalidOperationException("Dữ liệu checkpoint nhãn vượt giới hạn an toàn.");
        var now=DateTimeOffset.UtcNow;using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText=@"INSERT INTO ozon_label_jobs(store_id,posting_number,task_id,status,safe_error,updated_at) VALUES($s,$p,$t,$st,$e,$at)
ON CONFLICT(store_id,posting_number) DO UPDATE SET task_id=CASE WHEN excluded.task_id='' THEN ozon_label_jobs.task_id ELSE excluded.task_id END,status=excluded.status,safe_error=excluded.safe_error,updated_at=excluded.updated_at";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$p",postingNumber);cmd.Parameters.AddWithValue("$t",taskId);cmd.Parameters.AddWithValue("$st",status);cmd.Parameters.AddWithValue("$e",safeError);cmd.Parameters.AddWithValue("$at",now.ToString("O"));cmd.ExecuteNonQuery();
        return GetOrCreateOzonLabelJob(store,postingNumber);
    }
}
