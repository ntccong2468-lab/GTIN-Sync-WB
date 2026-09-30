using Microsoft.Data.Sqlite;
using MarketplaceHub.Core;
using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Infrastructure;

public sealed class AppDatabase
{
    public string DbPath { get; }
    private string ConnectionString => $"Data Source={DbPath}";

    public AppDatabase()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketplaceHub");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "marketplacehub.db");
        Initialize();
    }

    private void Initialize()
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS stores(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 marketplace TEXT NOT NULL,
 name TEXT NOT NULL,
 client_id TEXT NOT NULL DEFAULT '',
 api_key_enc TEXT NOT NULL DEFAULT '',
 business_id TEXT NOT NULL DEFAULT '',
 campaign_id TEXT NOT NULL DEFAULT '',
 token_enc TEXT NOT NULL DEFAULT '',
 enabled INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS products(
 store_id INTEGER NOT NULL,
 marketplace TEXT NOT NULL,
 external_id TEXT NOT NULL,
 sku TEXT NOT NULL,
 name TEXT NOT NULL,
 price REAL NULL,
 image_url TEXT NOT NULL DEFAULT '',
 raw_json TEXT NOT NULL,
 synced_at TEXT NOT NULL,
 PRIMARY KEY(store_id, sku)
);
CREATE TABLE IF NOT EXISTS fbs_orders(
 store_id INTEGER NOT NULL,
 marketplace TEXT NOT NULL,
 external_order_id TEXT NOT NULL,
 sku TEXT NOT NULL,
 name TEXT NOT NULL,
 quantity INTEGER NOT NULL,
 status TEXT NOT NULL,
 needs_kiz INTEGER NOT NULL DEFAULT 0,
 raw_json TEXT NOT NULL,
 synced_at TEXT NOT NULL,
 PRIMARY KEY(store_id, external_order_id, sku)
);
CREATE TABLE IF NOT EXISTS price_history(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 store_id INTEGER NOT NULL,
 sku TEXT NOT NULL,
 old_price REAL NULL,
 new_price REAL NOT NULL,
 changed_at TEXT NOT NULL,
 result TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS kiz_pool(
 code TEXT PRIMARY KEY,
 gtin TEXT NOT NULL,
 status TEXT NOT NULL,
 assigned_order TEXT NOT NULL DEFAULT '',
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS audit(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 at TEXT NOT NULL,
 module TEXT NOT NULL,
 action TEXT NOT NULL,
 detail TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS znak_config(
 id INTEGER PRIMARY KEY CHECK(id=1),
 inn TEXT NOT NULL DEFAULT '',
 environment TEXT NOT NULL DEFAULT 'Production',
 certificate_thumbprint TEXT NOT NULL DEFAULT '',
 certificate_subject TEXT NOT NULL DEFAULT '',
 auto_sign_mode TEXT NOT NULL DEFAULT 'Thủ công',
 enabled INTEGER NOT NULL DEFAULT 0,
 oms_id TEXT NOT NULL DEFAULT '',
 oms_connection TEXT NOT NULL DEFAULT '',
 auto_circulation INTEGER NOT NULL DEFAULT 0
);";
        cmd.ExecuteNonQuery();
        EnsureZnakColumns(c);
    }

    private static void EnsureZnakColumns(SqliteConnection c)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var q = c.CreateCommand())
        {
            q.CommandText = "PRAGMA table_info(znak_config)";
            using var r = q.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }

        if (!columns.Contains("oms_id"))
        {
            using var a = c.CreateCommand();
            a.CommandText = "ALTER TABLE znak_config ADD COLUMN oms_id TEXT NOT NULL DEFAULT ''";
            a.ExecuteNonQuery();
        }
        if (!columns.Contains("oms_connection"))
        {
            using var a = c.CreateCommand();
            a.CommandText = "ALTER TABLE znak_config ADD COLUMN oms_connection TEXT NOT NULL DEFAULT ''";
            a.ExecuteNonQuery();
        }
        if (!columns.Contains("auto_circulation"))
        {
            using var a = c.CreateCommand();
            a.CommandText = "ALTER TABLE znak_config ADD COLUMN auto_circulation INTEGER NOT NULL DEFAULT 0";
            a.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<StoreProfile> Stores()
    {
        var result = new List<StoreProfile>();
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,marketplace,name,client_id,api_key_enc,business_id,campaign_id,token_enc,enabled FROM stores ORDER BY marketplace,name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new StoreProfile(
                r.GetInt64(0),
                Enum.Parse<Marketplace>(r.GetString(1)),
                r.GetString(2),
                r.GetString(3),
                SecretVault.Unprotect(r.GetString(4)),
                r.GetString(5),
                r.GetString(6),
                SecretVault.Unprotect(r.GetString(7)),
                r.GetInt64(8) == 1));
        }
        return result;
    }

    public StoreProfile SaveStore(StoreProfile s)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        if (s.Id == 0)
            cmd.CommandText = @"INSERT INTO stores(marketplace,name,client_id,api_key_enc,business_id,campaign_id,token_enc,enabled)
VALUES($m,$n,$c,$a,$b,$ca,$t,$e); SELECT last_insert_rowid();";
        else
        {
            cmd.CommandText = @"UPDATE stores SET marketplace=$m,name=$n,client_id=$c,api_key_enc=$a,business_id=$b,campaign_id=$ca,token_enc=$t,enabled=$e WHERE id=$id; SELECT $id;";
            cmd.Parameters.AddWithValue("$id", s.Id);
        }
        cmd.Parameters.AddWithValue("$m", s.Marketplace.ToString());
        cmd.Parameters.AddWithValue("$n", s.Name.Trim());
        cmd.Parameters.AddWithValue("$c", s.ClientId.Trim());
        cmd.Parameters.AddWithValue("$a", SecretVault.Protect(s.ApiKey));
        cmd.Parameters.AddWithValue("$b", s.BusinessId.Trim());
        cmd.Parameters.AddWithValue("$ca", s.CampaignId.Trim());
        cmd.Parameters.AddWithValue("$t", SecretVault.Protect(s.Token));
        cmd.Parameters.AddWithValue("$e", s.Enabled ? 1 : 0);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        Audit("Stores", "Save", $"{s.Marketplace}:{s.Name}");
        return s with { Id = id };
    }

    public void ReplaceProducts(long storeId, Marketplace marketplace, IEnumerable<ProductRow> rows)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        using (var d = c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = "DELETE FROM products WHERE store_id=$s";
            d.Parameters.AddWithValue("$s", storeId);
            d.ExecuteNonQuery();
        }
        foreach (var p in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO products(store_id,marketplace,external_id,sku,name,price,image_url,raw_json,synced_at)
VALUES($s,$m,$e,$sku,$n,$p,$i,$r,$at)";
            cmd.Parameters.AddWithValue("$s", storeId);
            cmd.Parameters.AddWithValue("$m", marketplace.ToString());
            cmd.Parameters.AddWithValue("$e", p.ExternalId);
            cmd.Parameters.AddWithValue("$sku", p.Sku);
            cmd.Parameters.AddWithValue("$n", p.Name);
            cmd.Parameters.AddWithValue("$p", p.Price is null ? DBNull.Value : p.Price.Value);
            cmd.Parameters.AddWithValue("$i", p.ImageUrl);
            cmd.Parameters.AddWithValue("$r", p.RawJson);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Audit("Products", "Sync", $"{marketplace}:{storeId}");
    }

    public IReadOnlyList<ProductRow> Products(long storeId)
    {
        var result = new List<ProductRow>();
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT marketplace,external_id,sku,name,price,image_url,raw_json FROM products WHERE store_id=$s ORDER BY name,sku";
        cmd.Parameters.AddWithValue("$s", storeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ProductRow(storeId, Enum.Parse<Marketplace>(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.GetString(5), r.GetString(6)));
        return result;
    }

    public ProductRow? Product(long storeId, string sku) =>
        Products(storeId).FirstOrDefault(x => x.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase));

    public void ReplaceOrders(long storeId, Marketplace marketplace, IEnumerable<FbsOrderRow> rows)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        using (var d = c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = "DELETE FROM fbs_orders WHERE store_id=$s";
            d.Parameters.AddWithValue("$s", storeId);
            d.ExecuteNonQuery();
        }
        foreach (var o in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO fbs_orders(store_id,marketplace,external_order_id,sku,name,quantity,status,needs_kiz,raw_json,synced_at)
VALUES($s,$m,$o,$sku,$n,$q,$st,$k,$r,$at)";
            cmd.Parameters.AddWithValue("$s", storeId);
            cmd.Parameters.AddWithValue("$m", marketplace.ToString());
            cmd.Parameters.AddWithValue("$o", o.ExternalOrderId);
            cmd.Parameters.AddWithValue("$sku", o.Sku);
            cmd.Parameters.AddWithValue("$n", o.Name);
            cmd.Parameters.AddWithValue("$q", o.Quantity);
            cmd.Parameters.AddWithValue("$st", o.Status);
            cmd.Parameters.AddWithValue("$k", o.NeedsKiz ? 1 : 0);
            cmd.Parameters.AddWithValue("$r", o.RawJson);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Audit("FBS", "Sync", $"{marketplace}:{storeId}");
    }

    public IReadOnlyList<FbsOrderRow> Orders(long storeId)
    {
        var result = new List<FbsOrderRow>();
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT marketplace,external_order_id,sku,name,quantity,status,needs_kiz,raw_json FROM fbs_orders WHERE store_id=$s ORDER BY external_order_id";
        cmd.Parameters.AddWithValue("$s", storeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new FbsOrderRow(storeId, Enum.Parse<Marketplace>(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetInt64(6) == 1, r.GetString(7)));
        return result;
    }

    public void AddPriceHistory(long storeId, string sku, decimal? oldPrice, decimal newPrice, string result)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO price_history(store_id,sku,old_price,new_price,changed_at,result) VALUES($s,$sku,$o,$n,$at,$r)";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$o", oldPrice is null ? DBNull.Value : oldPrice.Value);
        cmd.Parameters.AddWithValue("$n", newPrice);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$r", result);
        cmd.ExecuteNonQuery();
    }

    public void UpsertKiz(string code, string gtin, string status, string assignedOrder = "")
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO kiz_pool(code,gtin,status,assigned_order,updated_at) VALUES($c,$g,$s,$o,$at)
ON CONFLICT(code) DO UPDATE SET gtin=$g,status=$s,assigned_order=$o,updated_at=$at";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gtin);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$o", assignedOrder);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Audit("KIZ", "Upsert", $"{gtin}:{status}");
    }

    public IReadOnlyList<(string Code, string Gtin, string Status, string Assigned)> Kiz()
    {
        var result = new List<(string, string, string, string)>();
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT code,gtin,status,assigned_order FROM kiz_pool ORDER BY updated_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return result;
    }


    public ZnakConfig GetZnakConfig()
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT inn,environment,certificate_thumbprint,certificate_subject,auto_sign_mode,enabled,oms_id,oms_connection,auto_circulation FROM znak_config WHERE id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new ZnakConfig("", "Production", "", "", "Thủ công", false, "", "", false);
        return new ZnakConfig(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) == 1, r.GetString(6), r.GetString(7), r.GetInt64(8) == 1);
    }

    public void SaveZnakConfig(ZnakConfig z)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO znak_config(id,inn,environment,certificate_thumbprint,certificate_subject,auto_sign_mode,enabled,oms_id,oms_connection,auto_circulation)
VALUES(1,$i,$e,$t,$s,$a,$n,$o,$c,$r)
ON CONFLICT(id) DO UPDATE SET inn=$i,environment=$e,certificate_thumbprint=$t,certificate_subject=$s,auto_sign_mode=$a,enabled=$n,oms_id=$o,oms_connection=$c,auto_circulation=$r";
        cmd.Parameters.AddWithValue("$i", z.Inn.Trim());
        cmd.Parameters.AddWithValue("$e", z.Environment);
        cmd.Parameters.AddWithValue("$t", z.CertificateThumbprint);
        cmd.Parameters.AddWithValue("$s", z.CertificateSubject);
        cmd.Parameters.AddWithValue("$a", z.AutoSignMode);
        cmd.Parameters.AddWithValue("$n", z.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$o", z.OmsId);
        cmd.Parameters.AddWithValue("$c", z.OmsConnection);
        cmd.Parameters.AddWithValue("$r", z.AutoCirculation ? 1 : 0);
        cmd.ExecuteNonQuery();
        Audit("Честный ЗНАК", "Lưu cấu hình", $"{z.Inn}:{z.Environment}:{z.AutoSignMode}");
    }

    public IReadOnlyList<AuditRow> AuditRows(int limit = 200)
    {
        var result = new List<AuditRow>();
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at,module,action,detail FROM audit ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var at = DateTimeOffset.TryParse(r.GetString(0), out var parsed) ? parsed : DateTimeOffset.MinValue;
            result.Add(new AuditRow(at, r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        return result;
    }

    public void DeleteStore(long id)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "DELETE FROM products WHERE store_id=$id";
            p.Parameters.AddWithValue("$id", id);
            p.ExecuteNonQuery();
        }
        using (var o = c.CreateCommand())
        {
            o.Transaction = tx;
            o.CommandText = "DELETE FROM fbs_orders WHERE store_id=$id";
            o.Parameters.AddWithValue("$id", id);
            o.ExecuteNonQuery();
        }
        using (var h = c.CreateCommand())
        {
            h.Transaction = tx;
            h.CommandText = "DELETE FROM price_history WHERE store_id=$id";
            h.Parameters.AddWithValue("$id", id);
            h.ExecuteNonQuery();
        }
        using (var st = c.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = "DELETE FROM stores WHERE id=$id";
            st.Parameters.AddWithValue("$id", id);
            st.ExecuteNonQuery();
        }
        tx.Commit();
        Audit("Cửa hàng", "Xóa", id.ToString());
    }

    public void Audit(string module, string action, string detail)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit(at,module,action,detail) VALUES($a,$m,$x,$d)";
        cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$m", module);
        cmd.Parameters.AddWithValue("$x", action);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.ExecuteNonQuery();
    }
}

internal static class SecretVault
{
    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }
}
