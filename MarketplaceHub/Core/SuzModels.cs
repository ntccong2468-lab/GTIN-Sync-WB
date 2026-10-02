namespace MarketplaceHub.Core;
public enum PurchaseStage { Draft, Validated, CreateSending, CreateUnknown, Polling, Downloading, DownloadUnknown, Recovering, AwaitingLegalState, CodesRecovered, Rejected, NeedsReconciliation }
public enum SuzOutcomeKind { Confirmed, Rejected, Unknown, Pending, Unsupported }
public sealed record SuzProfile(string Id, long StoreId, int Version, string OwnerInn, string Environment, string ProductGroup, string ReleaseMethodType, string CisType, int TemplateId, string CredentialVersion, bool AutoPurchaseEnabled, bool ContractEnabled, Uri SuzBaseUri, Uri TrueApiBaseUri);
public sealed record PurchaseIntentRequest(string JobId, int Revision, SuzProfile Profile, string Gtin, int Quantity, string PayloadHash);
public sealed record PurchaseIntent(string Id, PurchaseIntentRequest Request, string RequestKey, PurchaseStage Stage, string? RemoteOrderId, DateTimeOffset? RetryAt, string? ErrorCode);
public sealed record SuzOutcome<T>(SuzOutcomeKind Kind, T? Value, string? ErrorCode = null, DateTimeOffset? RetryAt = null)
{
    public override string ToString() => $"SUZ {Kind} {ErrorCode}";
}
public sealed record SuzOrderReceipt(string OrderId);
public sealed record SuzOrderStatus(string OrderId, string State, int AvailableCodes, string? RejectionCode);
public sealed record SuzOrderCandidate(string OrderId, string OwnerInn, string Environment, string Gtin, int Quantity, string? CorrelationProof);
public sealed record SuzBlock(string BlockId, string OrderId, string Gtin, IReadOnlyList<string> Codes)
{
    public override string ToString() => $"SUZ block {BlockId}: {Codes.Count} codes";
}
public sealed record PurchaseResult(string IntentId, PurchaseStage Stage, int RecoveredCount, string? RemoteOrderId, DateTimeOffset? RetryAt, string? ErrorCode);
public interface IWorkflowClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}
public sealed class WorkflowClock : IWorkflowClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
