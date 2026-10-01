using MarketplaceHub.Core;

namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    public Task<(bool Ok, string Message)> RefreshProductCatalogAsync(StoreProfile store, CancellationToken ct = default,
        IProgress<string>? progress = null) => RunStoreSyncAsync(store, () => SyncProductsResumableCoreAsync(store, ct, progress), ct);

    private async Task<(bool Ok, string Message)> SyncProductsResumableCoreAsync(StoreProfile store, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var run = Db.StartSyncRun(store.Id, "products");
        var read = 0; var written = 0;
        try
        {
            var scope = ProductCatalog.Scope(store);
            var checkpoint = Db.ProductCatalogCheckpoint(store);
            if (checkpoint is null || checkpoint.Complete || checkpoint.Scope != scope)
            {
                Db.BeginProductCatalog(store, scope);
                checkpoint = Db.ProductCatalogCheckpoint(store)!;
            }
            var cursor = checkpoint.Cursor;
            var seen = new HashSet<string>(StringComparer.Ordinal) { cursor };
            progress?.Report(checkpoint.Pages > 0 ? $"Tiếp tục sau {checkpoint.Pages} trang đã lưu." : "Đang đọc catalog…");
            for (var page = checkpoint.Pages; page < 100000; page++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = await Api.ReadProductCatalogPageAsync(store, cursor, ct, progress).ConfigureAwait(false);
                if (!batch.Complete && !seen.Add(batch.NextCursor)) throw new InvalidDataException("Catalog lặp cursor; đã tạm dừng.");
                read += batch.Entries.Count;
                written += await Task.Run(() => Db.ApplyProductCatalogPage(store, cursor, scope, batch), ct).ConfigureAwait(false);
                var current = Db.ProductCatalogCheckpoint(store)!;
                progress?.Report($"Đã lưu {current.Products} card / {current.Variants} biến thể · trang {current.Pages}.");
                if (batch.Complete)
                {
                    Db.SaveSyncState(store.Id, "products", "", "", "", "");
                    Db.FinishSyncRun(run, true, read, written);
                    var status = Db.ProductCatalogStatus(store);
                    Db.Audit("Products", "Sync", $"{store.Marketplace}:{store.Id}:{current.Products}:{current.Variants}");
                    return (true, $"Đã đồng bộ {current.Products} card, {current.Variants} biến thể qua {current.Pages} trang. " +
                        $"{status.MissingGtin} biến thể thiếu GTIN hợp lệ; barcode và ánh xạ hiện có được giữ lại.");
                }
                cursor = batch.NextCursor;
            }
            throw new InvalidDataException("Catalog vượt giới hạn an toàn; bấm Tiếp tục để đọc tiếp từ trang đã lưu.");
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Đã tạm dừng đồng bộ." : ex.Message;
            Db.FailProductCatalog(store, message);
            var checkpoint = Db.ProductCatalogCheckpoint(store);
            Db.SaveSyncState(store.Id, "products", checkpoint?.Cursor ?? "", "", "", message);
            Db.FinishSyncRun(run, false, read, written, message);
            return (false, message + " Dữ liệu đã lưu vẫn còn; lần đồng bộ sau tiếp tục tại trang chưa hoàn tất.");
        }
    }
}
