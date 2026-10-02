using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Infrastructure;

public sealed record GtinMappingRow(long StoreId,Marketplace Marketplace,string Sku,string ExternalId,string VariantId,
    string Name,string Size,string Gtin,string Source,bool Confirmed,string ZnackStage,string WbStage,string LastError,
    int TotalKiz,int AvailableKiz,int ReservedKiz,int AssignedKiz);
public sealed record GtinMappingPage(IReadOnlyList<GtinMappingRow> Rows,int Total,int MappingRuleCount,int Offset,int Limit,
    int TotalKiz,int AvailableKiz,int ReservedKiz,int AssignedKiz);

public sealed partial class AppDatabase
{
    private static void EnsureGtinMappingTables(SqliteConnection c)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=@"
CREATE TABLE IF NOT EXISTS gtin_mapping(
 store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,sku TEXT NOT NULL,variant_id TEXT NOT NULL,
 gtin TEXT NOT NULL,source TEXT NOT NULL,confirmed INTEGER NOT NULL DEFAULT 0,
 znack_good_id TEXT NOT NULL DEFAULT '',znack_stage TEXT NOT NULL DEFAULT '',wb_stage TEXT NOT NULL DEFAULT '',
 last_error TEXT NOT NULL DEFAULT '',metadata_json TEXT NOT NULL DEFAULT '{}',archived INTEGER NOT NULL DEFAULT 0,
 updated_at TEXT NOT NULL,PRIMARY KEY(store_id,marketplace,sku,variant_id));
CREATE INDEX IF NOT EXISTS ix_gtin_mapping_confirmed ON gtin_mapping(store_id,marketplace,confirmed DESC,gtin);
CREATE INDEX IF NOT EXISTS ix_kiz_gtin_status ON kiz_pool(gtin,status);
CREATE TABLE IF NOT EXISTS gtin_sync_jobs(
 store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,endpoint TEXT NOT NULL,scope TEXT NOT NULL,
 snapshot_json TEXT NOT NULL DEFAULT '[]',cursor INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL,
 retry_at TEXT NULL,last_error TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL,
 PRIMARY KEY(store_id,marketplace,endpoint));";cmd.ExecuteNonQuery();
    }

    private const string MappingProjection=@"
WITH inventory AS (
 SELECT gtin,COUNT(*) AS total,
 SUM(CASE WHEN status='AVAILABLE' AND assigned_order='' THEN 1 ELSE 0 END) AS available,
 SUM(CASE WHEN status='RESERVED' THEN 1 ELSE 0 END) AS reserved,
 SUM(CASE WHEN status='ASSIGNED' THEN 1 ELSE 0 END) AS assigned FROM kiz_pool GROUP BY gtin),
 variants AS (
 SELECT v.sku,v.external_id,v.variant_id,p.name,v.size,COALESCE(m.gtin,v.gtin) AS gtin,
 COALESCE(m.source,'marketplace') AS source,COALESCE(m.confirmed,0) AS confirmed,
 COALESCE(m.znack_stage,'') AS znack_stage,COALESCE(m.wb_stage,'') AS wb_stage,COALESCE(m.last_error,'') AS last_error,
 CASE WHEN m.sku IS NULL THEN 0 ELSE 1 END AS has_rule
 FROM product_variants v JOIN products p ON p.store_id=v.store_id AND p.marketplace=v.marketplace AND p.sku=v.sku
 LEFT JOIN gtin_mapping m ON m.store_id=v.store_id AND m.marketplace=v.marketplace AND m.sku=v.sku AND m.variant_id=v.variant_id
 WHERE v.store_id=$s AND v.marketplace=$m AND v.active=1 AND COALESCE(m.archived,0)=0),
 rows AS (
 SELECT v.*,COALESCE(i.total,0) AS total,COALESCE(i.available,0) AS available,COALESCE(i.reserved,0) AS reserved,COALESCE(i.assigned,0) AS assigned
 FROM variants v LEFT JOIN inventory i ON i.gtin=v.gtin),
 filtered AS (
 SELECT * FROM rows WHERE ($q='' OR sku LIKE $q ESCAPE '\' OR name LIKE $q ESCAPE '\' OR gtin LIKE $q ESCAPE '\' OR size LIKE $q ESCAPE '\')
 AND ($filter='all' OR ($filter='mapped' AND confirmed=1) OR ($filter='unmapped' AND confirmed=0)
 OR ($filter='available' AND available>0) OR ($filter='error' AND last_error!=''))) ";

    public GtinMappingPage GetGtinMappingPage(StoreProfile store,int offset=0,int limit=50,string query="",string filter="all")
    {
        if(limit is < 1 or > 50||offset<0)throw new ArgumentOutOfRangeException(nameof(limit),"Tối đa 50 dòng/trang.");
        if(filter is not ("all" or "mapped" or "unmapped" or "available" or "error"))throw new ArgumentException("Bộ lọc không hợp lệ.");
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();
        void Parameters(SqliteCommand cmd){cmd.Transaction=tx;cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());
            cmd.Parameters.AddWithValue("$q",query.Length==0?"":"%"+query.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%");cmd.Parameters.AddWithValue("$filter",filter);}
        int total,rules,inventory,available,reserved,assigned;
        using(var count=c.CreateCommand()){
            count.CommandText=MappingProjection+@"SELECT (SELECT COUNT(*) FROM filtered),(SELECT COALESCE(SUM(has_rule),0) FROM rows),
 (SELECT COALESCE(SUM(i.total),0) FROM inventory i WHERE i.gtin IN(SELECT DISTINCT gtin FROM variants WHERE gtin!='')),
 (SELECT COALESCE(SUM(i.available),0) FROM inventory i WHERE i.gtin IN(SELECT DISTINCT gtin FROM variants WHERE gtin!='')),
 (SELECT COALESCE(SUM(i.reserved),0) FROM inventory i WHERE i.gtin IN(SELECT DISTINCT gtin FROM variants WHERE gtin!='')),
 (SELECT COALESCE(SUM(i.assigned),0) FROM inventory i WHERE i.gtin IN(SELECT DISTINCT gtin FROM variants WHERE gtin!=''))";
            Parameters(count);using var r=count.ExecuteReader();r.Read();total=r.GetInt32(0);rules=r.GetInt32(1);inventory=r.GetInt32(2);available=r.GetInt32(3);reserved=r.GetInt32(4);assigned=r.GetInt32(5);
        }
        var rows=new List<GtinMappingRow>();
        using(var cmd=c.CreateCommand()){
            cmd.CommandText=MappingProjection+@"SELECT sku,external_id,variant_id,name,size,gtin,source,confirmed,znack_stage,wb_stage,last_error,total,available,reserved,assigned
 FROM filtered ORDER BY confirmed DESC,sku,size,variant_id LIMIT $limit OFFSET $offset";Parameters(cmd);cmd.Parameters.AddWithValue("$limit",limit);cmd.Parameters.AddWithValue("$offset",offset);
            using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(store.Id,store.Marketplace,r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7)==1,r.GetString(8),r.GetString(9),r.GetString(10),r.GetInt32(11),r.GetInt32(12),r.GetInt32(13),r.GetInt32(14)));
        }tx.Commit();return new(rows,total,rules,offset,limit,inventory,available,reserved,assigned);
    }

    public void UpsertSellerGtinMapping(StoreProfile store,string sku,string variantId,string gtin)
    {
        gtin=GtinCode.Normalize(gtin);if(gtin.Length==0)throw new InvalidOperationException("GTIN phải có 8/12/13/14 chữ số và checksum hợp lệ.");
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();GuardMappingVariant(c,tx,store,sku,variantId);
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=@"
INSERT INTO gtin_mapping(store_id,marketplace,sku,variant_id,gtin,source,confirmed,updated_at)
 VALUES($s,$m,$sku,$v,$g,'seller',1,$at)
ON CONFLICT(store_id,marketplace,sku,variant_id) DO UPDATE SET gtin=$g,source='seller',confirmed=1,archived=0,
 znack_good_id=CASE WHEN gtin_mapping.gtin=$g THEN gtin_mapping.znack_good_id ELSE '' END,
 znack_stage=CASE WHEN gtin_mapping.gtin=$g THEN gtin_mapping.znack_stage ELSE '' END,
 wb_stage=CASE WHEN gtin_mapping.gtin=$g THEN gtin_mapping.wb_stage ELSE 'PENDING' END,last_error='',updated_at=$at";
        MappingParameters(cmd,store,sku,variantId,gtin);cmd.ExecuteNonQuery();tx.Commit();
    }

    public IReadOnlyDictionary<(string Sku,string VariantId),string> ConfirmedGtinMappings(StoreProfile store)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT m.sku,m.variant_id,m.gtin FROM gtin_mapping m JOIN product_variants v ON v.store_id=m.store_id AND v.marketplace=m.marketplace AND v.sku=m.sku AND v.variant_id=m.variant_id WHERE m.store_id=$s AND m.marketplace=$m AND m.confirmed=1 AND m.archived=0 AND v.active=1";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());
        var result=new Dictionary<(string,string),string>();using var r=cmd.ExecuteReader();while(r.Read())result[(r.GetString(0),r.GetString(1))]=r.GetString(2);return result;
    }

    private static void MappingParameters(SqliteCommand cmd,StoreProfile store,string sku,string variantId,string gtin)
    {cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());cmd.Parameters.AddWithValue("$sku",sku);cmd.Parameters.AddWithValue("$v",variantId);cmd.Parameters.AddWithValue("$g",gtin);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));}

    private static void GuardMappingVariant(SqliteConnection c,SqliteTransaction tx,StoreProfile store,string sku,string variantId)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT COUNT(*) FROM product_variants WHERE store_id=$s AND marketplace=$m AND sku=$sku AND variant_id=$v AND active=1";
        cmd.Parameters.AddWithValue("$s",store.Id);cmd.Parameters.AddWithValue("$m",store.Marketplace.ToString());cmd.Parameters.AddWithValue("$sku",sku);cmd.Parameters.AddWithValue("$v",variantId);
        if(Convert.ToInt32(cmd.ExecuteScalar())!=1)throw new InvalidOperationException("Không tìm thấy đúng biến thể hoạt động trong cửa hàng.");
    }

    public void ArchiveGtinMapping(StoreProfile store,string sku,string variantId)
    {
        using var c=new SqliteConnection(ConnectionString);c.Open();using var tx=c.BeginTransaction();GuardMappingVariant(c,tx,store,sku,variantId);
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=@"
INSERT INTO gtin_mapping(store_id,marketplace,sku,variant_id,gtin,source,archived,updated_at)
SELECT store_id,marketplace,sku,variant_id,gtin,'marketplace',1,$at FROM product_variants WHERE store_id=$s AND marketplace=$m AND sku=$sku AND variant_id=$v
ON CONFLICT(store_id,marketplace,sku,variant_id) DO UPDATE SET archived=1,updated_at=$at";
        MappingParameters(cmd,store,sku,variantId,"");cmd.ExecuteNonQuery();tx.Commit();
    }
}
