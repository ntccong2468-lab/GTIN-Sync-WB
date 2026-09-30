using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using System.Security.Cryptography.X509Certificates;

namespace MarketplaceHub.Services;

public sealed class AppServices
{
    public AppDatabase Db { get; } = new();
    public MarketplaceGateway Api { get; } = new();

    public async Task<(bool Ok, string Message)> SyncProductsAsync(StoreProfile store, CancellationToken ct = default)
    {
        try
        {
            var rows = await Api.SyncProductsAsync(store, ct);
            Db.ReplaceProducts(store.Id, store.Marketplace, rows);
            return (true, $"Đã đồng bộ {rows.Count} sản phẩm.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string Message)> SyncOrdersAsync(StoreProfile store, CancellationToken ct = default)
    {
        try
        {
            var rows = await Api.SyncFbsAsync(store, ct);
            Db.ReplaceOrders(store.Id, store.Marketplace, rows);
            return (true, $"Đã đồng bộ {rows.Count} dòng đơn FBS.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<PriceUpdateResult> ChangePriceAsync(StoreProfile store, ProductRow product, decimal price, CancellationToken ct = default)
    {
        var result = await Api.UpdatePriceAsync(store, product, price, ct);
        Db.AddPriceHistory(store.Id, product.Sku, product.Price, price, result.Message);
        Db.Audit("Price", result.Success ? "Accepted" : "Failed", $"{store.Marketplace}:{product.Sku}:{price}:{result.Message}");
        return result;
    }

    public static (bool Ok, string Gtin, string Message) ParseKiz(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, "", "Mã KIZ/DataMatrix trống.");
        var s = code.Trim();
        var index = s.IndexOf("01", StringComparison.Ordinal);
        if (index < 0 || s.Length < index + 16) return (false, "", "Không tìm thấy AI(01) + GTIN-14.");
        var gtin = s.Substring(index + 2, 14);
        if (!gtin.All(char.IsDigit)) return (false, "", "GTIN trong DataMatrix không hợp lệ.");
        return (true, gtin, "Cấu trúc mã hợp lệ. Trạng thái pháp lý cần kiểm tra qua True API.");
    }

    public static IReadOnlyList<(string Subject, string Thumbprint, DateTime NotAfter, bool HasPrivateKey)> Certificates()
    {
        var result = new List<(string, string, DateTime, bool)>();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var cert in store.Certificates)
            result.Add((cert.Subject, cert.Thumbprint, cert.NotAfter, cert.HasPrivateKey));
        return result.OrderByDescending(x => x.Item3).ToList();
    }

    public static bool SelfTest()
    {
        var report = new List<string>
        {
            $"Marketplace Hub E2E self-test - {DateTimeOffset.Now:O}"
        };
        var allOk = true;
        AppDatabase? db = null;
        StoreProfile? createdStore = null;
        ZnakConfig? originalZnak = null;

        void Check(string name, Func<bool> test)
        {
            try
            {
                var ok = test();
                report.Add($"{(ok ? "[PASS]" : "[FAIL]")} {name}");
                if (!ok) allOk = false;
            }
            catch (Exception ex)
            {
                allOk = false;
                report.Add($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            db = new AppDatabase();

            Check("01. Khởi tạo SQLite và migration", () => File.Exists(db.DbPath));

            var storeName = $"__SELFTEST_{Guid.NewGuid():N}";
            Check("02. Lưu/đọc cửa hàng + DPAPI bảo vệ thông tin xác thực", () =>
            {
                createdStore = db.SaveStore(new StoreProfile(
                    0, Marketplace.Wildberries, storeName, "",
                    "selftest-api-key", "", "", "selftest-token", true));
                var loaded = db.Stores().Single(x => x.Id == createdStore.Id);
                return loaded.Name == storeName
                       && loaded.Marketplace == Marketplace.Wildberries
                       && loaded.ApiKey == "selftest-api-key"
                       && loaded.Token == "selftest-token"
                       && loaded.Enabled;
            });

            Check("03. Đồng bộ giả lập sản phẩm -> SQLite -> đọc lại", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceProducts(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new ProductRow(createdStore.Id, Marketplace.Wildberries, "123456789",
                        "SKU-SELFTEST", "Sản phẩm kiểm thử", 12345m, "", "{\"source\":\"selftest\"}")
                });
                var p = db.Product(createdStore.Id, "SKU-SELFTEST");
                return p is not null
                       && p.ExternalId == "123456789"
                       && p.Price == 12345m
                       && p.Name == "Sản phẩm kiểm thử";
            });

            Check("04. Đồng bộ giả lập đơn FBS -> SQLite -> đọc lại", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceOrders(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new FbsOrderRow(createdStore.Id, Marketplace.Wildberries, "900000001",
                        "SKU-SELFTEST", "Sản phẩm kiểm thử", 1, "new", true, "{\"source\":\"selftest\"}")
                });
                var o = db.Orders(createdStore.Id).SingleOrDefault(x => x.ExternalOrderId == "900000001");
                return o is not null && o.Sku == "SKU-SELFTEST" && o.NeedsKiz && o.Status == "new";
            });

            Check("05. Phân tích DataMatrix/KIZ hợp lệ", () =>
            {
                var parsed = ParseKiz("010460123456789021SELFTEST");
                return parsed.Ok && parsed.Gtin == "46012345678902";
            });

            Check("06. Từ chối DataMatrix/KIZ không hợp lệ", () => !ParseKiz("INVALID-CODE").Ok);

            Check("07. Lưu/đọc cấu hình Znack và khôi phục cấu hình cũ", () =>
            {
                originalZnak = db.GetZnakConfig();
                var testConfig = new ZnakConfig(
                    "7701234567", "Test", "SELFTEST-THUMBPRINT", "CN=MarketplaceHub SelfTest",
                    "Thủ công", true, "SELFTEST-OMS", "SELFTEST-CONNECTION", false);
                db.SaveZnakConfig(testConfig);
                var loaded = db.GetZnakConfig();
                var ok = loaded == testConfig;
                db.SaveZnakConfig(originalZnak);
                originalZnak = null;
                return ok;
            });

            Check("08. Ghi và đọc nhật ký audit", () =>
            {
                var marker = Guid.NewGuid().ToString("N");
                db.Audit("SelfTest", "E2E", marker);
                return db.AuditRows(100).Any(x => x.Module == "SelfTest" && x.Detail == marker);
            });

            Check("09. Xóa cửa hàng và dữ liệu sản phẩm/FBS liên quan", () =>
            {
                if (createdStore is null) return false;
                var id = createdStore.Id;
                db.DeleteStore(id);
                createdStore = null;
                return db.Stores().All(x => x.Id != id)
                       && db.Products(id).Count == 0
                       && db.Orders(id).Count == 0;
            });

            Check("10. Đọc kho chứng thư Windows không làm ứng dụng lỗi", () =>
            {
                _ = Certificates();
                return true;
            });
        }
        finally
        {
            if (db is not null && originalZnak is not null)
            {
                try { db.SaveZnakConfig(originalZnak); }
                catch (Exception ex) { allOk = false; report.Add($"[FAIL] Khôi phục cấu hình Znack: {ex.Message}"); }
            }

            if (db is not null && createdStore is not null)
            {
                try { db.DeleteStore(createdStore.Id); }
                catch (Exception ex) { allOk = false; report.Add($"[FAIL] Dọn dữ liệu self-test: {ex.Message}"); }
            }

            report.Add(allOk ? "KẾT QUẢ: PASS" : "KẾT QUẢ: FAIL");
            try
            {
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "MarketplaceHub-self-test-report.txt"), report);
            }
            catch { }
        }

        return allOk;
    }
}
