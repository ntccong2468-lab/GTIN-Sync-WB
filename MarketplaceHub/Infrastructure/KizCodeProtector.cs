using MarketplaceHub.Core;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
namespace MarketplaceHub.Infrastructure;
public sealed class KizCodeProtector : IKizCodeProtector
{
    private readonly byte[] key;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarketplaceHub-kiz-v1");
    public KizCodeProtector(string dbPath)
    {
        using var c = new SqliteConnection("Data Source=" + dbPath); c.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT OR IGNORE INTO kiz_crypto_metadata(name,value) VALUES('hmac-key',$v)";
        insert.Parameters.AddWithValue("$v", Convert.ToBase64String(ProtectedData.Protect(RandomNumberGenerator.GetBytes(32), Entropy, DataProtectionScope.CurrentUser)));
        insert.ExecuteNonQuery();
        using var read = c.CreateCommand(); read.Transaction = tx; read.CommandText = "SELECT value FROM kiz_crypto_metadata WHERE name='hmac-key'";
        key = ProtectedData.Unprotect(Convert.FromBase64String((string)read.ExecuteScalar()!), Entropy, DataProtectionScope.CurrentUser); tx.Commit();
    }
    public string Protect(string raw) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(raw), Entropy, DataProtectionScope.CurrentUser));
    public string Unprotect(string encrypted) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser));
    private string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    public string Identity(string raw) => Hash("raw\0" + KizCodeIdentity.Canonical(raw));
    public string CisIdentity(string raw) => Hash("cis\0" + KizCodeIdentity.Cis(raw));
}
