using System.Text.Json.Nodes;

namespace GTINSyncWB;

public sealed class Shop { public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string Name { get; set; } = ""; public string ProtectedToken { get; set; } = ""; }
public sealed class Settings
{
    public List<Shop> Shops { get; set; } = [];
    public string ProtectedCatalogKey { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Since { get; set; } = "2000-01-01";
    public bool DarkMode { get; set; }
    public Dictionary<string,string> SizeRules { get; set; } = new();
    public Dictionary<string,string> ColorRules { get; set; } = new();
    public Dictionary<string,string> ProductRules { get; set; } = new();
}
public sealed record CatalogItem(string Gtin,string Model,string Name,string Brand,string Color,string Size,string Status,DateTimeOffset SyncedAt,string Source, bool Accessible = true);
public sealed record Listing(string ShopId,string ShopName,long NmId,string VendorCode,string Title,string Color,string Photo,JsonObject Raw,DateTimeOffset SyncedAt);
public enum MatchStatus { Exact, Existing, Missing, Multiple, Conflict, Unpublished, AccessDenied, Stale, Updated, Failed, Queued, Sending, Received, Verifying, Unknown, Review }
public sealed class MatchRow
{
    public bool Selected { get; set; }
    public string ShopId { get; init; } = "";
    public string Shop { get; init; } = "";
    public long NmId { get; init; }
    public string VendorCode { get; init; } = "";
    public string Color { get; init; } = "";
    public string WbSize { get; init; } = "";
    public long ChrtId { get; init; }
    public string Gtin { get; set; } = "";
    public string Source { get; set; } = "";
    public MatchStatus Status { get; set; }
    public string Detail { get; set; } = "";
}
public sealed record HistoryEntry(DateTimeOffset At,string Shop,long NmId,long ChrtId,string Gtin,string Result,string Detail);
public static class Json
{
    public static string S(JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    public static long L(JsonNode? node,string key) => long.TryParse(S(node,key),out var n) ? n : 0;
    public static JsonArray A(JsonNode? node,string key) => node?[key] as JsonArray ?? new JsonArray();
}
