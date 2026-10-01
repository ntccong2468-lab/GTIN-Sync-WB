using MarketplaceHub.Core;

namespace MarketplaceHub.Services;

public sealed class OrderTruthService
{
    public OrderTruthSnapshot Build(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> rows,
        IReadOnlySet<string> shipmentMembers,
        IReadOnlyDictionary<string, OrderRemoteState> remoteStates)
    {
        var result = new List<OrderTruthRow>();
        foreach (var group in rows
                     .Where(x => x.StoreId == store.Id && x.Marketplace == store.Marketplace)
                     .GroupBy(x => x.ExternalOrderId, StringComparer.Ordinal)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var lines = group.ToArray();
            var hasRemote = remoteStates.TryGetValue(group.Key, out var remote);
            var authoritative = store.Marketplace != Marketplace.Wildberries || hasRemote && remote!.Complete;
            var state = Classify(store.Marketplace, lines, shipmentMembers.Contains(group.Key), remote, authoritative);
            result.Add(new(group.Key, store.Marketplace, state, lines, authoritative, hasRemote ? remote!.ObservedAt : null));
        }

        var observed = result.Where(x => x.ObservedAt.HasValue).Select(x => x.ObservedAt!.Value).DefaultIfEmpty().Min();
        return new(result, result.All(x => x.IsAuthoritative), observed == default ? null : observed);
    }

    private static OrderTruthState Classify(
        Marketplace marketplace,
        IReadOnlyList<FbsOrderRow> lines,
        bool isMember,
        OrderRemoteState? remote,
        bool authoritative)
    {
        var local = lines.Select(x => x.Status).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        var supplier = remote?.SupplierStatus ?? local;
        var platform = remote?.MarketplaceStatus ?? "";

        if (IsCancelled(supplier) || IsCancelled(platform) || lines.Any(x => IsCancelled(x.Status)))
            return OrderTruthState.Cancelled;

        if (marketplace == Marketplace.Wildberries && !authoritative)
            return OrderTruthState.Unknown;

        if (supplier.Equals("complete", StringComparison.OrdinalIgnoreCase))
            return OrderTruthState.Shipping;
        if (IsCompleted(supplier) || IsCompleted(platform))
            return OrderTruthState.Completed;
        if (isMember)
            return OrderTruthState.InShipment;
        if (IsShipping(supplier) || IsShipping(platform))
            return OrderTruthState.Shipping;
        if (IsPacking(supplier) || IsPacking(platform))
            return OrderTruthState.Packing;
        if (IsNew(supplier))
            return OrderTruthState.New;
        return OrderTruthState.Unknown;
    }

    private static bool IsNew(string value) =>
        value.Equals("new", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("awaiting_packaging", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PROCESSING/STARTED", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PROCESSING/CONFIRMED", StringComparison.OrdinalIgnoreCase);

    private static bool IsPacking(string value) =>
        new[] { "confirm", "assembling", "awaiting_deliver", "PROCESSING/PACKING", "PROCESSING/READY_FOR_DELIVERY", "PROCESSING/READY_TO_SHIP" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsShipping(string value) =>
        value.Equals("delivering", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("deliver", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("DELIVERY/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("PICKUP/", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleted(string value) =>
        value.Equals("delivered", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelled(string value) =>
        value.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("reject", StringComparison.OrdinalIgnoreCase);
}
