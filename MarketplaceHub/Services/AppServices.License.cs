using MarketplaceHub.Core;

namespace MarketplaceHub.Services;

public sealed record StoreCreationResult(bool Success, string Message, StoreProfile? Store = null);

public sealed partial class AppServices
{
    private readonly SemaphoreSlim storeCreationGate = new(1, 1);

    public async Task<StoreCreationResult> CreateStoreAsync(StoreProfile store, CancellationToken ct = default)
    {
        if (store.Id != 0) return new StoreCreationResult(false, "Chỉ cửa hàng mới dùng luồng tạo có license.");
        await storeCreationGate.WaitAsync(ct);
        try
        {
            var stores = Db.Stores();
            var decision = await License.ValidateBeforeCreateStoreAsync(
                store.Marketplace,
                stores.Count,
                stores.Count(x => x.Marketplace == store.Marketplace),
                ct);
            if (!decision.Allowed) return new StoreCreationResult(false, decision.Message);
            var saved = Db.SaveStore(store);
            return new StoreCreationResult(true, "Đã lưu cửa hàng.", saved);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new StoreCreationResult(false, "Đã hủy xác thực license; cửa hàng chưa được lưu.");
        }
        catch (Exception ex)
        {
            return new StoreCreationResult(false, "Không thể lưu cửa hàng: " + ex.Message);
        }
        finally { storeCreationGate.Release(); }
    }
}
