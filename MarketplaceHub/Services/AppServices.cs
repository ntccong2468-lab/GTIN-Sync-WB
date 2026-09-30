using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Services;

public sealed class AppServices
{
    public AppDatabase Db { get; } = new();
    public MarketplaceGateway Api { get; } = new();

    public async Task<(bool Ok, string Message)> SyncProductsAsync(StoreProfile store, CancellationToken ct = default)
    {
        var run = Db.StartSyncRun(store.Id, "products");
        try
        {
            var rows = await Api.SyncProductsAsync(store, ct);
            Db.ReplaceProducts(store.Id, store.Marketplace, rows);
            Db.SaveSyncState(store.Id, "products", "", "", "", "");
            Db.FinishSyncRun(run, true, rows.Count, rows.Count);
            return (true, $"Đã đồng bộ {rows.Count} sản phẩm.");
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "products", "", "", "", ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> SyncOrdersAsync(StoreProfile store, CancellationToken ct = default)
    {
        var run = Db.StartSyncRun(store.Id, "fbs_orders");
        try
        {
            var rows = await Api.SyncFbsAsync(store, ct);
            var written = Db.UpsertOrders(store.Id, store.Marketplace, rows);
            Db.SaveSyncState(store.Id, "fbs_orders", "", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"), DateTimeOffset.UtcNow.ToString("O"), "");
            Db.FinishSyncRun(run, true, rows.Count, written);
            return (true, $"Đã đồng bộ {written} dòng đơn FBS và giữ lại trạng thái lịch sử trong cache.");
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "fbs_orders", "", "", "", ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> SyncFboSuppliesAsync(StoreProfile store, CancellationToken ct = default)
    {
        var run = Db.StartSyncRun(store.Id, "fbo_supplies");
        try
        {
            var rows = await Api.SyncFboSuppliesAsync(store, ct);
            Db.ReplaceFboSupplies(store.Id, store.Marketplace, rows);
            Db.SaveSyncState(store.Id, "fbo_supplies", "", "", "", "");
            Db.FinishSyncRun(run, true, rows.Count, rows.Count);
            return (true, $"Đã đồng bộ {rows.Count} yêu cầu nhập kho FBO/FBW.");
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "fbo_supplies", "", "", "", ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message, FinanceSnapshot? Snapshot)> ReadFinanceAsync(
        StoreProfile store, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var run = Db.StartSyncRun(store.Id, "finance");
        try
        {
            var snapshot = await Api.ReadFinanceAsync(store, from, to, ct);
            Db.SaveSyncState(store.Id, "finance", "", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), "");
            Db.FinishSyncRun(run, true, snapshot.ReportCount, snapshot.ReportCount);
            return (true, $"Đã đọc {snapshot.ReportCount} báo cáo quyết toán.", snapshot);
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "finance", "", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message, null);
        }
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

    public static IReadOnlyList<CertificateInfo> Certificates()
    {
        var result = new List<CertificateInfo>();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var cert in store.Certificates)
        {
            var subject = cert.Subject ?? "";
            var owner = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(owner)) owner = SubjectValue(subject, "CN");
            if (string.IsNullOrWhiteSpace(owner)) owner = subject;
            result.Add(new CertificateInfo(
                subject,
                cert.Thumbprint ?? "",
                cert.NotAfter,
                cert.HasPrivateKey,
                owner,
                ExtractInn(subject)));
        }
        return result.OrderByDescending(x => x.NotAfter).ToList();
    }

    internal static string ExtractInn(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        var labels = new[]
        {
            "INN FL", "ИНН ФЛ", "ИНН ЮЛ", "INN", "ИНН",
            "OID.1.2.643.3.131.1.1", "1.2.643.3.131.1.1"
        };

        foreach (var label in labels)
        {
            var pattern = $@"(?:^|[,\r\n])\s*{Regex.Escape(label)}\s*=\s*(?<v>[^,\r\n]+)";
            var m = Regex.Match(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) continue;
            var value = DecodeDnValue(m.Groups["v"].Value.Trim());
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length is 10 or 12) return digits;
        }

        var fallback = Regex.Match(subject, @"(?<!\d)(\d{10}|\d{12})(?!\d)");
        return fallback.Success ? fallback.Groups[1].Value : "";
    }

    private static string SubjectValue(string subject, string label)
    {
        var pattern = $@"(?:^|[,\r\n])\s*{Regex.Escape(label)}\s*=\s*(?<v>[^,\r\n]+)";
        var m = Regex.Match(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? DecodeDnValue(m.Groups["v"].Value.Trim()) : "";
    }

    private static string DecodeDnValue(string value)
    {
        value = value.Trim().Trim('"');
        var hex = value.StartsWith("#", StringComparison.Ordinal) ? value[1..] : value;
        if (hex.Length >= 4 && hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
        {
            try
            {
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length > 2 && bytes[0] is 0x0C or 0x13 or 0x16)
                {
                    var length = bytes[1];
                    if (length > 0 && length <= bytes.Length - 2)
                        bytes = bytes.Skip(2).Take(length).ToArray();
                }
                var decoded = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(decoded)) return decoded;
            }
            catch { }
        }
        return value;
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
                var parsed = ParseKiz("014601234567890221SELFTEST");
                return parsed.Ok && parsed.Gtin == "46012345678902";
            });

            Check("06. Từ chối DataMatrix/KIZ không hợp lệ", () => !ParseKiz("INVALID-CODE").Ok);

            Check("07. Lưu/đọc cấu hình Znack và khôi phục cấu hình cũ", () =>
            {
                originalZnak = db.GetZnakConfig();
                var testConfig = new ZnakConfig(
                    "7701234567", "Test", "SELFTEST-THUMBPRINT", "CN=MarketplaceHub SelfTest",
                    "Thủ công", true, "SELFTEST-OMS", "SELFTEST-CONNECTION", true);
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

            Check("09. Sync run/state được lưu và đọc lại", () =>
            {
                if (createdStore is null) return false;
                var run = db.StartSyncRun(createdStore.Id, "selftest");
                db.SaveSyncState(createdStore.Id, "selftest", "cursor-1", "from", "to", "");
                db.FinishSyncRun(run, true, 2, 2);
                var state = db.SyncState(createdStore.Id, "selftest");
                var latest = db.SyncRuns(createdStore.Id, 10).FirstOrDefault(x => x.Id == run);
                return state.Cursor == "cursor-1" && latest is not null && latest.Success && latest.WrittenCount == 2;
            });

            Check("10. FBO cache và Znack pipeline tồn tại qua vòng đọc", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceFboSupplies(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new FboSupplyRow(createdStore.Id, Marketplace.Wildberries, "PRE-1", "SUP-1", "READY", "Kho kiểm thử", "2026-09-30", 10, 2, "{}")
                });
                db.UpsertZnakPipeline(createdStore.Id, "SKU-SELFTEST", "46012345678902", "QUEUED", "", "selftest");
                return db.FboSupplies(createdStore.Id).Any(x => x.OrderId == "PRE-1")
                       && db.ZnakPipelines(createdStore.Id).Any(x => x.Sku == "SKU-SELFTEST" && x.Stage == "QUEUED");
            });

            Check("11. Đọc kho chứng thư Windows không làm ứng dụng lỗi", () =>
            {
                _ = Certificates();
                return true;
            });

            Check("12. Parser INN chứng thư nhận nhãn Nga/OID", () =>
            {
                return ExtractInn("CN=Тест, ИНН ФЛ=123456789012")
                       == "123456789012"
                       && ExtractInn("CN=Test, OID.1.2.643.3.131.1.1=7701234567")
                       == "7701234567";
            });

            Check("13. Xóa cửa hàng và toàn bộ dữ liệu liên quan", () =>
            {
                if (createdStore is null) return false;
                var id = createdStore.Id;
                db.DeleteStore(id);
                createdStore = null;
                return db.Stores().All(x => x.Id != id)
                       && db.Products(id).Count == 0
                       && db.Orders(id).Count == 0
                       && db.FboSupplies(id).Count == 0
                       && db.ZnakPipelines(id).Count == 0
                       && db.SyncRuns(id, 10).Count == 0;
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
