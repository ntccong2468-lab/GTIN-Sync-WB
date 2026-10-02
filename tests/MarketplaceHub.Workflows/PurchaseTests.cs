using MarketplaceHub.Core;
using MarketplaceHub.Services.Suz;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class PurchaseTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("concurrent_clicks_send_one_purchase_after_durable_checkpoint", async () => {
            using var f = PurchaseFixture.Create();
            f.Suz.OnCreate = (i, _) => { Expect(f.Db.GetPurchaseIntent(i.Id)!.Stage == PurchaseStage.CreateSending, "POST preceded durable sending checkpoint"); return Task.FromResult(new SuzOutcome<SuzOrderReceipt>(SuzOutcomeKind.Confirmed, new("SUZ-1"))); };
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => f.Coordinator.EnsureAsync(f.Request, default)));
            Expect(results.All(x => x.IntentId == results[0].IntentId) && f.Suz.CreateCalls == 1 && f.Suz.ReceiveCalls == 1 && results[0].RecoveredCount == 2, "Repeated clicks bought or received twice");
        });
        await r.CheckAsync("zero_and_negative_quantity_do_not_create_intent_or_http", async () => {
            using var f = PurchaseFixture.Create(); var zero = await f.Coordinator.EnsureAsync(f.Request with { Quantity = 0 }, default);
            var rejected = false; try { await f.Coordinator.EnsureAsync(f.Request with { Quantity = -1 }, default); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Expect(zero.RecoveredCount == 0 && rejected && f.Suz.TotalCalls == 0 && Convert.ToInt64(f.Data.Sql("SELECT COUNT(*) FROM kiz_purchase_intents")) == 0, "Invalid quantity issued purchase intent");
        });
        await r.CheckAsync("poll_error_preserves_id_after_restart", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnRead = (i, _) => Task.FromResult(new SuzOutcome<SuzOrderStatus>(SuzOutcomeKind.Unknown, null, "poll_failed"));
            var first = await f.Coordinator.EnsureAsync(f.Request, default); f.Clock.UtcNow += TimeSpan.FromMinutes(1);
            var resumed = await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(resumed.RemoteOrderId == "SUZ-1" && f.Suz.CreateCalls == 1 && f.Suz.ReadIds.All(x => x == "SUZ-1"), "Poll error lost receipt or created replacement");
        });
        await r.CheckAsync("crash_after_sending_reconciles_without_resend", async () => {
            using var f = PurchaseFixture.Create(); var intent = f.Db.GetOrCreatePurchaseIntent(f.Request); Expect(f.Db.TryBeginPurchase(intent.Id), "Fixture did not enter Sending");
            var result = await f.Reopen().ResumeAsync(intent.Id, default);
            Expect(result.Stage == PurchaseStage.NeedsReconciliation && f.Suz.CreateCalls == 0 && f.Suz.ReceiveCalls == 0, "Restart resent ambiguous create");
        });
        await r.CheckAsync("lost_receive_recovers_original_known_block", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnReceive = (i, q, _) => { Expect(q == 2 && f.Db.GetPurchaseIntent(i.Id)!.Stage == PurchaseStage.Downloading, "Receive did not persist checkpoint/quantity"); return Task.FromResult(new SuzOutcome<SuzBlock>(SuzOutcomeKind.Unknown, null, "transport_unknown")); };
            var first = await f.Coordinator.EnsureAsync(f.Request, default);
            f.Suz.Blocks = new[] { new SuzBlock("BLOCK-1", "SUZ-1", WorkflowFixture.Gtin, Array.Empty<string>()) };
            var resumed = await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(resumed.RecoveredCount == 2 && resumed.Stage == PurchaseStage.CodesRecovered && f.Suz.ReceiveCalls == 1 && f.Suz.RecoverCalls == 1 && f.Suz.CreateCalls == 1, "Lost response reissued or lost original block");
            await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(f.Suz.ReceiveCalls == 1 && f.Suz.CreateCalls == 1, "Recovered block triggered more purchases");
        });
        await r.CheckAsync("retry_after_survives_restart_and_never_polls_early", async () => {
            using var f = PurchaseFixture.Create(); var deadline = f.Clock.UtcNow.AddMinutes(2);
            f.Suz.OnRead = (_, _) => Task.FromResult(new SuzOutcome<SuzOrderStatus>(SuzOutcomeKind.Pending, null, "quota", deadline));
            var first = await f.Coordinator.EnsureAsync(f.Request, default); var before = f.Suz.TotalCalls;
            var resumed = await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(resumed.RetryAt == deadline && f.Suz.TotalCalls == before, "Restart ignored persisted quota");
            f.Clock.UtcNow = deadline; await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(f.Suz.TotalCalls == before + 1 && f.Suz.CreateCalls == 1, "Deadline did not resume original order");
        });
        await r.CheckAsync("partial_codes_are_kept_without_top_up", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnReceive = (i, _, _) => Task.FromResult(new SuzOutcome<SuzBlock>(SuzOutcomeKind.Confirmed, FakeSuzClient.FullBlock(i) with { Codes = new[] { WorkflowFixture.Raw } }));
            var first = await f.Coordinator.EnsureAsync(f.Request, default); var second = await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(first.Stage == PurchaseStage.NeedsReconciliation && second.RecoveredCount == 1 && f.Suz.CreateCalls == 1 && f.Suz.ReceiveCalls == 1 && f.Db.PurchaseBlocks(first.IntentId).Single().Codes.Count == 1, "Partial result was discarded or topped up");
        });
        await r.CheckAsync("unknown_purchase_blocks_another_job_in_owner_environment_gtin", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnCreate = (_, _) => Task.FromResult(new SuzOutcome<SuzOrderReceipt>(SuzOutcomeKind.Unknown, null, "create_response_unknown"));
            await f.Coordinator.EnsureAsync(f.Request, default);
            var snapshot = f.Data.Snapshot with { Target = f.Data.Snapshot.Target with { TargetId = "SUPPLY-2" } }; var job = f.Db.GetOrCreateLabelJob(snapshot);
            var result = await f.Coordinator.EnsureAsync(f.Request with { JobId = job.Id }, default);
            Expect(result.Stage == PurchaseStage.NeedsReconciliation && f.Suz.CreateCalls == 1, "Second job bypassed ambiguous purchase scope");
        });
        await r.CheckAsync("rejected_order_is_not_replaced", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnRead = (i, _) => Task.FromResult(new SuzOutcome<SuzOrderStatus>(SuzOutcomeKind.Confirmed, new(i.RemoteOrderId!, "REJECTED", 0, "remote_rejected")));
            var first = await f.Coordinator.EnsureAsync(f.Request, default); var second = await f.Coordinator.EnsureAsync(f.Request, default);
            Expect(first.Stage == PurchaseStage.Rejected && second.Stage == PurchaseStage.Rejected && f.Suz.CreateCalls == 1 && f.Suz.ReceiveCalls == 0, "Rejection bought a replacement");
        });
        await r.CheckAsync("same_gtin_and_quantity_without_correlation_never_autobinds", async () => {
            using var f = PurchaseFixture.Create(); var i = f.Db.GetOrCreatePurchaseIntent(f.Request); f.Db.TryBeginPurchase(i.Id);
            f.Suz.Candidates = new[] { new SuzOrderCandidate("REMOTE-A", f.Profile.OwnerInn, f.Profile.Environment, WorkflowFixture.Gtin, 2, null), new SuzOrderCandidate("REMOTE-B", f.Profile.OwnerInn, f.Profile.Environment, WorkflowFixture.Gtin, 2, null) };
            var result = await f.Reopen().ResumeAsync(i.Id, default);
            Expect(result.Stage == PurchaseStage.NeedsReconciliation && result.RemoteOrderId is null && f.Suz.CreateCalls == 0, "Similar orders were guessed");
            f.Suz.Candidates = f.Suz.Candidates.Take(1).ToArray(); result = await f.Coordinator.ReconcileAsync(i.Id, null, false, default);
            Expect(result.RemoteOrderId is null, "Single uncorrelated candidate was guessed");
        });
        await r.CheckAsync("manual_selection_rejects_wrong_scope_and_confirms_exact_candidate", async () => {
            using var f = PurchaseFixture.Create(); var i = f.Db.GetOrCreatePurchaseIntent(f.Request); f.Db.TryBeginPurchase(i.Id); f.Data.Reopen();
            f.Suz.Candidates = new[] { new SuzOrderCandidate("REMOTE-A", "OTHER-OWNER", f.Profile.Environment, WorkflowFixture.Gtin, 2, null) };
            var bad = await f.Coordinator.ReconcileAsync(i.Id, "REMOTE-A", true, default); Expect(bad.RemoteOrderId is null, "Manual acknowledgement bypassed owner");
            f.Suz.Candidates = new[] { f.Suz.Candidates[0] with { OwnerInn = f.Profile.OwnerInn } };
            var ok = await f.Coordinator.ReconcileAsync(i.Id, "REMOTE-A", true, default);
            Expect(ok.RemoteOrderId == "REMOTE-A" && f.Suz.CreateCalls == 0, "Exact confirmed candidate was not preserved");
        });
        await r.CheckAsync("credential_change_before_send_and_after_receipt_is_fenced", async () => {
            using var f = PurchaseFixture.Create();
            f.Suz.OnCreate = (_, _) => { f.Db.SaveZnakConfig(f.Db.GetZnakConfig() with { OmsConnection = "rotated-fixture", AutoCirculation = true }); return Task.FromResult(new SuzOutcome<SuzOrderReceipt>(SuzOutcomeKind.Confirmed, new("SUZ-LATE"))); };
            var result = await f.Coordinator.EnsureAsync(f.Request, default);
            Expect(result.RemoteOrderId == "SUZ-LATE" && result.Stage == PurchaseStage.NeedsReconciliation && f.Suz.ReadCalls == 0 && f.Suz.ReceiveCalls == 0, "Late receipt continued with old authorization");
            var snapshot = f.Data.Snapshot with { Target = f.Data.Snapshot.Target with { TargetId = "SUPPLY-2" } }; var job = f.Db.GetOrCreateLabelJob(snapshot);
            await f.Coordinator.EnsureAsync(f.Request with { JobId = job.Id }, default); Expect(f.Suz.CreateCalls == 1, "Changed credential sent another purchase");
        });
        await r.CheckAsync("deleted_store_during_create_never_recreates_intent_or_inventory", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnCreate = (_, _) => { f.Db.DeleteStore(f.Data.Store.Id); return Task.FromResult(new SuzOutcome<SuzOrderReceipt>(SuzOutcomeKind.Confirmed, new("SUZ-DELETED"))); };
            var result = await f.Coordinator.EnsureAsync(f.Request, default);
            Expect(result.Stage == PurchaseStage.NeedsReconciliation && f.Suz.ReadCalls == 0 && Convert.ToInt64(f.Data.Sql("SELECT COUNT(*) FROM kiz_purchase_intents")) == 0 && Convert.ToInt64(f.Data.Sql("SELECT COUNT(*) FROM kiz_codes_scoped")) == 0, "Late worker recreated deleted store data");
        });
        await r.CheckAsync("recovery_only_never_creates_or_receives_new_codes", async () => {
            using var f = PurchaseFixture.Create(); var i = f.Db.GetOrCreatePurchaseIntent(f.Request);
            await f.Coordinator.ResumeAsync(i.Id, default, WorkflowRunMode.RecoveryOnly); Expect(f.Suz.TotalCalls == 0, "RecoveryOnly sent a draft");
            f.Db.SavePurchaseOutcome(i.Id, PurchaseStage.Polling, "SUZ-1", null, null);
            await f.Coordinator.ResumeAsync(i.Id, default, WorkflowRunMode.RecoveryOnly);
            Expect(f.Suz.ReadCalls == 1 && f.Suz.CreateCalls == 0 && f.Suz.ReceiveCalls == 0, "RecoveryOnly received new codes");
        });
        await r.CheckAsync("closed_order_with_unavailable_blocks_needs_reconciliation", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnRead = (i, _) => Task.FromResult(new SuzOutcome<SuzOrderStatus>(SuzOutcomeKind.Confirmed, new(i.RemoteOrderId!, "CLOSED", 0, null)));
            var result = await f.Coordinator.EnsureAsync(f.Request, default);
            Expect(result.Stage == PurchaseStage.NeedsReconciliation && result.RemoteOrderId == "SUZ-1" && f.Suz.CreateCalls == 1 && f.Suz.ReceiveCalls == 0, "Closed order was replaced or reported ready");
        });
        await r.CheckAsync("duplicate_block_listing_recovers_once_without_erasing_evidence", async () => {
            using var f = PurchaseFixture.Create(); f.Suz.OnReceive = (_, _, _) => Task.FromResult(new SuzOutcome<SuzBlock>(SuzOutcomeKind.Unknown, null, "transport_unknown"));
            var first = await f.Coordinator.EnsureAsync(f.Request, default);
            var block = new SuzBlock("BLOCK-1", "SUZ-1", WorkflowFixture.Gtin, Array.Empty<string>()); f.Suz.Blocks = new[] { block, block };
            var result = await f.Reopen().ResumeAsync(first.IntentId, default);
            Expect(result.Stage == PurchaseStage.CodesRecovered && result.RecoveredCount == 2 && f.Suz.RecoverCalls == 1 && f.Db.PurchaseBlocks(first.IntentId).Single().Codes.Count == 2, "Duplicate listing repeated recovery or damaged evidence");
        });
    }
}
