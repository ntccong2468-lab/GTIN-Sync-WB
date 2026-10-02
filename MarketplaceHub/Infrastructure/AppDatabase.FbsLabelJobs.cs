using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace MarketplaceHub.Infrastructure;
public sealed partial class AppDatabase
{
    private IKizCodeProtector? kizProtector;
    internal void ConfigureWorkflowCodeProtector(IKizCodeProtector protector)=>kizProtector=protector;
    public IKizCodeProtector CodeProtector => kizProtector ??= new KizCodeProtector(DbPath);
    private SqliteConnection WorkflowConnection() { var c = new SqliteConnection(ConnectionString); c.Open(); return c; }
    private static SqliteCommand WfSql(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private void InitializeWorkflowTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = """
CREATE TABLE IF NOT EXISTS fbs_label_jobs(id TEXT PRIMARY KEY,store_id INTEGER NOT NULL,marketplace TEXT NOT NULL,target_kind TEXT NOT NULL,target_id TEXT NOT NULL,revision INTEGER NOT NULL,snapshot_json TEXT NOT NULL,snapshot_hash TEXT NOT NULL,stage TEXT NOT NULL,active INTEGER NOT NULL,version INTEGER NOT NULL DEFAULT 1,claimed INTEGER NOT NULL DEFAULT 0,generation INTEGER NOT NULL,updated_at TEXT NOT NULL,artifacts_json TEXT NOT NULL DEFAULT '[]');
CREATE UNIQUE INDEX IF NOT EXISTS fbs_label_job_active ON fbs_label_jobs(store_id,marketplace,target_kind,target_id) WHERE active=1;
CREATE TABLE IF NOT EXISTS fbs_job_units(job_id TEXT NOT NULL,unit_key TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(job_id,unit_key));
CREATE TABLE IF NOT EXISTS fbs_job_authorizations(job_id TEXT PRIMARY KEY,profile_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS kiz_purchase_intents(id TEXT PRIMARY KEY,store_id INTEGER NOT NULL,job_id TEXT NOT NULL,revision INTEGER NOT NULL,profile_id TEXT NOT NULL,owner_inn TEXT NOT NULL,environment TEXT NOT NULL,gtin TEXT NOT NULL,request_json TEXT NOT NULL,request_key TEXT NOT NULL,stage TEXT NOT NULL,remote_order_id TEXT NULL,retry_at TEXT NULL,error_code TEXT NULL,generation INTEGER NOT NULL,authorization_version TEXT NOT NULL,created_at TEXT NOT NULL,UNIQUE(job_id,revision,profile_id,environment,gtin));
CREATE TABLE IF NOT EXISTS kiz_purchase_blocks(intent_id TEXT NOT NULL,block_id TEXT NOT NULL,order_id TEXT NOT NULL,gtin TEXT NOT NULL,codes_enc TEXT NOT NULL,evidence_hash TEXT NOT NULL,PRIMARY KEY(intent_id,block_id));
CREATE TABLE IF NOT EXISTS kiz_receive_checkpoints(intent_id TEXT PRIMARY KEY,store_id INTEGER NOT NULL);
INSERT OR IGNORE INTO kiz_receive_checkpoints SELECT id,store_id FROM kiz_purchase_intents WHERE stage IN('Downloading','DownloadUnknown','Recovering','CodesRecovered') OR EXISTS(SELECT 1 FROM kiz_purchase_blocks b WHERE b.intent_id=kiz_purchase_intents.id);
CREATE TABLE IF NOT EXISTS kiz_codes_scoped(code_hash TEXT PRIMARY KEY,cis_hash TEXT NOT NULL UNIQUE,store_id INTEGER NOT NULL,owner_inn TEXT NOT NULL,environment TEXT NOT NULL,gtin TEXT NOT NULL,raw_enc TEXT NOT NULL,allocation TEXT NOT NULL,legal_json TEXT NULL,intent_id TEXT NULL,source TEXT NOT NULL,conflict INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS kiz_unit_bindings(unit_key TEXT PRIMARY KEY,store_id INTEGER NOT NULL,code_hash TEXT NOT NULL UNIQUE,cis_hash TEXT NOT NULL UNIQUE,job_id TEXT NOT NULL,physical_at TEXT NULL,status TEXT NOT NULL DEFAULT 'Reserved');
CREATE TABLE IF NOT EXISTS kiz_profiles(profile_id TEXT NOT NULL,version INTEGER NOT NULL,store_id INTEGER NOT NULL,profile_json TEXT NOT NULL,current INTEGER NOT NULL DEFAULT 1,PRIMARY KEY(profile_id,version));
CREATE UNIQUE INDEX IF NOT EXISTS kiz_current_profile ON kiz_profiles(store_id) WHERE current=1;
CREATE TABLE IF NOT EXISTS workflow_store_generations(store_id INTEGER PRIMARY KEY,generation INTEGER NOT NULL DEFAULT 1,credential_version TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS kiz_crypto_metadata(name TEXT PRIMARY KEY,value TEXT NOT NULL);
INSERT OR IGNORE INTO workflow_store_generations(store_id,generation,credential_version) SELECT id,1,lower(hex(randomblob(16))) FROM stores;
INSERT OR IGNORE INTO kiz_crypto_metadata(name,value) VALUES('znak-credential-version',lower(hex(randomblob(16))));
UPDATE kiz_purchase_intents SET stage='CreateUnknown' WHERE stage='CreateSending';
UPDATE kiz_purchase_intents SET stage='DownloadUnknown' WHERE stage='Downloading';
"""; cmd.ExecuteNonQuery();
        MigrateLegacyWorkflowEvidence(c);
    }
    private static bool GenerationMatches(SqliteConnection c, SqliteTransaction? tx, long storeId, int generation)
    {
        using var cmd = WfSql(c, tx, "SELECT COUNT(*) FROM stores s JOIN workflow_store_generations g ON g.store_id=s.id WHERE s.id=$s AND g.generation=$g", ("$s", storeId), ("$g", generation));
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }
    public int StoreGeneration(long storeId)
    {
        using var c = WorkflowConnection();
        using var init = WfSql(c, null, "INSERT OR IGNORE INTO workflow_store_generations SELECT id,1,lower(hex(randomblob(16))) FROM stores WHERE id=$s", ("$s", storeId)); init.ExecuteNonQuery();
        using var read = WfSql(c, null, "SELECT generation FROM workflow_store_generations WHERE store_id=$s", ("$s", storeId)); return Convert.ToInt32(read.ExecuteScalar() ?? 0);
    }
    public bool StoreGenerationMatches(long storeId, int generation) { using var c = WorkflowConnection(); return GenerationMatches(c, null, storeId, generation); }
    public string ZnakCredentialVersion()
    {
        using var c = WorkflowConnection(); using var read = WfSql(c, null, "SELECT value FROM kiz_crypto_metadata WHERE name='znak-credential-version'"); return (string)read.ExecuteScalar()!;
    }
    public FbsLabelJob GetOrCreateLabelJob(LabelJobSnapshot snapshot)
    {
        var protector = CodeProtector; // Initialize outside the write transaction.
        if (snapshot.Units.Select(x => x.Unit).Distinct().Count() != snapshot.Units.Count || snapshot.Units.Any(x => x.Unit.StoreId != snapshot.Target.StoreId || x.Unit.Marketplace != snapshot.Target.Marketplace || x.Unit.UnitIndex < 0))
            throw new InvalidOperationException("invalid_unit_snapshot");
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        if (!GenerationMatches(c, tx, snapshot.Target.StoreId, snapshot.StoreGeneration)) throw new InvalidOperationException("store_generation_changed");
        using var read = WfSql(c, tx, "SELECT id FROM fbs_label_jobs WHERE store_id=$s AND marketplace=$m AND target_kind=$k AND target_id=$t AND active=1",
            ("$s", snapshot.Target.StoreId), ("$m", snapshot.Target.Marketplace.ToString()), ("$k", snapshot.Target.Kind.ToString()), ("$t", snapshot.Target.TargetId));
        var id = read.ExecuteScalar() as string; var hash = WorkflowIdentity.Snapshot(snapshot);
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            using var insert = WfSql(c, tx, "INSERT INTO fbs_label_jobs(id,store_id,marketplace,target_kind,target_id,revision,snapshot_json,snapshot_hash,stage,active,generation,updated_at) VALUES($id,$s,$m,$k,$t,1,$j,$h,'Queued',1,$g,$at)",
                ("$id", id), ("$s", snapshot.Target.StoreId), ("$m", snapshot.Target.Marketplace.ToString()), ("$k", snapshot.Target.Kind.ToString()), ("$t", snapshot.Target.TargetId),
                ("$j", JsonSerializer.Serialize(snapshot)), ("$h", hash), ("$g", snapshot.StoreGeneration), ("$at", DateTimeOffset.UtcNow.ToString("O"))); insert.ExecuteNonQuery();
            foreach (var u in snapshot.Units) { using var unit = WfSql(c, tx, "INSERT INTO fbs_job_units VALUES($id,$u,$r)", ("$id", id), ("$u", WorkflowIdentity.Unit(u.Unit)), ("$r", JsonSerializer.Serialize(new UnitWorkflowResult(u.Unit, "Queued", null, null)))); unit.ExecuteNonQuery(); }
        }
        else
        {
            using var change = WfSql(c, tx, "UPDATE fbs_label_jobs SET stage='SnapshotChanged',version=version+1 WHERE id=$id AND snapshot_hash<>$h AND stage<>'SnapshotChanged'", ("$id", id), ("$h", hash)); change.ExecuteNonQuery();
        }
        tx.Commit(); return GetLabelJob(id)!;
    }
    public FbsLabelJob? GetLabelJob(string jobId)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT revision,snapshot_json,snapshot_hash,stage,active,version,updated_at FROM fbs_label_jobs WHERE id=$id", ("$id", jobId)); using var row = cmd.ExecuteReader();
        return row.Read() ? new(jobId, row.GetInt32(0), JsonSerializer.Deserialize<LabelJobSnapshot>(row.GetString(1))!, row.GetString(2), Enum.Parse<FbsLabelJobStage>(row.GetString(3)), row.GetInt32(4) != 0, row.GetInt64(5), DateTimeOffset.Parse(row.GetString(6))) : null;
    }
    public bool TryClaimLabelJob(string jobId, long expectedVersion)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "UPDATE fbs_label_jobs SET claimed=1 WHERE id=$id AND version=$v AND claimed=0 AND active=1 AND EXISTS(SELECT 1 FROM stores s JOIN workflow_store_generations g ON g.store_id=s.id WHERE s.id=fbs_label_jobs.store_id AND g.generation=fbs_label_jobs.generation)", ("$id", jobId), ("$v", expectedVersion)); return cmd.ExecuteNonQuery() == 1;
    }
    public void ReleaseLabelJob(string jobId) { using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "UPDATE fbs_label_jobs SET claimed=0 WHERE id=$id", ("$id", jobId)); cmd.ExecuteNonQuery(); }
    public bool TrySaveLabelJob(string jobId, long expectedVersion, FbsLabelJobStage stage, IReadOnlyList<UnitWorkflowResult> units)
    {
        using var c = WorkflowConnection(); using var tx = c.BeginTransaction(deferred: false);
        using var update = WfSql(c, tx, "UPDATE fbs_label_jobs SET stage=$st,version=version+1,updated_at=$at WHERE id=$id AND version=$v AND EXISTS(SELECT 1 FROM stores s JOIN workflow_store_generations g ON g.store_id=s.id WHERE s.id=fbs_label_jobs.store_id AND g.generation=fbs_label_jobs.generation)", ("$st", stage.ToString()), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", jobId), ("$v", expectedVersion));
        if (update.ExecuteNonQuery() != 1) return false;
        foreach (var u in units) { using var cmd = WfSql(c, tx, "UPDATE fbs_job_units SET result_json=$r WHERE job_id=$id AND unit_key=$u", ("$r", JsonSerializer.Serialize(u)), ("$id", jobId), ("$u", WorkflowIdentity.Unit(u.Unit))); if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("unit_outside_job"); }
        tx.Commit(); return true;
    }
    public IReadOnlyList<UnitWorkflowResult> LabelJobUnits(string jobId)
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "SELECT result_json FROM fbs_job_units WHERE job_id=$id ORDER BY unit_key", ("$id", jobId)); using var row = cmd.ExecuteReader(); var result = new List<UnitWorkflowResult>();
        while (row.Read()) result.Add(JsonSerializer.Deserialize<UnitWorkflowResult>(row.GetString(0))!); return result;
    }
    public void RecoverWorkflowClaims()
    {
        using var c = WorkflowConnection(); using var cmd = WfSql(c, null, "UPDATE fbs_label_jobs SET claimed=0; UPDATE kiz_purchase_intents SET stage='CreateUnknown' WHERE stage='CreateSending'; UPDATE kiz_purchase_intents SET stage='DownloadUnknown' WHERE stage='Downloading'"); cmd.ExecuteNonQuery();
    }
    public SuzProfile? LabelJobProfile(string jobId)
    {
        using var c=WorkflowConnection();using var tx=c.BeginTransaction(deferred:false);
        using var job=WfSql(c,tx,"SELECT store_id FROM fbs_label_jobs WHERE id=$j",("$j",jobId));var store=job.ExecuteScalar();if(store is null)return null;
        using var saved=WfSql(c,tx,"SELECT profile_json FROM fbs_job_authorizations WHERE job_id=$j",("$j",jobId));var json=saved.ExecuteScalar() as string;
        if(json is null){using var current=WfSql(c,tx,"SELECT profile_json FROM kiz_profiles WHERE store_id=$s AND current=1",("$s",store));json=current.ExecuteScalar() as string;if(json is not null){using var bind=WfSql(c,tx,"INSERT INTO fbs_job_authorizations VALUES($j,$p)",("$j",jobId),("$p",json));bind.ExecuteNonQuery();}}
        tx.Commit();return json is null?null:JsonSerializer.Deserialize<SuzProfile>(json);
    }
    public bool WorkflowAuthorizationMatches(FbsWorkflowContext context,bool requireActive=true)
    {try{using var c=WorkflowConnection();ValidateWorkflowContext(c,null,context,requireActive);return true;}catch(InvalidOperationException){return false;}}
    public IReadOnlyList<LabelArtifact> LabelJobArtifacts(string jobId)
    {using var c=WorkflowConnection();using var q=WfSql(c,null,"SELECT artifacts_json FROM fbs_label_jobs WHERE id=$j",("$j",jobId));return q.ExecuteScalar() is string json?JsonSerializer.Deserialize<LabelArtifact[]>(json)!:Array.Empty<LabelArtifact>();}
    public bool SaveLabelJobArtifacts(FbsWorkflowContext context,IReadOnlyList<LabelArtifact> artifacts)
    {
        using var c=WorkflowConnection();using var tx=c.BeginTransaction(deferred:false);try{ValidateWorkflowContext(c,tx,context,requireActive:false);}catch(InvalidOperationException){return false;}
        if(artifacts.Any(x=>x.JobId!=context.JobId||x.Revision!=context.Revision||x.Units.Any(u=>!context.Snapshot.Units.Any(d=>d.Unit==u))))throw new InvalidOperationException("artifact_outside_job");
        using var q=WfSql(c,tx,"UPDATE fbs_label_jobs SET artifacts_json=$a WHERE id=$j",("$j",context.JobId),("$a",JsonSerializer.Serialize(artifacts)));q.ExecuteNonQuery();tx.Commit();return true;
    }
    public IReadOnlyList<FbsLabelJob> ActiveLabelJobs(long? storeId=null)
    {using var c=WorkflowConnection();using var q=WfSql(c,null,"SELECT id FROM fbs_label_jobs WHERE active=1 AND ($s IS NULL OR store_id=$s) ORDER BY updated_at DESC",("$s",storeId));using var rows=q.ExecuteReader();var ids=new List<string>();while(rows.Read())ids.Add(rows.GetString(0));rows.Close();return ids.Select(id=>GetLabelJob(id)!).ToArray();}
    public IReadOnlyList<PurchaseIntent> LabelJobPurchases(string jobId)
    {using var c=WorkflowConnection();using var q=WfSql(c,null,"SELECT id FROM kiz_purchase_intents WHERE job_id=$j ORDER BY id",("$j",jobId));using var rows=q.ExecuteReader();var ids=new List<string>();while(rows.Read())ids.Add(rows.GetString(0));rows.Close();return ids.Select(id=>GetPurchaseIntent(id)!).ToArray();}
    public FbsLabelJob NewLabelRevision(string oldId,LabelJobSnapshot fresh)
    {
        var old=GetLabelJob(oldId)??throw new InvalidOperationException("job_deleted");if(old.Snapshot.Target!=fresh.Target)throw new InvalidOperationException("revision_target_changed");
        if(fresh.Units.Select(x=>x.Unit).Distinct().Count()!=fresh.Units.Count||fresh.Units.Any(x=>x.Unit.StoreId!=fresh.Target.StoreId||x.Unit.Marketplace!=fresh.Target.Marketplace||x.Unit.UnitIndex<0))throw new InvalidOperationException("invalid_unit_snapshot");
        var next=Guid.NewGuid().ToString("N");using var c=WorkflowConnection();using var tx=c.BeginTransaction(deferred:false);
        if(!GenerationMatches(c,tx,fresh.Target.StoreId,fresh.StoreGeneration))throw new InvalidOperationException("store_generation_changed");
        using var close=WfSql(c,tx,"UPDATE fbs_label_jobs SET active=0 WHERE id=$j AND claimed=0 AND active=1 AND version=$v",("$j",oldId),("$v",old.Version));if(close.ExecuteNonQuery()!=1)throw new InvalidOperationException("job_busy");
        using var insert=WfSql(c,tx,"INSERT INTO fbs_label_jobs(id,store_id,marketplace,target_kind,target_id,revision,snapshot_json,snapshot_hash,stage,active,generation,updated_at) VALUES($j,$s,$m,$kind,$target,$r,$snapshot,$hash,'Queued',1,$g,$at)",("$j",next),("$s",fresh.Target.StoreId),("$m",fresh.Target.Marketplace.ToString()),("$kind",fresh.Target.Kind.ToString()),("$target",fresh.Target.TargetId),("$r",old.Revision+1),("$snapshot",JsonSerializer.Serialize(fresh)),("$hash",WorkflowIdentity.Snapshot(fresh)),("$g",fresh.StoreGeneration),("$at",DateTimeOffset.UtcNow.ToString("O")));insert.ExecuteNonQuery();
        foreach(var demand in fresh.Units){using var unit=WfSql(c,tx,"INSERT INTO fbs_job_units VALUES($j,$u,$r)",("$j",next),("$u",WorkflowIdentity.Unit(demand.Unit)),("$r",JsonSerializer.Serialize(new UnitWorkflowResult(demand.Unit,"Queued",null,null))));unit.ExecuteNonQuery();}
        tx.Commit();return GetLabelJob(next)!;
    }
    private static void DeleteWorkflowStore(SqliteConnection c, SqliteTransaction tx, long id)
    {
        using var cmd = WfSql(c, tx, """
UPDATE workflow_store_generations SET generation=generation+1,credential_version=lower(hex(randomblob(16))) WHERE store_id=$s;
DELETE FROM fbs_job_units WHERE job_id IN(SELECT id FROM fbs_label_jobs WHERE store_id=$s);
DELETE FROM fbs_job_authorizations WHERE job_id IN(SELECT id FROM fbs_label_jobs WHERE store_id=$s);
DELETE FROM kiz_purchase_blocks WHERE intent_id IN(SELECT id FROM kiz_purchase_intents WHERE store_id=$s);
DELETE FROM kiz_receive_checkpoints WHERE store_id=$s;
DELETE FROM kiz_purchase_intents WHERE store_id=$s;
DELETE FROM fbs_label_jobs WHERE store_id=$s;
DELETE FROM kiz_unit_bindings WHERE store_id=$s;
DELETE FROM kiz_codes_scoped WHERE store_id=$s;
DELETE FROM kiz_profiles WHERE store_id=$s;
""", ("$s", id)); cmd.ExecuteNonQuery();
    }
}
