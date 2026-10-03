namespace MarketplaceHub.Core;

public enum OrderTruthState
{
    New,
    InShipment,
    Packing,
    Shipping,
    Completed,
    Cancelled,
    Unknown
}

public sealed record OrderRemoteState(
    string SupplierStatus,
    string MarketplaceStatus,
    bool Complete,
    DateTimeOffset ObservedAt);

public sealed record OrderTruthRow(
    string ExternalOrderId,
    Marketplace Marketplace,
    OrderTruthState State,
    IReadOnlyList<FbsOrderRow> Lines,
    bool IsAuthoritative,
    DateTimeOffset? ObservedAt);

public sealed record OrderTruthSnapshot(
    IReadOnlyList<OrderTruthRow> Orders,
    bool IsAuthoritative,
    DateTimeOffset? ObservedAt)
{
    public int NewCount => Orders.Count(x => x.State == OrderTruthState.New);
    public int PackingCount => Orders.Count(x => x.State is OrderTruthState.InShipment or OrderTruthState.Packing);
    public int ShippingCount => Orders.Count(x => x.State == OrderTruthState.Shipping);
}
