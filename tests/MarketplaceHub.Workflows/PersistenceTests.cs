using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using static MarketplaceHub.Workflows.WorkflowTestRunner;
namespace MarketplaceHub.Workflows;
public static class PersistenceTests
{
    public static async Task Run(WorkflowTestRunner r)
    {
        await r.CheckAsync("active_target_is_unique_and_reorder_is_stable", async () => {
            using var f = WorkflowFixture.Create();
            var jobs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Db.GetOrCreateLabelJob(f.Snapshot))));
            var reordered = f.Db.GetOrCreateLabelJob(f.Snapshot with { Units = f.Snapshot.Units.Reverse().ToArray() });
            Expect(jobs.All(x => x.Id == reordered.Id && x.SnapshotHash == reordered.SnapshotHash), "Repeated/reordered request duplicated active job");
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM fbs_label_jobs")) == 1, "Duplicate persisted jobs");
        });
        await r.CheckAsync("snapshot_changes_stop_without_replacing_original", () => {
            using var f = WorkflowFixture.Create(); var first = f.Db.GetOrCreateLabelJob(f.Snapshot);
            var changed = f.Db.GetOrCreateLabelJob(f.Snapshot with { Units = f.Snapshot.Units.Take(1).ToArray() });
            Expect(changed.Id == first.Id && changed.Stage == FbsLabelJobStage.SnapshotChanged && changed.Snapshot.Units.Count == 2 && changed.Revision == 1, "Changed quantity replaced submitted identity");
            return Task.CompletedTask;
        });
        await r.CheckAsync("intent_remote_id_survives_error_and_restart", () => {
            using var f = WorkflowFixture.Create(); var intent = f.Db.GetOrCreatePurchaseIntent(f.Request());
            f.Db.SavePurchaseOutcome(intent.Id, PurchaseStage.Polling, "SUZ-1", null, null);
            f.Db.SavePurchaseOutcome(intent.Id, PurchaseStage.NeedsReconciliation, null, null, "poll_failed");
            Expect(f.Reopen().GetPurchaseIntent(intent.Id)!.RemoteOrderId == "SUZ-1", "ERROR/restart erased SUZ ID");
            Throws<InvalidOperationException>(() => f.Db.SavePurchaseOutcome(intent.Id, PurchaseStage.Polling, "SUZ-2", null, null));
            return Task.CompletedTask;
        });
        await r.CheckAsync("intent_payload_and_quantity_are_immutable", () => {
            using var f = WorkflowFixture.Create(); var request = f.Request(); var first = f.Db.GetOrCreatePurchaseIntent(request);
            Expect(f.Db.GetOrCreatePurchaseIntent(request).Id == first.Id, "Repeated request duplicated intent");
            Throws<ArgumentOutOfRangeException>(() => f.Db.GetOrCreatePurchaseIntent(request with { Quantity = -1 }));
            Throws<InvalidOperationException>(() => f.Db.GetOrCreatePurchaseIntent(request with { Quantity = 1 }));
            Throws<InvalidOperationException>(() => f.Db.GetOrCreatePurchaseIntent(request with { PayloadHash = "CHANGED" }));
            return Task.CompletedTask;
        });
        await r.CheckAsync("legacy_error_is_not_rebuyable_and_migration_is_idempotent", () => {
            using var f = WorkflowFixture.Create();
            f.Db.UpsertZnakPipeline(f.Store.Id, "KNOWN", WorkflowFixture.Gtin, "ERROR", "LEGACY-1");
            f.Db.UpsertZnakPipeline(f.Store.Id, "UNKNOWN", "04601234567886", "ERROR");
            f.Db.UpsertZnakPipeline(f.Store.Id, "ALIAS", WorkflowFixture.Gtin, "ERROR", "LEGACY-1");
            var reopened = f.Reopen();
            var scope = new KizScope(f.Store.Id, "", "Unknown");
            var known = reopened.FindOpenPurchase(scope, WorkflowFixture.Gtin);
            var unknown = reopened.FindOpenPurchase(scope, "04601234567886");
            Expect(known is { Stage: PurchaseStage.Polling, RemoteOrderId: "LEGACY-1" } && unknown is { Stage: PurchaseStage.NeedsReconciliation }, "Legacy error became rebuyable");
            var count = Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM kiz_purchase_intents"));
            f.Reopen(); Expect(count == 2 && Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM kiz_purchase_intents")) == 2, "Migration duplicated legacy reference");
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM znak_pipeline")) == 3, "Migration changed legacy evidence");
            return Task.CompletedTask;
        });
        await r.CheckAsync("canonical_alias_preserves_tail_and_cis_identity", () => {
            using var f = WorkflowFixture.Create(); var p = new KizCodeProtector(f.DbPath);
            Expect(p.Identity(WorkflowFixture.Raw) == p.Identity("]d2" + WorkflowFixture.Raw), "Scanner prefix created a second identity");
            Expect(p.Identity(WorkflowFixture.Raw) != p.Identity(WorkflowFixture.OtherTail), "Crypto tail evidence was discarded");
            Expect(p.CisIdentity(WorkflowFixture.Raw) == p.CisIdentity(WorkflowFixture.OtherTail), "Same CIS became two stock units");
            Expect(p.Unprotect(p.Protect(WorkflowFixture.Raw)) == WorkflowFixture.Raw, "Encrypted payload lost GS/case/tail");
            Expect(new KizCodeProtector(f.DbPath).Identity(WorkflowFixture.Raw) == p.Identity(WorkflowFixture.Raw), "Restart changed HMAC identity");
            return Task.CompletedTask;
        });
        await r.CheckAsync("protected_block_tail_conflict_is_not_second_inventory_unit", () => {
            using var f = WorkflowFixture.Create(); var i = f.Db.GetOrCreatePurchaseIntent(f.Request());
            f.Db.SavePurchaseOutcome(i.Id, PurchaseStage.Downloading, "SUZ-1", null, null);
            var block = new SuzBlock("BLOCK-1", "SUZ-1", WorkflowFixture.Gtin, new[] { WorkflowFixture.Raw });
            f.Db.SavePurchaseBlock(i.Id, block); f.Db.SavePurchaseBlock(i.Id, block);
            f.Db.SavePurchaseBlock(i.Id, block with { BlockId = "BLOCK-2", Codes = new[] { WorkflowFixture.OtherTail } });
            Expect(f.Reopen().PurchaseBlocks(i.Id).Count == 2, "Block evidence was lost or duplicated");
            Expect(Convert.ToInt64(f.Sql("SELECT COUNT(*) FROM kiz_codes_scoped")) == 1, "Tail collision increased stock");
            var dump = Convert.ToString(f.Sql("SELECT group_concat(codes_enc) FROM kiz_purchase_blocks"))! + Convert.ToString(f.Sql("SELECT group_concat(raw_enc) FROM kiz_codes_scoped"));
            Expect(!dump.Contains(WorkflowFixture.Raw) && !dump.Contains("TAIL-A"), "New persistence leaked raw KIZ");
            Throws<InvalidOperationException>(() => f.Db.SavePurchaseBlock(i.Id, block with { OrderId = "WRONG" }));
            return Task.CompletedTask;
        });
        await r.CheckAsync("instance_lock_and_deleted_store_fence_late_worker", () => {
            using var f = WorkflowFixture.Create();
            Expect(DatabaseInstanceLock.TryAcquire(f.DbPath, out var held), "First instance failed");
            using (held) Expect(!DatabaseInstanceLock.TryAcquire(f.DbPath, out _), "Second instance acquired same DB");
            Expect(DatabaseInstanceLock.TryAcquire(f.DbPath, out var released), "Lock failed to release"); released!.Dispose();
            var job = f.Db.GetOrCreateLabelJob(f.Snapshot); var generation = f.Db.StoreGeneration(f.Store.Id);
            f.Db.DeleteStore(f.Store.Id);
            Expect(!f.Db.StoreGenerationMatches(f.Store.Id, generation), "Deleted store generation matched");
            Expect(!f.Db.TrySaveLabelJob(job.Id, job.Version, FbsLabelJobStage.LabelsReady, Array.Empty<UnitWorkflowResult>()), "Late worker recreated deleted job");
            Throws<InvalidOperationException>(() => f.Db.GetOrCreateLabelJob(f.Snapshot));
            return Task.CompletedTask;
        });
        await r.CheckAsync("claim_and_optimistic_save_reject_stale_versions", () => {
            using var f = WorkflowFixture.Create(); var job = f.Db.GetOrCreateLabelJob(f.Snapshot);
            Expect(f.Db.TryClaimLabelJob(job.Id, job.Version) && !f.Db.TryClaimLabelJob(job.Id, job.Version), "Two workers claimed same job");
            f.Db.ReleaseLabelJob(job.Id);
            Expect(f.Db.TrySaveLabelJob(job.Id, job.Version, FbsLabelJobStage.Validating, Array.Empty<UnitWorkflowResult>()), "Current version rejected");
            Expect(!f.Db.TrySaveLabelJob(job.Id, job.Version, FbsLabelJobStage.LabelsReady, Array.Empty<UnitWorkflowResult>()), "Stale save accepted");
            return Task.CompletedTask;
        });
        await r.CheckAsync("purchase_claim_is_persisted_before_send", () => {
            using var f = WorkflowFixture.Create(); var intent = f.Db.GetOrCreatePurchaseIntent(f.Request());
            Expect(f.Db.TryBeginPurchase(intent.Id) && !f.Db.TryBeginPurchase(intent.Id), "Create claim was not exclusive");
            Expect(f.Db.GetPurchaseIntent(intent.Id)!.Stage == PurchaseStage.CreateSending, "Sending checkpoint was not durable");
            Expect(f.Reopen().GetPurchaseIntent(intent.Id)!.Stage == PurchaseStage.CreateUnknown, "Crash recovery allowed resending");
            return Task.CompletedTask;
        });
    }
}
