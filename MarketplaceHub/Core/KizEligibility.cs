namespace MarketplaceHub.Core;
public sealed record KizScope(long StoreId, string OwnerInn, string Environment);
public sealed record KizLegalProof(string CodeHash, string Gtin, string OwnerInn, string Environment, string RawStatus, string StatusEx, string PackageType, DateTimeOffset ObservedAt, string Source);
public sealed record PhysicalMarkEvidence(FbsUnitKey Unit, string CodeHash, DateTimeOffset ConfirmedAt);
public interface IKizCodeProtector
{
    string Protect(string raw);
    string Unprotect(string encrypted);
    string Identity(string raw);
    string CisIdentity(string raw);
}
public static class KizCodeIdentity
{
    public static string Canonical(string raw)
    {
        if (raw.StartsWith("]d2", StringComparison.Ordinal) || raw.StartsWith("]D2", StringComparison.Ordinal)) raw = raw[3..];
        return raw.TrimStart('\u001d'); // Internal GS boundaries and crypto tail are evidence.
    }
    public static string Cis(string raw)
    {
        var code = Canonical(raw);
        if (code.Length < 19 || !code.StartsWith("01", StringComparison.Ordinal) || code.Substring(16, 2) != "21" || GtinCode.Normalize(code.Substring(2, 14)) == "")
            throw new InvalidOperationException("invalid_kiz_identifier");
        var serialEnd = code.IndexOf('\u001d', 18);
        // lp compact scanner form: 13 serial characters followed by AI 91.
        if (serialEnd < 0 && code.Length >= 33 && code.Substring(31, 2) == "91") serialEnd = 31;
        if (serialEnd < 0) serialEnd = code.Length;
        var serial = code[18..serialEnd];
        if (serial.Length is < 1 or > 20) throw new InvalidOperationException("invalid_kiz_serial");
        return code.Substring(2, 14) + "\u001d" + serial;
    }
    public static string Gtin(string raw) => Cis(raw)[..14];
}
