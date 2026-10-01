namespace MarketplaceHub.Core;

public enum Marketplace { Wildberries, Ozon, Yandex }

public sealed record StoreProfile(
    long Id, Marketplace Marketplace, string Name, string ClientId, string ApiKey,
    string BusinessId, string CampaignId, string Token, bool Enabled);

public sealed record ProductRow(
    long StoreId, Marketplace Marketplace, string ExternalId, string Sku, string Name,
    decimal? Price, string ImageUrl, string RawJson);

public sealed record FbsOrderRow(
    long StoreId, Marketplace Marketplace, string ExternalOrderId, string Sku,
    string Name, int Quantity, string Status, bool NeedsKiz, string RawJson);

public sealed record PriceUpdateResult(bool Success, string Message, string? ExternalTaskId = null);
public sealed record ApiTestResult(bool Success, string Message);
public sealed record LabelResult(bool Success, string Message, string? FilePath = null,
    string? Barcode = null, string? PartA = null, string? PartB = null);
public sealed record WbPrintKizMetadata(bool Required,IReadOnlyList<string> Codes);

public sealed record ZnakConfig(
    string Inn,
    string Environment,
    string CertificateThumbprint,
    string CertificateSubject,
    string AutoSignMode,
    bool Enabled,
    string OmsId,
    string OmsConnection,
    bool AutoCirculation);

public sealed record CertificateInfo(
    string Subject,
    string Thumbprint,
    DateTime NotAfter,
    bool HasPrivateKey,
    string OwnerName,
    string Inn);

public sealed record SyncRunRow(
    long Id,
    long StoreId,
    string Stream,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Success,
    int ReadCount,
    int WrittenCount,
    string Error);

public sealed record FboSupplyRow(
    long StoreId,
    Marketplace Marketplace,
    string OrderId,
    string SupplyId,
    string Status,
    string Warehouse,
    string PlannedAt,
    int TotalQuantity,
    int AcceptedQuantity,
    string RawJson);

public sealed record FinanceSnapshot(
    string Currency,
    decimal Revenue,
    decimal Payout,
    decimal Delivery,
    decimal Storage,
    decimal Acceptance,
    decimal Deductions,
    decimal Penalties,
    decimal AdditionalPayments,
    decimal Cashback,
    int ReportCount,
    string From,
    string To);

public sealed record ZnakPipelineRow(
    long Id,
    long StoreId,
    string Sku,
    string Gtin,
    string Stage,
    string ExternalOrderId,
    string Detail,
    DateTimeOffset UpdatedAt);

public sealed record AuditRow(DateTimeOffset At, string Module, string Action, string Detail);
