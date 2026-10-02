using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using System.Collections.Concurrent;
namespace MarketplaceHub.Services.Suz;
public sealed class KizPurchaseCoordinator(AppDatabase db, ISuzClient suz, IWorkflowClock clock)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private SemaphoreSlim Gate(SuzProfile profile, string gtin) => Gates.GetOrAdd(db.DbPath + "\0" + profile.OwnerInn + "\0" + profile.Environment + "\0" + gtin, _ => new(1, 1));
    public async Task<PurchaseResult> EnsureAsync(PurchaseIntentRequest request, CancellationToken ct)
    {
        if (request.Quantity < 0) throw new ArgumentOutOfRangeException(nameof(request.Quantity));
        if (request.Quantity == 0) return new("", PurchaseStage.CodesRecovered, 0, null, null, null);
        var gate = Gate(request.Profile, request.Gtin); await gate.WaitAsync(ct);
        try
        {
            var existing = db.PurchaseForJob(request);
            if (existing is not null)
            {
                // Repository enforces the immutable request even when resuming a paid order.
                db.GetOrCreatePurchaseIntent(request); return await ResumeCore(existing.Id, ct, WorkflowRunMode.UserRequested);
            }
            if (db.BlockingPurchase(request.Profile, request.Gtin) is not null) return new("", PurchaseStage.NeedsReconciliation, 0, null, null, "scope_purchase_pending");
            if (!request.Profile.AutoPurchaseEnabled || !request.Profile.ContractEnabled || !SuzProfileCatalog.Supported(request.Profile)) return new("", PurchaseStage.NeedsReconciliation, 0, null, null, "auto_purchase_unavailable");
            var intent = db.GetOrCreatePurchaseIntent(request); return await ResumeCore(intent.Id, ct, WorkflowRunMode.UserRequested);
        }
        finally { gate.Release(); }
    }
    public async Task<PurchaseResult> ResumeAsync(string intentId, CancellationToken ct, WorkflowRunMode mode = WorkflowRunMode.UserRequested)
    {
        var intent = db.GetPurchaseIntent(intentId); if (intent is null) return Missing(intentId);
        var gate = Gate(intent.Request.Profile, intent.Request.Gtin); await gate.WaitAsync(ct);
        try { return await ResumeCore(intentId, ct, mode); } finally { gate.Release(); }
    }
    private async Task<PurchaseResult> ResumeCore(string id, CancellationToken ct, WorkflowRunMode mode)
    {
        var intent = db.GetPurchaseIntent(id); if (intent is null) return Missing(id);
        if (intent.RetryAt > clock.UtcNow) return Result(intent);
        if (intent.Stage is PurchaseStage.CodesRecovered or PurchaseStage.Rejected) return Result(intent);
        if (!db.PurchaseAuthorizationMatches(id)) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "authorization_changed");
        try
        {
            if (intent.Stage is PurchaseStage.Draft or PurchaseStage.Validated)
            {
                if (mode == WorkflowRunMode.RecoveryOnly) return Result(intent);
                if (!intent.Request.Profile.AutoPurchaseEnabled || !intent.Request.Profile.ContractEnabled || db.BlockingPurchase(intent.Request.Profile, intent.Request.Gtin, id) is not null)
                    return Save(id, PurchaseStage.NeedsReconciliation, null, null, "purchase_not_authorized");
                if (!db.TryBeginPurchase(id)) return Result(db.GetPurchaseIntent(id) ?? intent);
                intent = db.GetPurchaseIntent(id)!;
                SuzOutcome<SuzOrderReceipt> receipt;
                try { receipt = await suz.CreateOrderAsync(intent, ct); }
                catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or TimeoutException) { return Save(id, PurchaseStage.CreateUnknown, null, null, "create_unknown"); }
                if (receipt.Kind != SuzOutcomeKind.Confirmed || string.IsNullOrWhiteSpace(receipt.Value?.OrderId))
                    return Save(id, receipt.Kind == SuzOutcomeKind.Rejected ? PurchaseStage.Rejected : PurchaseStage.CreateUnknown, null, receipt.RetryAt, receipt.ErrorCode ?? "create_unknown");
                // Evidence is saved before the generation/config fence decides the next phase.
                db.SavePurchaseOutcome(id, PurchaseStage.Polling, receipt.Value.OrderId, null, null);
                intent = db.GetPurchaseIntent(id); if (intent is null) return Missing(id);
                if (!db.PurchaseAuthorizationMatches(id)) return Result(intent);
            }
            if (intent.RemoteOrderId is null)
            {
                if (intent.Stage is PurchaseStage.CreateSending) db.SavePurchaseOutcome(id, PurchaseStage.CreateUnknown, null, null, "crash_after_send");
                return await ReconcileCore(id, null, false, ct);
            }
            if (intent.Stage is PurchaseStage.DownloadUnknown or PurchaseStage.Downloading or PurchaseStage.Recovering || db.PurchaseBlocks(id).Count > 0)
                return await RecoverCore(intent, ct);
            if (!db.PurchaseAuthorizationMatches(id)) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "authorization_changed");
            var status = await suz.ReadOrderAsync(intent, ct);
            if (status.Kind != SuzOutcomeKind.Confirmed || status.Value is null)
                return Save(id, PurchaseStage.Polling, null, status.RetryAt ?? clock.UtcNow.AddSeconds(2), status.ErrorCode ?? "poll_pending");
            if (status.Value.OrderId != intent.RemoteOrderId) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "order_identity_mismatch");
            if (status.Value.State is "REJECTED" or "DECLINED") return Save(id, PurchaseStage.Rejected, null, null, "remote_rejected");
            if (status.Value.State is "CLOSED") return await RecoverCore(intent, ct);
            if (status.Value.State is not ("ACTIVE" or "READY") || status.Value.AvailableCodes <= 0) return Save(id, PurchaseStage.Polling, null, clock.UtcNow.AddSeconds(2), "codes_pending");
            if (mode == WorkflowRunMode.RecoveryOnly) return Save(id, PurchaseStage.Polling, null, null, "user_requested_receive_required");
            db.SavePurchaseOutcome(id, PurchaseStage.Downloading, null, null, null);
            intent = db.GetPurchaseIntent(id); if (intent is null) return Missing(id);
            if (!db.PurchaseAuthorizationMatches(id)) return Result(intent);
            var block = await suz.ReceiveCodesAsync(intent, intent.Request.Quantity, ct);
            if (block.Kind != SuzOutcomeKind.Confirmed || block.Value is null) return Save(id, PurchaseStage.DownloadUnknown, null, block.RetryAt, block.ErrorCode ?? "receive_unknown");
            if (!ValidBlock(intent, block.Value)) return Save(id, PurchaseStage.DownloadUnknown, null, null, "block_identity_mismatch");
            db.SavePurchaseBlock(id, block.Value);
            return FinishCodes(id);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or TimeoutException)
        {
            var saved = db.GetPurchaseIntent(id); if (saved is null) return Missing(id);
            var stage = saved.Stage switch { PurchaseStage.CreateSending => PurchaseStage.CreateUnknown, PurchaseStage.Downloading => PurchaseStage.DownloadUnknown, _ => saved.Stage };
            return Save(id, stage, null, saved.RetryAt, "operation_interrupted");
        }
        catch (InvalidOperationException) { return Save(id, PurchaseStage.NeedsReconciliation, null, null, "evidence_conflict"); }
    }
    private async Task<PurchaseResult> RecoverCore(PurchaseIntent intent, CancellationToken ct)
    {
        if (!db.PurchaseAuthorizationMatches(intent.Id)) return Save(intent.Id, PurchaseStage.NeedsReconciliation, null, null, "authorization_changed");
        var stored = db.PurchaseBlocks(intent.Id);
        if (Count(stored) >= intent.Request.Quantity && !db.PurchaseHasConflicts(intent.Id)) return FinishCodes(intent.Id);
        var listed = await suz.ListBlocksAsync(intent, ct);
        if (listed.Kind != SuzOutcomeKind.Confirmed || listed.Value is null)
            return Save(intent.Id, PurchaseStage.NeedsReconciliation, null, listed.RetryAt, listed.ErrorCode ?? "blocks_unavailable");
        foreach (var block in listed.Value)
        {
            if (!ValidBlock(intent, block)) return Save(intent.Id, PurchaseStage.NeedsReconciliation, null, null, "block_identity_mismatch");
            if (stored.Any(x => x.BlockId == block.BlockId && x.Codes.Count > 0)) continue;
            // Persist identity before the reconciliation request can be interrupted.
            db.SavePurchaseBlock(intent.Id, block with { Codes = Array.Empty<string>() });
            db.SavePurchaseOutcome(intent.Id, PurchaseStage.Recovering, null, null, null);
            if (!db.PurchaseAuthorizationMatches(intent.Id)) return Result(db.GetPurchaseIntent(intent.Id) ?? intent);
            var recovered = await suz.RecoverBlockAsync(intent, block.BlockId, ct);
            if (recovered.Kind != SuzOutcomeKind.Confirmed || recovered.Value is null)
                return Save(intent.Id, PurchaseStage.NeedsReconciliation, null, recovered.RetryAt, recovered.ErrorCode ?? "block_recovery_pending");
            if (!ValidBlock(intent, recovered.Value) || recovered.Value.BlockId != block.BlockId) return Save(intent.Id, PurchaseStage.NeedsReconciliation, null, null, "block_identity_mismatch");
            db.SavePurchaseBlock(intent.Id, recovered.Value);
        }
        return FinishCodes(intent.Id);
    }
    public async Task<PurchaseResult> ReconcileAsync(string intentId, string? selectedRemoteOrderId, bool sellerConfirmed, CancellationToken ct)
    {
        var intent = db.GetPurchaseIntent(intentId); if (intent is null) return Missing(intentId);
        var gate = Gate(intent.Request.Profile, intent.Request.Gtin); await gate.WaitAsync(ct);
        try { return await ReconcileCore(intentId, selectedRemoteOrderId, sellerConfirmed, ct); } finally { gate.Release(); }
    }
    private async Task<PurchaseResult> ReconcileCore(string id, string? selectedId, bool confirmed, CancellationToken ct)
    {
        var intent = db.GetPurchaseIntent(id); if (intent is null) return Missing(id);
        if (intent.RetryAt > clock.UtcNow) return Result(intent);
        if (!db.PurchaseAuthorizationMatches(id)) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "authorization_changed");
        if (intent.RemoteOrderId is not null) return Result(intent);
        var listed = await suz.ListOrdersAsync(intent, ct);
        if (listed.Kind != SuzOutcomeKind.Confirmed || listed.Value is null) return Save(id, PurchaseStage.NeedsReconciliation, null, listed.RetryAt, "create_reconciliation_pending");
        var matches = listed.Value.Where(x => x.OwnerInn == intent.Request.Profile.OwnerInn && x.Environment == intent.Request.Profile.Environment && x.Gtin == intent.Request.Gtin && x.Quantity == intent.Request.Quantity && x.OrderId != "").ToArray();
        var candidates = selectedId is not null && confirmed ? matches.Where(x => x.OrderId == selectedId).ToArray() : matches.Where(x => x.CorrelationProof == intent.RequestKey).ToArray();
        if (candidates.Length != 1) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "correlation_required");
        db.SavePurchaseOutcome(id, PurchaseStage.Polling, candidates[0].OrderId, null, null);
        if (confirmed) db.Audit("FBS workflow", "Reconcile purchase", "Seller confirmed intent " + id);
        return Result(db.GetPurchaseIntent(id) ?? intent);
    }
    private PurchaseResult FinishCodes(string id)
    {
        var intent = db.GetPurchaseIntent(id); if (intent is null) return Missing(id);
        if (!db.PurchaseAuthorizationMatches(id)) return Save(id, PurchaseStage.NeedsReconciliation, null, null, "authorization_changed");
        var count = Count(db.PurchaseBlocks(id));
        return Save(id, count == intent.Request.Quantity && !db.PurchaseHasConflicts(id) ? PurchaseStage.CodesRecovered : PurchaseStage.NeedsReconciliation, null, null, count == intent.Request.Quantity && !db.PurchaseHasConflicts(id) ? null : "partial_or_conflicting_codes");
    }
    private static bool ValidBlock(PurchaseIntent intent, SuzBlock block)
    {
        if (block.BlockId == "" || block.OrderId != intent.RemoteOrderId || block.Gtin != intent.Request.Gtin) return false;
        try { return block.Codes.All(x => KizCodeIdentity.Gtin(x) == intent.Request.Gtin); } catch (InvalidOperationException) { return false; }
    }
    private static int Count(IReadOnlyList<SuzBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var raw in blocks.SelectMany(x => x.Codes)) { try { seen.Add(KizCodeIdentity.Cis(raw)); } catch (InvalidOperationException) { } } return seen.Count;
    }
    private PurchaseResult Result(PurchaseIntent intent) => new(intent.Id, intent.Stage, Count(db.PurchaseBlocks(intent.Id)), intent.RemoteOrderId, intent.RetryAt, intent.ErrorCode);
    private PurchaseResult Save(string id, PurchaseStage stage, string? orderId, DateTimeOffset? at, string? code)
    { db.SavePurchaseOutcome(id, stage, orderId, at, code); var intent = db.GetPurchaseIntent(id); return intent is null ? Missing(id) : Result(intent); }
    private static PurchaseResult Missing(string id) => new(id, PurchaseStage.NeedsReconciliation, 0, null, null, "store_or_intent_deleted");
}
