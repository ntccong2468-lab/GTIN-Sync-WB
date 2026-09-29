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
        var db = new AppDatabase();
        db.Audit("SelfTest", "Database", "OK");
        db.UpsertKiz("010460123456789021TEST", "46012345678902", "AVAILABLE");
        var parsed = ParseKiz("010460123456789021TEST");
        return File.Exists(db.DbPath) && parsed.Ok;
    }
}
