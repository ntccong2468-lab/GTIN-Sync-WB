using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace MarketplaceHub.Infrastructure;
public sealed partial class AppDatabase
{
    public void SaveSuzProfile(SuzProfile profile)
    {
        if (profile.Version < 1 || string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidOperationException("invalid_suz_profile");
        var generation = StoreGeneration(profile.StoreId);
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        if (!GenerationMatches(c, tx, profile.StoreId, generation)) throw new InvalidOperationException("store_generation_changed");
        var json = JsonSerializer.Serialize(profile);
        using var existing = WfSql(c, tx, "SELECT profile_json FROM kiz_profiles WHERE profile_id=$p AND version=$v", ("$p", profile.Id), ("$v", profile.Version));
        var saved = existing.ExecuteScalar() as string;
        if (saved is not null && saved != json) throw new InvalidOperationException("profile_version_is_immutable");
        using var clear = WfSql(c, tx, "UPDATE kiz_profiles SET current=0 WHERE store_id=$s", ("$s", profile.StoreId)); clear.ExecuteNonQuery();
        using var insert = WfSql(c, tx, "INSERT INTO kiz_profiles VALUES($p,$v,$s,$j,1) ON CONFLICT(profile_id,version) DO UPDATE SET current=1", ("$p", profile.Id), ("$v", profile.Version), ("$s", profile.StoreId), ("$j", json)); insert.ExecuteNonQuery(); tx.Commit();
    }
    public SuzProfile? SuzProfileForStore(long storeId)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT profile_json FROM kiz_profiles WHERE store_id=$s AND current=1", ("$s", storeId));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SuzProfile>(json) : null;
    }
    public PurchaseIntent GetOrCreatePurchaseIntent(PurchaseIntentRequest request)
    {
        if (request.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(request.Quantity));
        if (GtinCode.Normalize(request.Gtin) != request.Gtin || string.IsNullOrEmpty(request.PayloadHash)) throw new InvalidOperationException("invalid_purchase_request");
        var job = GetLabelJob(request.JobId) ?? throw new InvalidOperationException("job_not_found");
        if (job.Revision != request.Revision || job.Snapshot.Target.StoreId != request.Profile.StoreId) throw new InvalidOperationException("purchase_scope_mismatch");
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        if (!GenerationMatches(c, tx, request.Profile.StoreId, job.Snapshot.StoreGeneration)) throw new InvalidOperationException("store_generation_changed");
        using var find = WfSql(c, tx, "SELECT id,request_json FROM kiz_purchase_intents WHERE job_id=$j AND revision=$r AND profile_id=$p AND environment=$e AND gtin=$g", ("$j", request.JobId), ("$r", request.Revision), ("$p", request.Profile.Id), ("$e", request.Profile.Environment), ("$g", request.Gtin));
        string? id; var json = JsonSerializer.Serialize(request);
        using (var row = find.ExecuteReader()) { id = row.Read() ? row.GetString(0) : null; if (id is not null && row.GetString(1) != json) throw new InvalidOperationException("purchase_payload_is_immutable"); }
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            InsertPurchase(c, tx, id, request, Guid.NewGuid().ToString("N"), PurchaseStage.Draft, null, job.Snapshot.StoreGeneration);
        }
        tx.Commit(); return GetPurchaseIntent(id)!;
    }
    private static void InsertPurchase(SqliteConnection c, SqliteTransaction? tx, string id, PurchaseIntentRequest request, string requestKey, PurchaseStage stage, string? remoteOrderId, int generation)
    {
        using var cmd = WfSql(c, tx, "INSERT OR IGNORE INTO kiz_purchase_intents(id,store_id,job_id,revision,profile_id,owner_inn,environment,gtin,request_json,request_key,stage,remote_order_id,generation,authorization_version,created_at) VALUES($id,$s,$j,$r,$p,$o,$e,$g,$json,$k,$st,$remote,$gen,$v,$at)",
            ("$id", id), ("$s", request.Profile.StoreId), ("$j", request.JobId), ("$r", request.Revision), ("$p", request.Profile.Id), ("$o", request.Profile.OwnerInn), ("$e", request.Profile.Environment), ("$g", request.Gtin),
            ("$json", JsonSerializer.Serialize(request)), ("$k", requestKey), ("$st", stage.ToString()), ("$remote", remoteOrderId), ("$gen", generation), ("$v", request.Profile.CredentialVersion), ("$at", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public PurchaseIntent? GetPurchaseIntent(string id)
    {
        using var c = WorkflowConnection(); return ReadPurchase(c, null, id);
    }
    public PurchaseIntent? PurchaseForJob(PurchaseIntentRequest request)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT id FROM kiz_purchase_intents WHERE job_id=$j AND revision=$r AND profile_id=$p AND environment=$e AND gtin=$g", ("$j", request.JobId), ("$r", request.Revision), ("$p", request.Profile.Id), ("$e", request.Profile.Environment), ("$g", request.Gtin));
        return cmd.ExecuteScalar() is string id ? ReadPurchase(c, null, id) : null;
    }
    public PurchaseIntent? BlockingPurchase(SuzProfile profile, string gtin, string? exceptIntent = null)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT id FROM kiz_purchase_intents WHERE gtin=$g AND (($o=owner_inn AND $e=environment) OR (store_id=$s AND owner_inn='' AND environment='Unknown')) AND stage NOT IN('CodesRecovered','Rejected') AND id<>COALESCE($id,'') ORDER BY created_at,id LIMIT 1", ("$g", gtin), ("$o", profile.OwnerInn), ("$e", profile.Environment), ("$s", profile.StoreId), ("$id", exceptIntent));
        return cmd.ExecuteScalar() is string id ? ReadPurchase(c, null, id) : null;
    }
    public bool PurchaseHasConflicts(string intentId)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT COUNT(*) FROM kiz_codes_scoped WHERE intent_id=$i AND conflict=1", ("$i", intentId)); return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    private static PurchaseIntent? ReadPurchase(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var cmd = WfSql(c, tx, "SELECT request_json,request_key,stage,remote_order_id,retry_at,error_code FROM kiz_purchase_intents WHERE id=$id", ("$id", id)); using var row = cmd.ExecuteReader();
        return row.Read() ? new(id, JsonSerializer.Deserialize<PurchaseIntentRequest>(row.GetString(0))!, row.GetString(1), Enum.Parse<PurchaseStage>(row.GetString(2)), row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : DateTimeOffset.Parse(row.GetString(4)), row.IsDBNull(5) ? null : row.GetString(5)) : null;
    }
    public PurchaseIntent? FindOpenPurchase(KizScope scope, string gtin)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT id FROM kiz_purchase_intents WHERE store_id=$s AND owner_inn=$o AND environment=$e AND gtin=$g AND stage NOT IN('CodesRecovered','Rejected') ORDER BY created_at,id LIMIT 1", ("$s", scope.StoreId), ("$o", scope.OwnerInn), ("$e", scope.Environment), ("$g", gtin));
        return cmd.ExecuteScalar() is string id ? ReadPurchase(c, null, id) : null;
    }
    public bool PurchaseAuthorizationMatches(string id)
    {
        using var c = WorkflowConnection(); return PurchaseAuthorizationMatches(c, null, id);
    }
    private static bool PurchaseAuthorizationMatches(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var cmd = WfSql(c, tx, "SELECT i.request_json,i.authorization_version FROM kiz_purchase_intents i JOIN stores s ON s.id=i.store_id JOIN workflow_store_generations g ON g.store_id=i.store_id AND g.generation=i.generation JOIN kiz_profiles p ON p.store_id=i.store_id AND p.profile_id=i.profile_id AND p.current=1 WHERE i.id=$id", ("$id", id));
        using var row = cmd.ExecuteReader(); if (!row.Read()) return false;
        var request = JsonSerializer.Deserialize<PurchaseIntentRequest>(row.GetString(0))!; var authorization = row.GetString(1); row.Close();
        using var profile = WfSql(c, tx, "SELECT profile_json FROM kiz_profiles WHERE store_id=$s AND current=1", ("$s", request.Profile.StoreId));
        var current = JsonSerializer.Deserialize<SuzProfile>((string)profile.ExecuteScalar()!)!;
        using var configVersion = WfSql(c, tx, "SELECT value FROM kiz_crypto_metadata WHERE name='znak-credential-version'");
        var activeCredential = (string)configVersion.ExecuteScalar()!;
        return current.Id == request.Profile.Id && current.Version == request.Profile.Version && current.OwnerInn == request.Profile.OwnerInn && current.Environment == request.Profile.Environment && current.CredentialVersion == authorization && authorization == activeCredential;
    }
    public bool TryBeginPurchase(string intentId)
    {
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        if (!PurchaseAuthorizationMatches(c, tx, intentId)) return false;
        using var cmd = WfSql(c, tx, "UPDATE kiz_purchase_intents SET stage='CreateSending' WHERE id=$id AND stage IN('Draft','Validated') AND remote_order_id IS NULL", ("$id", intentId)); var ok = cmd.ExecuteNonQuery() == 1; tx.Commit(); return ok;
    }
    public void SavePurchaseOutcome(string intentId, PurchaseStage stage, string? remoteOrderId, DateTimeOffset? retryAt, string? errorCode)
    {
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        var old = ReadPurchase(c, tx, intentId); if (old is null) return; // Deleted stores are never recreated by workers.
        if(stage is PurchaseStage.Downloading or PurchaseStage.DownloadUnknown or PurchaseStage.Recovering or PurchaseStage.CodesRecovered){using var checkpoint=WfSql(c,tx,"INSERT OR IGNORE INTO kiz_receive_checkpoints SELECT id,store_id FROM kiz_purchase_intents WHERE id=$id",("$id",intentId));checkpoint.ExecuteNonQuery();}
        if (!string.IsNullOrWhiteSpace(remoteOrderId) && old.RemoteOrderId is not null && remoteOrderId != old.RemoteOrderId) throw new InvalidOperationException("remote_order_id_is_immutable");
        if (!PurchaseAuthorizationMatches(c, tx, intentId)) { stage = PurchaseStage.NeedsReconciliation; errorCode = "authorization_changed"; }
        using var cmd = WfSql(c, tx, "UPDATE kiz_purchase_intents SET stage=$st,remote_order_id=COALESCE(NULLIF($remote,''),remote_order_id),retry_at=$retry,error_code=$err WHERE id=$id", ("$st", stage.ToString()), ("$remote", remoteOrderId), ("$retry", retryAt?.ToString("O")), ("$err", errorCode), ("$id", intentId)); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public bool PurchaseReceiveStarted(string intentId)
    {using var c=WorkflowConnection();using var q=WfSql(c,null,"SELECT COUNT(*) FROM kiz_receive_checkpoints WHERE intent_id=$id",("$id",intentId));return Convert.ToInt64(q.ExecuteScalar())>0;}
    public void SavePurchaseBlock(string intentId, SuzBlock block)
    {
        var protector = CodeProtector;
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        var intent = ReadPurchase(c, tx, intentId); if (intent is null) return;
        if (intent.RemoteOrderId != block.OrderId || intent.Request.Gtin != block.Gtin || string.IsNullOrWhiteSpace(block.BlockId)) throw new InvalidOperationException("block_scope_mismatch");
        var rawJson = JsonSerializer.Serialize(block.Codes); var evidenceHash = protector.Identity(rawJson);
        using var previous = WfSql(c, tx, "SELECT evidence_hash,codes_enc FROM kiz_purchase_blocks WHERE intent_id=$i AND block_id=$b", ("$i", intentId), ("$b", block.BlockId));
        using (var row = previous.ExecuteReader())
        {
            if (row.Read() && row.GetString(0) != evidenceHash && JsonSerializer.Deserialize<string[]>(protector.Unprotect(row.GetString(1)))!.Length != 0)
                throw new InvalidOperationException("block_evidence_changed");
        }
        using var save = WfSql(c, tx, "INSERT INTO kiz_purchase_blocks VALUES($i,$b,$o,$g,$raw,$h) ON CONFLICT(intent_id,block_id) DO UPDATE SET codes_enc=excluded.codes_enc,evidence_hash=excluded.evidence_hash", ("$i", intentId), ("$b", block.BlockId), ("$o", block.OrderId), ("$g", block.Gtin), ("$raw", protector.Protect(rawJson)), ("$h", evidenceHash)); save.ExecuteNonQuery();
        if (PurchaseAuthorizationMatches(c, tx, intentId))
        {
            foreach (var code in block.Codes)
            {
                try { if (KizCodeIdentity.Gtin(code) != block.Gtin) continue; InsertScopedCode(c, tx, new(intent.Request.Profile.StoreId, intent.Request.Profile.OwnerInn, intent.Request.Profile.Environment), block.Gtin, code, "ReservedForJob", intentId, "SUZ"); }
                catch (InvalidOperationException) { /* Encrypted block remains authoritative evidence; invalid code is not stock. */ }
            }
        }
        tx.Commit();
    }
    public IReadOnlyList<SuzBlock> PurchaseBlocks(string intentId)
    {
        var protector = CodeProtector;
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT block_id,order_id,gtin,codes_enc FROM kiz_purchase_blocks WHERE intent_id=$id ORDER BY block_id", ("$id", intentId)); using var row = cmd.ExecuteReader(); var result = new List<SuzBlock>();
        while (row.Read()) result.Add(new(row.GetString(0), row.GetString(1), row.GetString(2), JsonSerializer.Deserialize<string[]>(protector.Unprotect(row.GetString(3)))!)); return result;
    }
    private void MigrateLegacyWorkflowEvidence(SqliteConnection c)
    {
        var pipelines = new List<(long Store, string Sku, string Gtin, string Stage, string Remote)>();
        using (var q = WfSql(c, null, "SELECT store_id,sku,gtin,stage,external_order_id FROM znak_pipeline WHERE stage IN('ERROR','POLLING','BUYING','CREATE_AMBIGUOUS')"))
        using (var row = q.ExecuteReader()) while (row.Read()) pipelines.Add((row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4)));
        foreach (var p in pipelines)
        {
            var profile = new SuzProfile("legacy-" + p.Store, p.Store, 1, "", "Unknown", "Unknown", "Unknown", "Unknown", 0, "legacy", false, false, new Uri("https://suzgrid.crpt.ru"), new Uri("https://markirovka.crpt.ru"));
            var identity = p.Remote == "" ? p.Sku : p.Remote;
            var request = new PurchaseIntentRequest("legacy-" + p.Store + "-" + identity + "-" + p.Gtin, 1, profile, p.Gtin, 0, "legacy-unverified");
            using var g = WfSql(c, null, "SELECT generation FROM workflow_store_generations WHERE store_id=$s", ("$s", p.Store)); var generation = Convert.ToInt32(g.ExecuteScalar() ?? 0);
            if (generation == 0) continue;
            InsertPurchase(c, null, Guid.NewGuid().ToString("N"), request, "legacy", p.Remote == "" ? PurchaseStage.NeedsReconciliation : PurchaseStage.Polling, p.Remote == "" ? null : p.Remote, generation);
        }
        var pool = new List<(string Raw, string Gtin)>();
        using (var q = WfSql(c, null, "SELECT code,gtin FROM kiz_pool")) using (var row = q.ExecuteReader()) while (row.Read()) pool.Add((row.GetString(0), row.GetString(1)));
        if (pool.Count == 0) return;
        var protector = CodeProtector;
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var code in pool) InsertScopedCode(c, tx, new(0, "", "Unknown"), code.Gtin, code.Raw, "NeedsVerification", null, "Legacy", allowInvalid: true);
        tx.Commit();
    }
}
