using MarketplaceHub.Core;
using MarketplaceHub.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure;

public sealed partial class AppDatabase
{
    // Used by portable persistence verification and by applications choosing an explicit database location.
    public AppDatabase(string databasePath)
    {
        DbPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Initialize();
    }

    private static void EnsureProductCatalogTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS product_catalog_checkpoint(
 store_id INTEGER NOT NULL,
 marketplace TEXT NOT NULL,
 cursor TEXT NOT NULL DEFAULT '',
 scope TEXT NOT NULL,
 pages INTEGER NOT NULL DEFAULT 0,
 products INTEGER NOT NULL DEFAULT 0,
 variants INTEGER NOT NULL DEFAULT 0,
 complete INTEGER NOT NULL DEFAULT 0,
 error TEXT NOT NULL DEFAULT '',
 updated_at TEXT NOT NULL,
 PRIMARY KEY(store_id,marketplace)
);
CREATE TABLE IF NOT EXISTS product_variants(
 store_id INTEGER NOT NULL,
 marketplace TEXT NOT NULL,
 sku TEXT NOT NULL,
 external_id TEXT NOT NULL,
 variant_id TEXT NOT NULL,
 size TEXT NOT NULL DEFAULT '',
 barcodes_json TEXT NOT NULL DEFAULT '[]',
 gtin TEXT NOT NULL DEFAULT '',
 image_url TEXT NOT NULL DEFAULT '',
 raw_json TEXT NOT NULL,
 active INTEGER NOT NULL DEFAULT 1,
 synced_at TEXT NOT NULL,
 PRIMARY KEY(store_id,marketplace,sku,variant_id)
);
CREATE INDEX IF NOT EXISTS product_variants_gtin ON product_variants(store_id,marketplace,gtin,active);";
        cmd.ExecuteNonQuery();
    }

    public ProductCatalogCheckpoint? ProductCatalogCheckpoint(StoreProfile store)
    {
        using var c = new SqliteConnection(ConnectionString); c.Open(); EnsureProductCatalogTables(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cursor,scope,pages,products,variants,complete FROM product_catalog_checkpoint WHERE store_id=$s AND marketplace=$m";
        cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5) != 0) : null;
    }

    public void BeginProductCatalog(StoreProfile store, string scope)
    {
        using var c = new SqliteConnection(ConnectionString); c.Open(); EnsureProductCatalogTables(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO product_catalog_checkpoint(store_id,marketplace,cursor,scope,pages,products,variants,complete,error,updated_at)
VALUES($s,$m,'',$scope,0,0,0,0,'',$at)
ON CONFLICT(store_id,marketplace) DO UPDATE SET cursor='',scope=$scope,pages=0,products=0,variants=0,complete=0,error='',updated_at=$at";
        cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
        cmd.Parameters.AddWithValue("$scope", scope); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public int ApplyProductCatalogPage(StoreProfile store, string requestedCursor, string scope, ProductCatalogPage page)
    {
        if (page.Entries.Any(e => e.Product.StoreId != store.Id || e.Product.Marketplace != store.Marketplace
            || e.Variants.Any(v => v.StoreId != store.Id || v.Marketplace != store.Marketplace || v.Sku != e.Product.Sku || v.ExternalId != e.Product.ExternalId)))
            throw new InvalidDataException("Trang catalog thuộc cửa hàng/SKU khác.");
        if (page.Entries.Select(e => e.Product.Sku).Distinct(StringComparer.Ordinal).Count() != page.Entries.Count)
            throw new InvalidDataException("Trang catalog trả trùng SKU; chưa ghi dữ liệu.");
        if (!page.Complete && (string.IsNullOrEmpty(page.NextCursor) || page.NextCursor == requestedCursor))
            throw new InvalidDataException("Cursor catalog không tiến triển; chưa ghi dữ liệu.");
        using var c = new SqliteConnection(ConnectionString); c.Open(); EnsureProductCatalogTables(c);
        using var tx = c.BeginTransaction();
        using (var validate = c.CreateCommand())
        {
            validate.Transaction = tx;
            validate.CommandText = "SELECT COUNT(*) FROM product_catalog_checkpoint WHERE store_id=$s AND marketplace=$m AND scope=$scope AND cursor=$cursor AND complete=0";
            validate.Parameters.AddWithValue("$s", store.Id); validate.Parameters.AddWithValue("$m", store.Marketplace.ToString());
            validate.Parameters.AddWithValue("$scope", scope); validate.Parameters.AddWithValue("$cursor", requestedCursor);
            if (Convert.ToInt64(validate.ExecuteScalar()) != 1) throw new InvalidDataException("Checkpoint catalog đã thay đổi; dừng để tránh ghi trang sai.");
        }
        if(page.Complete && page.Total is { } total) {
            using var count=c.CreateCommand();count.Transaction=tx;count.CommandText="SELECT products FROM product_catalog_checkpoint WHERE store_id=$s AND marketplace=$m";count.Parameters.AddWithValue("$s",store.Id);count.Parameters.AddWithValue("$m",store.Marketplace.ToString());
            if(Convert.ToInt64(count.ExecuteScalar())+page.Entries.Count!=total)throw new InvalidDataException("Catalog chưa đủ total của sàn; giữ checkpoint và dữ liệu trước đó.");
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var entry in page.Entries)
        {
            var p = entry.Product;
            using (var existing = c.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT marketplace,external_id FROM products WHERE store_id=$s AND sku=$sku";
                existing.Parameters.AddWithValue("$s", store.Id); existing.Parameters.AddWithValue("$sku", p.Sku);
                using var reader = existing.ExecuteReader();
                if (reader.Read() && (reader.GetString(0) != store.Marketplace.ToString() || reader.GetString(1) != p.ExternalId && store.Marketplace != Marketplace.Yandex))
                    throw new InvalidDataException($"SKU {p.Sku} đã gắn với card khác. Kiểm tra ánh xạ trước khi cập nhật catalog.");
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO products(store_id,marketplace,external_id,sku,name,price,image_url,raw_json,synced_at)
VALUES($s,$m,$id,$sku,$name,$price,$image,$raw,$at)
ON CONFLICT(store_id,sku) DO UPDATE SET marketplace=$m,external_id=$id,name=$name,
 price=COALESCE($price,products.price),image_url=CASE WHEN $image='' THEN products.image_url ELSE $image END,raw_json=$raw,synced_at=$at";
                cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
                cmd.Parameters.AddWithValue("$id", p.ExternalId); cmd.Parameters.AddWithValue("$sku", p.Sku); cmd.Parameters.AddWithValue("$name", p.Name);
                cmd.Parameters.AddWithValue("$price", p.Price is null ? DBNull.Value : p.Price.Value); cmd.Parameters.AddWithValue("$image", p.ImageUrl);
                cmd.Parameters.AddWithValue("$raw", p.RawJson); cmd.Parameters.AddWithValue("$at", now); cmd.ExecuteNonQuery();
            }
            using (var deactivate = c.CreateCommand())
            {
                deactivate.Transaction = tx;
                deactivate.CommandText = "UPDATE product_variants SET active=0 WHERE store_id=$s AND marketplace=$m AND sku=$sku";
                deactivate.Parameters.AddWithValue("$s", store.Id); deactivate.Parameters.AddWithValue("$m", store.Marketplace.ToString());
                deactivate.Parameters.AddWithValue("$sku", p.Sku); deactivate.ExecuteNonQuery();
            }
            foreach (var variant in entry.Variants)
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO product_variants(store_id,marketplace,sku,external_id,variant_id,size,barcodes_json,gtin,image_url,raw_json,active,synced_at)
VALUES($s,$m,$sku,$id,$variant,$size,$barcodes,$gtin,$image,$raw,1,$at)
ON CONFLICT(store_id,marketplace,sku,variant_id) DO UPDATE SET external_id=$id,size=$size,barcodes_json=$barcodes,gtin=$gtin,
 image_url=CASE WHEN $image='' THEN product_variants.image_url ELSE $image END,raw_json=$raw,active=1,synced_at=$at";
                cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
                cmd.Parameters.AddWithValue("$sku", p.Sku); cmd.Parameters.AddWithValue("$id", p.ExternalId); cmd.Parameters.AddWithValue("$variant", variant.VariantId);
                cmd.Parameters.AddWithValue("$size", variant.Size); cmd.Parameters.AddWithValue("$barcodes", JsonSerializer.Serialize(variant.Barcodes));
                cmd.Parameters.AddWithValue("$gtin", variant.Gtin); cmd.Parameters.AddWithValue("$image", variant.ImageUrl);
                cmd.Parameters.AddWithValue("$raw", variant.RawJson); cmd.Parameters.AddWithValue("$at", now); cmd.ExecuteNonQuery();
            }
        }
        using (var checkpoint = c.CreateCommand())
        {
            checkpoint.Transaction = tx;
            checkpoint.CommandText = @"UPDATE product_catalog_checkpoint SET cursor=$next,pages=pages+1,products=products+$products,
variants=variants+$variants,complete=$complete,error='',updated_at=$at WHERE store_id=$s AND marketplace=$m AND scope=$scope";
            checkpoint.Parameters.AddWithValue("$next", page.Complete ? "" : page.NextCursor);
            checkpoint.Parameters.AddWithValue("$products", page.Entries.Count); checkpoint.Parameters.AddWithValue("$variants", page.Entries.Sum(e => e.Variants.Count));
            checkpoint.Parameters.AddWithValue("$complete", page.Complete ? 1 : 0); checkpoint.Parameters.AddWithValue("$at", now);
            checkpoint.Parameters.AddWithValue("$s", store.Id); checkpoint.Parameters.AddWithValue("$m", store.Marketplace.ToString());
            checkpoint.Parameters.AddWithValue("$scope", scope); checkpoint.ExecuteNonQuery();
        }
        tx.Commit(); return page.Entries.Count;
    }

    public void FailProductCatalog(StoreProfile store, string error)
    {
        using var c = new SqliteConnection(ConnectionString); c.Open(); EnsureProductCatalogTables(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE product_catalog_checkpoint SET error=$error,updated_at=$at WHERE store_id=$s AND marketplace=$m";
        cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
        cmd.Parameters.AddWithValue("$error", error); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ProductVariantRow> ProductVariants(StoreProfile store)
    {
        var variants = new List<ProductVariantRow>();
        using var c = new SqliteConnection(ConnectionString); c.Open(); EnsureProductCatalogTables(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sku,external_id,variant_id,size,barcodes_json,gtin,image_url,raw_json FROM product_variants WHERE store_id=$s AND marketplace=$m AND active=1 ORDER BY sku,size,variant_id";
        cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) variants.Add(new(store.Id, store.Marketplace, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? Array.Empty<string>(), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return variants;
    }

    public ProductCatalogStatus ProductCatalogStatus(StoreProfile store)
    {
        var variants = ProductVariants(store); var checkpoint = ProductCatalogCheckpoint(store);
        using var c = new SqliteConnection(ConnectionString); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT error FROM product_catalog_checkpoint WHERE store_id=$s AND marketplace=$m";
        cmd.Parameters.AddWithValue("$s", store.Id); cmd.Parameters.AddWithValue("$m", store.Marketplace.ToString());
        var error = cmd.ExecuteScalar()?.ToString() ?? "";
        return new(Products(store.Id).Count, variants.Count, variants.Count(v => v.Gtin.Length == 0), checkpoint?.Pages ?? 0,
            checkpoint is { Complete: false } && checkpoint.Scope == ProductCatalog.Scope(store), error);
    }
}
