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
public sealed record LabelResult(bool Success, string Message, string? FilePath = null);

public sealed record ZnakConfig(
    string Inn,
    string Environment,
    string CertificateThumbprint,
    string CertificateSubject,
    string AutoSignMode,
    bool Enabled);

public sealed record AuditRow(DateTimeOffset At, string Module, string Action, string Detail);
