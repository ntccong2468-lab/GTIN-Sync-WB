using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
namespace MarketplaceHub.Infrastructure;
public sealed partial class AppDatabase
{
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
