using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace MarketplaceHub.Infrastructure;
public sealed partial class AppDatabase
{
    public void ImportScopedKiz(KizScope scope, string raw, string source)
    {
        var protector=CodeProtector; var gtin=KizCodeIdentity.Gtin(raw);
        using var c=WorkflowConnection(); using var tx=c.BeginTransaction(deferred:false);
        using var store=WfSql(c,tx,"SELECT COUNT(*) FROM stores WHERE id=$s",("$s",scope.StoreId));
        if(Convert.ToInt64(store.ExecuteScalar())!=1 || scope.OwnerInn=="" || scope.Environment=="Unknown")throw new InvalidOperationException("explicit_kiz_scope_required");
        // An explicit seller import can attribute unowned legacy evidence, never an owned code.
        using var adopt=WfSql(c,tx,"UPDATE kiz_codes_scoped SET store_id=$s,owner_inn=$o,environment=$e,source=$source WHERE code_hash=$h AND store_id=0 AND source='Legacy' AND conflict=0 AND NOT EXISTS(SELECT 1 FROM kiz_unit_bindings b WHERE b.code_hash=kiz_codes_scoped.code_hash)",("$s",scope.StoreId),("$o",scope.OwnerInn),("$e",scope.Environment),("$h",protector.Identity(raw)),("$source",source));adopt.ExecuteNonQuery();
        InsertScopedCode(c,tx,scope,gtin,raw,"NeedsVerification",null,source);tx.Commit();
    }
    public IReadOnlyDictionary<FbsUnitKey,string> ReserveScopedKiz(FbsWorkflowContext context,IReadOnlyList<FbsUnitDemand> units)
    {
        var protector=CodeProtector;var profile=context.Profile;
        if(profile is null)return new Dictionary<FbsUnitKey,string>();
        using var c=WorkflowConnection();using var tx=c.BeginTransaction(deferred:false);
        ValidateWorkflowContext(c,tx,context);
        var result=new Dictionary<FbsUnitKey,string>();
        foreach(var demand in units.OrderBy(x=>WorkflowIdentity.Unit(x.Unit),StringComparer.Ordinal)){
            if(!context.Snapshot.Units.Contains(demand))throw new InvalidOperationException("unit_outside_snapshot");
            if(!demand.RequiresKiz)continue;
            using var existing=WfSql(c,tx,"SELECT k.raw_enc,k.gtin,k.owner_inn,k.environment,k.store_id,k.conflict FROM kiz_unit_bindings b JOIN kiz_codes_scoped k ON k.code_hash=b.code_hash WHERE b.unit_key=$u",("$u",WorkflowIdentity.Unit(demand.Unit)));
            using(var row=existing.ExecuteReader())if(row.Read()){
                if(row.GetString(1)!=demand.Gtin||row.GetString(2)!=profile.OwnerInn||row.GetString(3)!=profile.Environment||row.GetInt64(4)!=profile.StoreId||row.GetInt32(5)!=0)throw new InvalidOperationException("binding_scope_conflict");
                result[demand.Unit]=protector.Unprotect(row.GetString(0));continue;
            }
            string? raw=null,hash=null,cis=null;
            using(var stock=WfSql(c,tx,"SELECT k.raw_enc,k.code_hash,k.cis_hash FROM kiz_codes_scoped k LEFT JOIN kiz_purchase_intents i ON i.id=k.intent_id WHERE k.store_id=$s AND k.owner_inn=$o AND k.environment=$e AND k.gtin=$g AND k.conflict=0 AND k.allocation IN('NeedsVerification','Available','ReservedForJob') AND (k.intent_id IS NULL OR i.job_id=$j) AND NOT EXISTS(SELECT 1 FROM kiz_unit_bindings b WHERE b.code_hash=k.code_hash) ORDER BY k.code_hash LIMIT 1",("$s",profile.StoreId),("$o",profile.OwnerInn),("$e",profile.Environment),("$g",demand.Gtin),("$j",context.JobId)))
            using(var row=stock.ExecuteReader())if(row.Read()){raw=protector.Unprotect(row.GetString(0));hash=row.GetString(1);cis=row.GetString(2);}
            if(raw is null)continue;
            // Existing marketplace journals remain authoritative owners of physical codes.
            GuardKizAliasOwnership(c,tx,raw);
            using var owner=WfSql(c,tx,"SELECT (SELECT COUNT(*) FROM wb_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM marketplace_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM kiz_pool WHERE code=$c AND (status!='AVAILABLE' OR assigned_order!=''))",("$c",raw));
            if(Convert.ToInt64(owner.ExecuteScalar())!=0)throw new InvalidOperationException("legacy_code_already_owned");
            using var bind=WfSql(c,tx,"INSERT INTO kiz_unit_bindings(unit_key,store_id,code_hash,cis_hash,job_id) VALUES($u,$s,$h,$cis,$j)",("$u",WorkflowIdentity.Unit(demand.Unit)),("$s",profile.StoreId),("$h",hash),("$cis",cis),("$j",context.JobId));bind.ExecuteNonQuery();
            using var reserve=WfSql(c,tx,"UPDATE kiz_codes_scoped SET allocation='Reserved' WHERE code_hash=$h",("$h",hash));reserve.ExecuteNonQuery();result[demand.Unit]=raw;
        }
        tx.Commit();return result;
    }
    public void BindAssignedKiz(FbsWorkflowContext context,IReadOnlyDictionary<FbsUnitKey,string> codes)
    {
        var protector=CodeProtector;if(context.Profile is null && codes.Count>0)throw new InvalidOperationException("profile_required");if(context.Profile is null)return;
        using var c=WorkflowConnection();using var tx=c.BeginTransaction(deferred:false);ValidateWorkflowContext(c,tx,context);
        foreach(var pair in codes){
            var demand=context.Snapshot.Units.SingleOrDefault(x=>x.Unit==pair.Key&&x.RequiresKiz)??throw new InvalidOperationException("remote_unit_outside_job");
            if(KizCodeIdentity.Gtin(pair.Value)!=demand.Gtin)throw new InvalidOperationException("remote_gtin_mismatch");
            if(pair.Key.Marketplace==Marketplace.Wildberries)ValidateWbKizOwnership(c,tx,pair.Key.StoreId,pair.Key.OrderId,demand.Gtin,pair.Value);
            else {using var other=WfSql(c,tx,"SELECT (SELECT COUNT(*) FROM wb_kiz_reservations WHERE code=$c)+(SELECT COUNT(*) FROM marketplace_kiz_reservations WHERE code=$c AND (store_id!=$s OR marketplace!=$m OR order_id!=$o OR item_id!=$i OR unit_index!=$u OR gtin!=$g))",("$c",pair.Value),("$s",pair.Key.StoreId),("$m",pair.Key.Marketplace.ToString()),("$o",pair.Key.OrderId),("$i",pair.Key.ItemId),("$u",pair.Key.UnitIndex),("$g",demand.Gtin));if(Convert.ToInt64(other.ExecuteScalar())>0)throw new InvalidOperationException("remote_owner_conflict");}
            using var adopt=WfSql(c,tx,"UPDATE kiz_codes_scoped SET store_id=$s,owner_inn=$o,environment=$e WHERE code_hash=$h AND store_id=0 AND source='Legacy'",("$s",context.Profile.StoreId),("$o",context.Profile.OwnerInn),("$e",context.Profile.Environment),("$h",protector.Identity(pair.Value)));adopt.ExecuteNonQuery();
            InsertScopedCode(c,tx,new(context.Profile.StoreId,context.Profile.OwnerInn,context.Profile.Environment),demand.Gtin,pair.Value,"Reserved",null,"RemoteReadback");
            using var binding=WfSql(c,tx,"INSERT OR IGNORE INTO kiz_unit_bindings(unit_key,store_id,code_hash,cis_hash,job_id) VALUES($u,$s,$h,$cis,$j)",("$u",WorkflowIdentity.Unit(pair.Key)),("$s",pair.Key.StoreId),("$h",protector.Identity(pair.Value)),("$cis",protector.CisIdentity(pair.Value)),("$j",context.JobId));binding.ExecuteNonQuery();
            using var verify=WfSql(c,tx,"SELECT COUNT(*) FROM kiz_unit_bindings b JOIN kiz_codes_scoped k ON k.code_hash=b.code_hash WHERE b.unit_key=$u AND b.code_hash=$h AND k.store_id=$s AND k.owner_inn=$o AND k.environment=$e AND k.conflict=0",("$u",WorkflowIdentity.Unit(pair.Key)),("$h",protector.Identity(pair.Value)),("$s",context.Profile.StoreId),("$o",context.Profile.OwnerInn),("$e",context.Profile.Environment));if(Convert.ToInt64(verify.ExecuteScalar())!=1)throw new InvalidOperationException("remote_binding_conflict");
        }tx.Commit();
    }
    public void SaveLegalProof(KizScope scope,KizLegalProof proof)
    {
        using var c=WorkflowConnection();using var cmd=WfSql(c,null,"UPDATE kiz_codes_scoped SET legal_json=$j WHERE code_hash=$h AND store_id=$s AND owner_inn=$o AND environment=$e AND gtin=$g AND conflict=0 AND EXISTS(SELECT 1 FROM stores WHERE id=$s)",("$j",JsonSerializer.Serialize(proof)),("$h",proof.CodeHash),("$s",scope.StoreId),("$o",scope.OwnerInn),("$e",scope.Environment),("$g",proof.Gtin));
        if(scope.OwnerInn!=proof.OwnerInn||scope.Environment!=proof.Environment)throw new InvalidOperationException("legal_scope_mismatch");cmd.ExecuteNonQuery();
    }
    public void ConfirmPhysicalMark(PhysicalMarkEvidence evidence)
    {
        using var c=WorkflowConnection();using var cmd=WfSql(c,null,"UPDATE kiz_unit_bindings SET physical_at=$at WHERE unit_key=$u AND code_hash=$h AND store_id=$s AND EXISTS(SELECT 1 FROM stores WHERE id=$s)",("$u",WorkflowIdentity.Unit(evidence.Unit)),("$h",evidence.CodeHash),("$s",evidence.Unit.StoreId),("$at",evidence.ConfirmedAt.ToString("O")));if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("physical_evidence_binding_mismatch");
    }
    public bool PhysicalMarkMatches(FbsUnitKey unit,string hash)
    {using var c=WorkflowConnection();using var q=WfSql(c,null,"SELECT COUNT(*) FROM kiz_unit_bindings WHERE unit_key=$u AND code_hash=$h AND physical_at IS NOT NULL",("$u",WorkflowIdentity.Unit(unit)),("$h",hash));return Convert.ToInt64(q.ExecuteScalar())==1;}
    private void ValidateWorkflowContext(SqliteConnection c,SqliteTransaction? tx,FbsWorkflowContext context)
    {
        if(!GenerationMatches(c,tx,context.Snapshot.Target.StoreId,context.Snapshot.StoreGeneration))throw new InvalidOperationException("store_generation_changed");
        using var job=WfSql(c,tx,"SELECT COUNT(*) FROM fbs_label_jobs WHERE id=$j AND revision=$r AND active=1 AND snapshot_hash=$h",("$j",context.JobId),("$r",context.Revision),("$h",WorkflowIdentity.Snapshot(context.Snapshot)));if(Convert.ToInt64(job.ExecuteScalar())!=1)throw new InvalidOperationException("job_snapshot_changed");
        if(context.Profile is {} p){using var profile=WfSql(c,tx,"SELECT profile_json FROM kiz_profiles WHERE store_id=$s AND current=1",("$s",context.Snapshot.Target.StoreId));if(profile.ExecuteScalar() is not string json||JsonSerializer.Deserialize<SuzProfile>(json)!=p)throw new InvalidOperationException("profile_version_changed");using var v=WfSql(c,tx,"SELECT value FROM kiz_crypto_metadata WHERE name='znak-credential-version'");if((string)v.ExecuteScalar()! !=p.CredentialVersion)throw new InvalidOperationException("credential_version_changed");}
    }
    private void InsertScopedCode(SqliteConnection c, SqliteTransaction tx, KizScope scope, string gtin, string raw, string allocation, string? intentId, string source, bool allowInvalid = false)
    {
        var hash = CodeProtector.Identity(raw); string cis;
        try { cis = CodeProtector.CisIdentity(raw); }
        catch (InvalidOperationException) when (allowInvalid) { cis = "INVALID-" + hash; }
        using var read = WfSql(c, tx, "SELECT code_hash,store_id,owner_inn,environment FROM kiz_codes_scoped WHERE cis_hash=$cis", ("$cis", cis));
        string? existing; bool scopeConflict;
        using (var row = read.ExecuteReader()) { existing = row.Read() ? row.GetString(0) : null; scopeConflict = existing is not null && (row.GetInt64(1) != scope.StoreId || row.GetString(2) != scope.OwnerInn || row.GetString(3) != scope.Environment); }
        if (existing is not null)
        {
            // Migration reads may observe an already scoped code; never poison its provenance.
            if (source != "Legacy" && (existing != hash || scopeConflict))
            { using var conflict = WfSql(c, tx, "UPDATE kiz_codes_scoped SET conflict=1,allocation='Quarantined' WHERE cis_hash=$cis", ("$cis", cis)); conflict.ExecuteNonQuery(); }
            return;
        }
        using var insert = WfSql(c, tx, "INSERT INTO kiz_codes_scoped(code_hash,cis_hash,store_id,owner_inn,environment,gtin,raw_enc,allocation,intent_id,source) VALUES($h,$cis,$s,$o,$e,$g,$raw,$a,$i,$source)",
            ("$h", hash), ("$cis", cis), ("$s", scope.StoreId), ("$o", scope.OwnerInn), ("$e", scope.Environment), ("$g", gtin), ("$raw", CodeProtector.Protect(raw)), ("$a", allocation), ("$i", intentId), ("$source", source)); insert.ExecuteNonQuery();
    }
}
