using MarketplaceHub.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MarketplaceHub.Services.Suz;
public sealed class TrueApiKizReader(SuzHttpClient auth, OperationPolicy policy, HttpClient http, IWorkflowClock clock, IKizCodeProtector protector) : IKizLegalReader
{
    public async Task<IReadOnlyList<KizLegalProof>> ReadAsync(SuzProfile profile, IReadOnlyList<string> rawCodes, CancellationToken ct)
    {
        var result = new List<KizLegalProof>();
        var codes = new Dictionary<string, string>(StringComparer.Ordinal); var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawCodes)
        {
            try { var cis = protector.CisIdentity(raw); if (codes.TryGetValue(cis, out var previous) && protector.Identity(previous) != protector.Identity(raw)) conflicted.Add(cis); else codes[cis] = raw; }
            catch (InvalidOperationException) { }
        }
        foreach (var cis in conflicted) codes.Remove(cis);
        var token = await auth.TokenAsync(profile, false, ct);
        if (token.Kind != SuzOutcomeKind.Confirmed) return result;
        foreach (var batch in codes.Chunk(100))
        {
            var requested = batch.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            // True API receives the identification part, not the crypto tail.
            var identifiers = batch.Select(x => "01" + KizCodeIdentity.Gtin(x.Value) + "21" + KizCodeIdentity.Cis(x.Value)[15..]).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(identifiers);
            var endpoint = new Uri(profile.TrueApiBaseUri, "/api/v3/true-api/cises/info?pg=" + Uri.EscapeDataString(profile.ProductGroup));
            var response = await policy.WithClient(http).SendAsync(OperationKind.TrueApiCisesInfo, () => { var q = SuzHttpClient.Message(HttpMethod.Post, endpoint, bytes); q.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Value); return q; }, ct);
            if (SuzHttpClient.Json(response) is not JsonArray rows) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var identifier = SuzHttpClient.Text(row, "requestedCis"); if (identifier == "" || SuzHttpClient.Text(row, "errorCode") != "") continue;
                string cis; try { cis = protector.CisIdentity(identifier); } catch (InvalidOperationException) { continue; }
                if (!requested.TryGetValue(cis, out var raw) || !seen.Add(cis)) continue;
                var info = row?["cisInfo"] as JsonObject; if (info is null || info["errorCode"] is not null) continue;
                var gtin = GtinCode.Normalize(SuzHttpClient.Text(info, "gtin")); var owner = SuzHttpClient.Text(info, "ownerInn");
                var status = SuzHttpClient.Text(info, "status"); var package = SuzHttpClient.Text(info, "packageType");
                if (gtin != KizCodeIdentity.Gtin(raw) || owner == "" || status == "" || package == "") continue;
                result.Add(new(protector.Identity(raw), gtin, owner, profile.Environment, status, SuzHttpClient.Text(info, "statusEx"), package, clock.UtcNow, "TrueAPI:cises/info"));
            }
        }
        return result;
    }
}
