using MarketplaceHub.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed partial class MarketplaceGateway
{
    public static DateOnly WbBusinessDay(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(3)).DateTime);

    public async Task<IReadOnlyList<WbSupply>> GetWbSuppliesAsync(StoreProfile store,CancellationToken ct=default)
    {
        RequireWbStore(store);var found=new Dictionary<string,WbSupply>(StringComparer.Ordinal);var cursors=new HashSet<long>();long next=0;
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            for(var page=0;page<200;page++) {
                if(!cursors.Add(next))throw new InvalidDataException("WB lặp cursor shipment. Hãy tải lại danh sách.");
                var body=await WbMarketplaceRequestAsync(store,HttpMethod.Get,$"/api/v3/supplies?limit=1000&next={next}",null,ct,null).ConfigureAwait(false);
                var root=JsonNode.Parse(body);var supplies=root?["supplies"] as JsonArray??throw new InvalidDataException("WB không trả danh sách shipment hợp lệ.");
                foreach(var row in supplies){var supply=ParseWbSupply(row);found[supply.Id]=supply;}
                if(supplies.Count==0)return found.Values.OrderByDescending(x=>x.CreatedAt).ToArray();
                if(!long.TryParse(root?["next"]?.ToString(),out next))throw new InvalidDataException("WB thiếu cursor shipment.");
                if(next==0)return found.Values.OrderByDescending(x=>x.CreatedAt).ToArray();
            }
            throw new InvalidDataException("Danh sách shipment WB quá dài; chưa tải đủ.");
        } finally {wbLabelGate.Release();}
    }

    public async Task<IReadOnlyList<WbSupply>> GetWbTodaySuppliesAsync(StoreProfile store, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        RequireWbStore(store);
        var day = WbBusinessDay(now ?? DateTimeOffset.UtcNow);
        var found = new Dictionary<string, WbSupply>(StringComparer.Ordinal);
        var cursors = new HashSet<long>();
        long next = 0;
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var page = 0; page < 200; page++)
            {
                if (!cursors.Add(next)) throw new InvalidDataException("WB lặp cursor shipment. Hãy tải lại danh sách.");
                var body = await WbMarketplaceRequestAsync(store, HttpMethod.Get, $"/api/v3/supplies?limit=1000&next={next}", null, ct, null).ConfigureAwait(false);
                var root = JsonNode.Parse(body);
                var supplies = root?["supplies"] as JsonArray ?? throw new InvalidDataException("WB không trả danh sách shipment hợp lệ.");
                foreach (var row in supplies)
                {
                    var supply = ParseWbSupply(row);
                    if (!supply.Done && WbBusinessDay(supply.CreatedAt) == day) found[supply.Id] = supply;
                }
                if (supplies.Count == 0) return found.Values.OrderByDescending(x => x.CreatedAt).ToArray();
                if (!long.TryParse(root?["next"]?.ToString(), out next)) throw new InvalidDataException("WB thiếu cursor shipment.");
                if (next == 0) return found.Values.OrderByDescending(x => x.CreatedAt).ToArray();
            }
            throw new InvalidDataException("Danh sách shipment WB quá dài; chưa tải đủ, không dùng kết quả một phần.");
        }
        finally { wbLabelGate.Release(); }
    }

    public async Task<WbSupply> GetWbSupplyAsync(StoreProfile store, string supplyId, CancellationToken ct = default)
    {
        RequireWbStore(store); ValidateWbSupplyId(supplyId);
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadWbSupplyCoreAsync(store, supplyId, ct).ConfigureAwait(false); }
        finally { wbLabelGate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetWbSupplyOrderIdsAsync(StoreProfile store, string supplyId, CancellationToken ct = default)
    {
        RequireWbStore(store); ValidateWbSupplyId(supplyId);
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadWbSupplyIdsCoreAsync(store, supplyId, ct).ConfigureAwait(false); }
        finally { wbLabelGate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, WbOrderStatus>> GetWbOrderStatusesAsync(StoreProfile store, IEnumerable<string> orderIds, CancellationToken ct = default)
    {
        RequireWbStore(store);
        var ids = ValidWbOrderIds(orderIds);
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadWbStatusesCoreAsync(store, ids, ct).ConfigureAwait(false); }
        finally { wbLabelGate.Release(); }
    }

    private async Task<PriceUpdateResult> CreateWbShipmentAsync(StoreProfile store, IReadOnlyList<FbsOrderRow> orders,
        string? existingSupplyId, string? name, CancellationToken ct, IProgress<string>? progress)
    {
        var result = await ReceiveWbShipmentAsync(store, orders, existingSupplyId, name, ct, progress).ConfigureAwait(false);
        return new(result.Success, result.Message, result.SupplyId);
    }

    public async Task<WbReceiveResult> ReceiveWbShipmentAsync(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> orders,
        string? existingSupplyId = null,
        string? name = null,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        var selected = orders.GroupBy(x => x.ExternalOrderId, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var outcomes = selected.ToDictionary(x => x.ExternalOrderId,
            x => new WbReceiveOrderResult(x.ExternalOrderId, WbReceiveDisposition.Rejected, false, "Chưa kiểm tra trạng thái WB."),
            StringComparer.Ordinal);
        string? supplyId = null;
        var created = false;
        var creating = false;
        if (selected.Length == 0)
            return new(false, null, false, 0, Array.Empty<WbReceiveOrderResult>(), "Chưa chọn đơn hàng.");
        if (store.Marketplace != Marketplace.Wildberries)
            return new(false, null, false, 0, outcomes.Values.ToArray(), "Chỉ nhận shipment cho cửa hàng Wildberries.");

        var candidateIds = new List<string>();
        foreach (var order in selected)
        {
            if (order.StoreId != store.Id || order.Marketplace != Marketplace.Wildberries)
            {
                outcomes[order.ExternalOrderId] = outcomes[order.ExternalOrderId] with { Message = "Đơn không thuộc cửa hàng WB đang chọn." };
                continue;
            }
            if (!long.TryParse(order.ExternalOrderId, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0 || value.ToString(CultureInfo.InvariantCulture) != order.ExternalOrderId)
            {
                outcomes[order.ExternalOrderId] = outcomes[order.ExternalOrderId] with { Message = "ID đơn WB không hợp lệ." };
                continue;
            }
            candidateIds.Add(order.ExternalOrderId);
        }

        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> members = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(existingSupplyId))
            {
                ValidateWbSupplyId(existingSupplyId);
                var supply = await ReadWbSupplyCoreAsync(store, existingSupplyId, ct).ConfigureAwait(false);
                if (supply.Done || WbBusinessDay(supply.CreatedAt) != WbBusinessDay(DateTimeOffset.UtcNow))
                    return new(false, null, false, 0, OrderedOutcomes(selected, outcomes), "Chỉ thêm vào shipment đang mở, được tạo hôm nay theo giờ Moscow.");
                supplyId = supply.Id;
                members = await ReadWbSupplyIdsCoreAsync(store, supplyId, ct).ConfigureAwait(false);
            }

            var statuses = await ReadWbReceiveStatusesCoreAsync(store, candidateIds.ToArray(), ct).ConfigureAwait(false);
            foreach (var id in candidateIds)
            {
                if (!statuses.TryGetValue(id, out var status))
                {
                    outcomes[id] = outcomes[id] with { Message = "WB không trả trạng thái duy nhất cho đơn này." };
                    continue;
                }
                if (status.WbStatus.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                    || status.SupplierStatus.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    outcomes[id] = new(id, WbReceiveDisposition.Cancelled, false, $"Khách đã hủy · {status.SupplierStatus}/{status.WbStatus}");
                    continue;
                }
                if (status.SupplierStatus.Equals("new", StringComparison.OrdinalIgnoreCase))
                {
                    outcomes[id] = new(id, WbReceiveDisposition.EligibleNew, false, "Đủ điều kiện thêm vào shipment.");
                    continue;
                }
                if (status.SupplierStatus.Equals("confirm", StringComparison.OrdinalIgnoreCase)
                    && supplyId is not null && members.Contains(id))
                {
                    outcomes[id] = new(id, WbReceiveDisposition.AlreadyMember, true, "Đã có trong shipment này.");
                    continue;
                }
                outcomes[id] = outcomes[id] with { Message = status.SupplierStatus.Equals("confirm", StringComparison.OrdinalIgnoreCase)
                    ? "Đơn đã thuộc shipment khác hoặc không thuộc shipment đang chọn."
                    : $"Trạng thái {status.SupplierStatus}/{status.WbStatus} không thể nhận." };
            }

            var eligible = outcomes.Values.Where(x => x.Disposition == WbReceiveDisposition.EligibleNew).Select(x => x.OrderId).ToArray();
            var usable = outcomes.Values.Count(x => x.Disposition is WbReceiveDisposition.EligibleNew or WbReceiveDisposition.AlreadyMember);
            if (usable == 0)
                return new(false, supplyId, false, 0, OrderedOutcomes(selected, outcomes), "Không có đơn hợp lệ để tạo hoặc cập nhật shipment.");

            if (supplyId is null && eligible.Length > 0)
            {
                name = string.IsNullOrWhiteSpace(name) ? "MarketplaceHub " + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm:ss") : name.Trim();
                if (name.Length > 128)
                    return new(false, null, false, 0, OrderedOutcomes(selected, outcomes), "Tên shipment tối đa 128 ký tự.");
                progress?.Report($"Tạo shipment cho {eligible.Length} đơn hợp lệ…");
                creating = true;
                var body = await WbMarketplaceRequestAsync(store, HttpMethod.Post, "/api/v3/supplies", JsonSerializer.Serialize(new { name }), ct, progress).ConfigureAwait(false);
                supplyId = JsonNode.Parse(body)?["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(supplyId)) throw new InvalidDataException("WB đã nhận tạo shipment nhưng thiếu supplyId. Kiểm tra shipment hôm nay trước khi tạo lại.");
                ValidateWbSupplyId(supplyId);
                creating = false; created = true;
            }

            var verified = outcomes.Values.Count(x => x.Verified);
            foreach (var batch in eligible.Where(id => !members.Contains(id)).Chunk(100))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Thêm vào {supplyId}: {verified}/{usable} đơn hợp lệ…");
                await WbMarketplaceRequestAsync(store, new HttpMethod("PATCH"), $"/api/marketplace/v3/supplies/{Uri.EscapeDataString(supplyId)}/orders",
                    JsonSerializer.Serialize(new { orders = batch.Select(long.Parse).ToArray() }), ct, progress).ConfigureAwait(false);
                for (var read = 0; ; read++)
                {
                    members = await ReadWbSupplyIdsCoreAsync(store, supplyId, ct).ConfigureAwait(false);
                    if (batch.All(members.Contains)) break;
                    if (read >= 2) throw new InvalidDataException("WB chưa xác nhận đủ đơn trong shipment. Dừng trước khi gắn KIZ hoặc lấy sticker.");
                    await wbLabelDelay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                }
                foreach (var id in batch.Where(members.Contains))
                    outcomes[id] = outcomes[id] with { Verified = true, Message = "Đã thêm và xác minh trong shipment." };
                verified = outcomes.Values.Count(x => x.Verified);
            }

            var usableIds = outcomes.Values.Where(x => x.Disposition is WbReceiveDisposition.EligibleNew or WbReceiveDisposition.AlreadyMember).Select(x => x.OrderId).ToArray();
            for (var read = 0; ; read++)
            {
                statuses = await ReadWbReceiveStatusesCoreAsync(store, usableIds, ct).ConfigureAwait(false);
                if (usableIds.All(id => statuses.TryGetValue(id, out var state) && state.SupplierStatus == "confirm" && !state.WbStatus.Contains("cancel", StringComparison.OrdinalIgnoreCase))) break;
                if (read >= 2) throw new InvalidDataException("WB chưa xác nhận mọi đơn ở trạng thái confirm. Chưa bắt đầu KIZ/in nhãn.");
                await wbLabelDelay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            members = await ReadWbSupplyIdsCoreAsync(store, supplyId, ct).ConfigureAwait(false);
            foreach (var id in usableIds)
                outcomes[id] = outcomes[id] with { Verified = members.Contains(id), Message = members.Contains(id)
                    ? outcomes[id].Disposition == WbReceiveDisposition.AlreadyMember ? "Đã có trong shipment này." : "Đã thêm và xác minh trong shipment."
                    : "WB chưa xác nhận membership của đơn." };
            verified = outcomes.Values.Count(x => x.Verified);
            if (usableIds.Any(id => !members.Contains(id)))
                throw new InvalidDataException("Shipment không còn đủ mọi đơn đã chọn. Đồng bộ lại membership trước khi gắn KIZ hoặc xuất nhãn.");
            return new(true, supplyId, created, verified, OrderedOutcomes(selected, outcomes),
                $"{(created ? "Đã tạo" : "Đã cập nhật")} shipment {supplyId}. WB xác nhận {verified}/{usable} đơn hợp lệ; {outcomes.Values.Count(x => x.Disposition == WbReceiveDisposition.Cancelled)} đơn đã hủy, {outcomes.Values.Count(x => x.Disposition == WbReceiveDisposition.Rejected)} đơn bị từ chối.");
        }
        catch (Exception ex)
        {
            var verified = outcomes.Values.Count(x => x.Verified);
            var usable = outcomes.Values.Count(x => x.Disposition is WbReceiveDisposition.EligibleNew or WbReceiveDisposition.AlreadyMember);
            var detail = supplyId is null
                ? creating ? "Kết quả tạo shipment chưa rõ. Kiểm tra danh sách shipment hôm nay trước khi tạo lại. " : ""
                : $"Shipment {supplyId} được giữ lại; đã xác nhận {verified}/{usable} đơn hợp lệ. Tiếp tục từ shipment này, không tạo lại. ";
            return new(false, supplyId, created, verified, OrderedOutcomes(selected, outcomes),
                detail + (ex is OperationCanceledException ? "Tác vụ đã dừng; chưa tiếp tục KIZ/nhãn." : ex.Message));
        }
        finally { wbLabelGate.Release(); }
    }

    private static IReadOnlyList<WbReceiveOrderResult> OrderedOutcomes(
        IReadOnlyList<FbsOrderRow> selected,
        IReadOnlyDictionary<string, WbReceiveOrderResult> outcomes) =>
        selected.Select(x => outcomes[x.ExternalOrderId]).ToArray();

    private async Task<IReadOnlyDictionary<string, WbOrderStatus>> ReadWbReceiveStatusesCoreAsync(
        StoreProfile store,
        string[] ids,
        CancellationToken ct)
    {
        var result = new Dictionary<string, WbOrderStatus>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(100))
        {
            var body = await WbLabelRequestAsync(store, "/api/v3/orders/status", batch, ct, null).ConfigureAwait(false);
            var rows = JsonNode.Parse(body)?["orders"] as JsonArray ?? throw new InvalidDataException("WB thiếu danh sách trạng thái đơn.");
            foreach (var id in batch)
            {
                var matches = rows.Where(x => x?["id"]?.ToString() == id).ToArray();
                var supplier = matches.Length == 1 ? matches[0]?["supplierStatus"]?.ToString() : null;
                var wb = matches.Length == 1 ? matches[0]?["wbStatus"]?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(supplier) && !string.IsNullOrWhiteSpace(wb))
                    result[id] = new(supplier, wb);
            }
        }
        return result;
    }

    private async Task<WbSupply> ReadWbSupplyCoreAsync(StoreProfile store, string supplyId, CancellationToken ct)
    {
        var body = await WbMarketplaceRequestAsync(store, HttpMethod.Get, $"/api/v3/supplies/{Uri.EscapeDataString(supplyId)}", null, ct, null).ConfigureAwait(false);
        var supply = ParseWbSupply(JsonNode.Parse(body));
        if (supply.Id != supplyId) throw new InvalidDataException("WB trả shipment khác ID đang chọn.");
        return supply;
    }

    private async Task<IReadOnlyList<string>> ReadWbSupplyIdsCoreAsync(StoreProfile store, string supplyId, CancellationToken ct)
    {
        var body = await WbMarketplaceRequestAsync(store, HttpMethod.Get, $"/api/marketplace/v3/supplies/{Uri.EscapeDataString(supplyId)}/order-ids", null, ct, null).ConfigureAwait(false);
        var ids = JsonNode.Parse(body)?["orderIds"] as JsonArray ?? throw new InvalidDataException("WB thiếu danh sách đơn trong shipment.");
        var result = ValidWbOrderIds(ids.Select(x => x?.ToString() ?? ""));
        if (result.Length != ids.Count) throw new InvalidDataException("WB trả đơn trùng trong shipment.");
        return result;
    }

    private async Task<IReadOnlyDictionary<string, WbOrderStatus>> ReadWbStatusesCoreAsync(StoreProfile store, string[] ids, CancellationToken ct)
    {
        var result = new Dictionary<string, WbOrderStatus>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(100))
        {
            var body = await WbLabelRequestAsync(store, "/api/v3/orders/status", batch, ct, null).ConfigureAwait(false);
            var rows = JsonNode.Parse(body)?["orders"] as JsonArray ?? throw new InvalidDataException("WB thiếu trạng thái đơn.");
            foreach (var id in batch)
            {
                var matches = rows.Where(x => x?["id"]?.ToString() == id).ToArray();
                var supplier = matches.Length == 1 ? matches[0]?["supplierStatus"]?.ToString() : null;
                var wb = matches.Length == 1 ? matches[0]?["wbStatus"]?.ToString() : null;
                if (string.IsNullOrWhiteSpace(supplier) || string.IsNullOrWhiteSpace(wb)) throw new InvalidDataException($"{id}: WB thiếu/trùng trạng thái. Hãy đồng bộ lại.");
                result[id] = new(supplier, wb);
            }
        }
        return result;
    }

    private static WbSupply ParseWbSupply(JsonNode? row)
    {
        var id = row?["id"]?.ToString() ?? "";
        ValidateWbSupplyId(id);
        if (!DateTimeOffset.TryParse(row?["createdAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            || !bool.TryParse(row?["done"]?.ToString(), out var done)) throw new InvalidDataException("WB thiếu ngày/trạng thái shipment; không dùng dữ liệu chưa xác định.");
        return new(id, row?["name"]?.ToString() ?? id, at, done);
    }

    private static string[] ValidWbOrderIds(IEnumerable<string> values)
    {
        var ids = values.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(id => !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value.ToString(CultureInfo.InvariantCulture) != id))
            throw new InvalidOperationException("ID đơn WB phải là số nguyên dương hợp lệ; không bỏ qua đơn lỗi.");
        return ids;
    }
    private static void RequireWbStore(StoreProfile store) { if (store.Marketplace != Marketplace.Wildberries) throw new InvalidOperationException("Shipment này chỉ áp dụng cho WB."); }
    private static void ValidateWbSupplyId(string id) { if (id.Length is < 1 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidOperationException("Supply ID WB không hợp lệ."); }
}
