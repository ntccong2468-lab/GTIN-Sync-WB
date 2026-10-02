using MarketplaceHub.Core;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed record ProductVariantRow(long StoreId, Marketplace Marketplace, string Sku, string ExternalId,
    string VariantId, string Size, IReadOnlyList<string> Barcodes, string Gtin, string ImageUrl, string RawJson);
public sealed record ProductCatalogEntry(ProductRow Product, IReadOnlyList<ProductVariantRow> Variants);
public sealed record ProductCatalogPage(IReadOnlyList<ProductCatalogEntry> Entries, string NextCursor, bool Complete, long? Total = null);
public sealed record ProductCatalogCheckpoint(string Cursor, string Scope, int Pages, int Products, int Variants, bool Complete);
public sealed record ProductCatalogStatus(int Products, int Variants, int MissingGtin, int Pages, bool Pending, string Error);

public static class ProductCatalog
{
    // The checkpoint belongs to the account as well as the local store. Never reuse it after credentials/scope change.
    public static string Scope(StoreProfile s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{s.Marketplace}\n{s.ClientId}\n{s.BusinessId}\n{s.CampaignId}\n{s.ApiKey}\n{s.Token}")));

    public static string NormalizeGtin(string? barcode)
    {
        return GtinCode.Normalize(barcode);
    }

    public static ProductRow Merge(ProductRow? existing, ProductRow incoming)
    {
        if (existing is null) return incoming;
        if (existing.StoreId != incoming.StoreId || existing.Marketplace != incoming.Marketplace || existing.Sku != incoming.Sku)
            throw new InvalidDataException("Không thể ghép catalog của hai cửa hàng/SKU khác nhau.");
        return incoming with
        {
            Price = incoming.Price ?? existing.Price,
            ImageUrl = string.IsNullOrWhiteSpace(incoming.ImageUrl) && incoming.ExternalId == existing.ExternalId
                ? existing.ImageUrl : incoming.ImageUrl
        };
    }

    public static string Image(ProductRow product)
    {
        var direct = MediaUrl(JsonValue.Create(product.ImageUrl));
        if (direct.Length > 0) return direct;
        try
        {
            var root = JsonNode.Parse(product.RawJson);
            return product.Marketplace switch
            {
                Marketplace.Wildberries => MediaUrl(root?["photos"]),
                Marketplace.Ozon => FirstMedia(root?["primary_image"], root?["images"]),
                Marketplace.Yandex => FirstMedia(root?["offer"]?["pictures"],root?["offer"]?["mediaFiles"]?["pictures"]),
                _ => ""
            };
        }
        catch (JsonException) { return ""; }
    }

    public static string FirstMedia(params JsonNode?[] nodes)
    {
        foreach (var node in nodes) { var value = MediaUrl(node); if (value.Length > 0) return value; }
        return "";
    }

    public static string MediaUrl(JsonNode? node)
    {
        if (node is JsonValue)
        {
            var value = node.ToString().Trim();
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? value : "";
        }
        if (node is JsonArray array)
            foreach (var item in array) { var value = MediaUrl(item); if (value.Length > 0) return value; }
        if (node is JsonObject obj)
            foreach (var key in new[] { "big", "c516x688", "url", "file_name", "image_url", "square", "primary_photo", "photo" })
            { var value = MediaUrl(obj[key]); if (value.Length > 0) return value; }
        return "";
    }

    public static ProductCatalogEntry Entry(ProductRow product)
    {
        var root = JsonNode.Parse(product.RawJson) as JsonObject ?? throw new InvalidDataException("Card catalog không hợp lệ.");
        var variants = new List<ProductVariantRow>();
        if (product.Marketplace == Marketplace.Wildberries && root["sizes"] is JsonArray sizes && sizes.Count > 0)
        {
            foreach (var size in sizes)
            {
                if (size is not JsonObject obj) throw new InvalidDataException("Biến thể WB không hợp lệ.");
                var barcodes = Strings(obj["skus"]);
                var id = obj["chrtID"]?.ToString() ?? "";
                if (id.Length == 0) id = "size:" + (obj["techSize"]?.ToString() ?? "") + ":" + string.Join(",", barcodes);
                variants.Add(new(product.StoreId, product.Marketplace, product.Sku, product.ExternalId, id,
                    obj["techSize"]?.ToString() ?? obj["wbSize"]?.ToString() ?? "", barcodes,
                    UniqueGtin(barcodes), product.ImageUrl, obj.ToJsonString()));
            }
        }
        else
        {
            var offer = root["offer"] ?? root;
            var barcodes = Strings(offer["barcodes"]);
            if (barcodes.Count == 0 && offer["barcode"] is JsonValue barcode) barcodes = new[] { barcode.ToString() };
            variants.Add(new(product.StoreId, product.Marketplace, product.Sku, product.ExternalId, product.ExternalId,
                offer["size"]?.ToString() ?? "", barcodes,
                UniqueGtin(barcodes), product.ImageUrl, root.ToJsonString()));
        }
        if (variants.Select(v => v.VariantId).Distinct(StringComparer.Ordinal).Count() != variants.Count)
            throw new InvalidDataException($"Catalog trả trùng biến thể cho SKU {product.Sku}.");
        return new(product, variants);
    }

    public static string UniqueGtin(IEnumerable<string> barcodes)
    {
        var codes=barcodes.Select(NormalizeGtin).Where(g=>g.Length>0).Distinct(StringComparer.Ordinal).ToArray();
        return codes.Length==1?codes[0]:"";
    }

    public static IReadOnlyList<string> Strings(JsonNode? value) => value is JsonArray array
        ? array.Where(v => v is JsonValue).Select(v => v!.ToString()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
        : Array.Empty<string>();

    public static ProductRow? ResolveOrder(StoreProfile store, FbsOrderRow order, IEnumerable<ProductRow> catalog)
    {
        if (order.StoreId != store.Id || order.Marketplace != store.Marketplace) return null;
        var products = catalog.Where(p => p.StoreId == store.Id && p.Marketplace == store.Marketplace).ToArray();
        JsonNode? raw;
        try { raw = JsonNode.Parse(order.RawJson); } catch (JsonException) { raw = null; }
        var external = store.Marketplace switch
        {
            Marketplace.Wildberries => raw?["nmId"]?.ToString() ?? raw?["nmID"]?.ToString(),
            Marketplace.Ozon => raw?["product_id"]?.ToString(),
            _ => null
        };
        var exact = products.Where(p => p.Sku.Equals(order.Sku, StringComparison.Ordinal)).ToArray();
        if (!string.IsNullOrEmpty(external))
        {
            var byId = products.Where(p => p.ExternalId == external).ToArray();
            if (byId.Length == 1) return SelectOrderSize(byId[0], raw);
            if (byId.Length > 1) return null;
            // Explicit identity mismatch must not borrow another product's image.
            if (exact.Length > 0 && store.Marketplace == Marketplace.Wildberries) return null;
        }
        if (exact.Length == 1) return SelectOrderSize(exact[0], raw);
        if (exact.Length > 1) return null;
        var barcodes = Strings(raw?["skus"]).Concat(Strings(raw?["barcodes"])).ToHashSet(StringComparer.Ordinal);
        if (raw?["barcode"] is JsonValue barcode) barcodes.Add(barcode.ToString());
        var matches = products.Where(p => Entry(p).Variants.Any(v => v.Barcodes.Any(barcodes.Contains))).ToArray();
        return matches.Length == 1 ? SelectOrderSize(matches[0], raw) : null;
    }

    private static ProductRow SelectOrderSize(ProductRow product, JsonNode? order)
    {
        if (product.Marketplace != Marketplace.Wildberries) return product;
        var root = JsonNode.Parse(product.RawJson) as JsonObject;
        if (root?["sizes"] is not JsonArray sizes || sizes.Count <= 1) return product;
        var chrt = order?["chrtId"]?.ToString() ?? order?["chrtID"]?.ToString();
        var skus = Strings(order?["skus"]).ToHashSet(StringComparer.Ordinal);
        var selected = sizes.Where(s => (!string.IsNullOrEmpty(chrt) && s?["chrtID"]?.ToString() == chrt)
            || Strings(s?["skus"]).Any(skus.Contains)).ToArray();
        if (selected.Length != 1) return product;
        root["sizes"] = new JsonArray(selected[0]!.DeepClone());
        return product with { RawJson = root.ToJsonString() };
    }
}

public sealed partial class MarketplaceGateway
{
    public async Task<IReadOnlyList<ProductRow>> ReadAllCatalogProductsAsync(StoreProfile store, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ProductRow>(StringComparer.Ordinal);
        var cursor = ""; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 100000; page++)
        {
            var batch = await ReadProductCatalogPageAsync(store, cursor, ct).ConfigureAwait(false);
            foreach (var entry in batch.Entries)
            {
                if (result.TryGetValue(entry.Product.Sku, out var prior) && prior.ExternalId != entry.Product.ExternalId)
                    throw new InvalidDataException($"SKU {entry.Product.Sku} thuộc nhiều card; cần xử lý trước khi đồng bộ.");
                result[entry.Product.Sku] = entry.Product;
            }
            if (batch.Complete) {if(batch.Total is { } total && result.Count!=total)throw new InvalidDataException("Catalog kết thúc khi còn thiếu sản phẩm theo total của sàn.");return result.Values.ToArray();}
            if (!seen.Add(batch.NextCursor)) throw new InvalidDataException("Catalog lặp cursor; đã dừng để bảo toàn dữ liệu.");
            cursor = batch.NextCursor;
        }
        throw new InvalidDataException("Catalog vượt giới hạn an toàn; tiến trình chưa hoàn tất.");
    }

    public Task<ProductCatalogPage> ReadProductCatalogPageAsync(StoreProfile store, string cursor = "",
        CancellationToken ct = default, IProgress<string>? progress = null) => store.Marketplace switch
        {
            Marketplace.Wildberries => ReadWbCatalogPageAsync(store, cursor, ct, progress),
            Marketplace.Ozon => ReadOzonCatalogPageAsync(store, cursor, ct, progress),
            Marketplace.Yandex => ReadYandexCatalogPageAsync(store, cursor, ct, progress),
            _ => throw new NotSupportedException("Marketplace không được hỗ trợ.")
        };

    private async Task<JsonObject> ReadCatalogJsonAsync(StoreProfile s, string url, string body,
        CancellationToken ct, IProgress<string>? progress)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Marketplace == Marketplace.Wildberries) await wbLabelDelay(TimeSpan.FromMilliseconds(650), ct).ConfigureAwait(false);
            using var request = Request(HttpMethod.Post, url, s, body);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
                if (response.Headers.TryGetValues("X-Ratelimit-Retry", out var values)
                    && double.TryParse(values.FirstOrDefault(), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
                    wait = TimeSpan.FromSeconds(Math.Max(wait.TotalSeconds, seconds));
                if (wait > TimeSpan.FromMinutes(1)) throw new HttpRequestException($"API giới hạn tốc độ; chờ {Math.Ceiling(wait.TotalSeconds)} giây rồi tiếp tục đồng bộ.");
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                progress?.Report($"API {(int)response.StatusCode}: thử lại trang hiện tại sau {Math.Ceiling(wait.TotalSeconds)} giây ({attempt + 1}/4).");
                await wbLabelDelay(wait, ct).ConfigureAwait(false);
                continue;
            }
            Ensure(response, text);
            JsonObject root;
            try { root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Catalog API không trả object JSON."); }
            catch (JsonException ex) { throw new InvalidDataException("Catalog API trả JSON không hợp lệ.", ex); }
            if (root["error"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || root["status"]?.ToString() is "ERROR" or "FAIL") throw new InvalidDataException("Catalog API báo lỗi trong response thành công.");
            return root;
        }
    }

    private async Task<ProductCatalogPage> ReadWbCatalogPageAsync(StoreProfile s, string cursor, CancellationToken ct, IProgress<string>? progress)
    {
        JsonObject position;
        try { position = string.IsNullOrEmpty(cursor) ? new() : JsonNode.Parse(cursor) as JsonObject ?? throw new InvalidDataException("Cursor WB không hợp lệ."); }
        catch (JsonException ex) { throw new InvalidDataException("Cursor WB không hợp lệ.", ex); }
        position["limit"] = 100;
        var body = new JsonObject { ["settings"] = new JsonObject { ["cursor"] = position,
            ["sort"] = new JsonObject { ["ascending"] = true }, ["filter"] = new JsonObject { ["withPhoto"] = -1 } } }.ToJsonString();
        var root = await ReadCatalogJsonAsync(s, "https://content-api.wildberries.ru/content/v2/get/cards/list", body, ct, progress).ConfigureAwait(false);
        var cards = root["cards"] as JsonArray ?? throw new InvalidDataException("WB thiếu danh sách cards.");
        var responseCursor = root["cursor"] as JsonObject ?? throw new InvalidDataException("WB thiếu cursor catalog.");
        if (!int.TryParse(responseCursor["total"]?.ToString(), out var count) || count < 0 || count != cards.Count)
            throw new InvalidDataException("WB trả số card/cursor.total không hợp lệ.");
        var entries = cards.Select(card =>
        {
            if (card is not JsonObject obj) throw new InvalidDataException("WB trả card không hợp lệ.");
            var external = RequiredCatalogId(obj, "nmID"); var sku = RequiredCatalogId(obj, "vendorCode");
            return ProductCatalog.Entry(new(s.Id, s.Marketplace, external, sku, obj["title"]?.ToString() ?? sku,
                null, ProductCatalog.MediaUrl(obj["photos"]), obj.ToJsonString()));
        }).ToArray();
        if(entries.Length>0)try {
            var prices=await ReadCatalogJsonAsync(s,"https://discounts-prices-api.wildberries.ru/api/v2/list/goods/filter",
                JsonSerializer.Serialize(new{nmIDs=entries.Select(e=>long.Parse(e.Product.ExternalId)).ToArray()}),ct,progress).ConfigureAwait(false);
            var goods=prices["data"]?["listGoods"] as JsonArray??throw new InvalidDataException("WB thiếu danh sách giá.");
            entries=entries.Select(e=>{
                var matches=goods.Where(g=>g?["nmID"]?.ToString()==e.Product.ExternalId).ToArray();
                if(matches.Length!=1)return e;
                var price=ParseDecimal(matches[0]?["sizes"]?[0]?["price"]?.ToString());
                var card=JsonNode.Parse(e.Product.RawJson)!;card["priceInfo"]=matches[0]!.DeepClone();
                return e with{Product=e.Product with{Price=price,RawJson=card.ToJsonString()}};
            }).ToArray();
        }catch(OperationCanceledException){throw;}catch(Exception ex){progress?.Report("Đã đọc card; giá chưa cập nhật: "+ex.Message);}
        if (count < 100) return new(entries, "", true);
        var next = new JsonObject { ["updatedAt"] = RequiredCatalogId(responseCursor, "updatedAt"),
            ["nmID"] = long.TryParse(responseCursor["nmID"]?.ToString(), out var nm) && nm > 0 ? nm : throw new InvalidDataException("WB thiếu nmID cursor.") }.ToJsonString();
        if (next == cursor) throw new InvalidDataException("WB lặp cursor catalog.");
        return new(entries, next, false);
    }

    private async Task<ProductCatalogPage> ReadOzonCatalogPageAsync(StoreProfile s, string cursor, CancellationToken ct, IProgress<string>? progress)
    {
        var root = await ReadCatalogJsonAsync(s, "https://api-seller.ozon.ru/v3/product/list", JsonSerializer.Serialize(
            new { filter = new { visibility = "ALL" }, last_id = cursor, limit = 1000 }), ct, progress).ConfigureAwait(false);
        var result = root["result"] as JsonObject ?? throw new InvalidDataException("Ozon thiếu result catalog.");
        var items = result["items"] as JsonArray ?? throw new InvalidDataException("Ozon thiếu items catalog.");
        var identities = items.Select(item => new { Id = RequiredCatalogId(item, "product_id"), Sku = RequiredCatalogId(item, "offer_id") }).ToArray();
        if (identities.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != identities.Length
            || identities.Select(i => i.Sku).Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new InvalidDataException("Ozon trả trùng định danh sản phẩm.");
        if(result["last_id"] is not JsonValue)throw new InvalidDataException("Ozon thiếu last_id; không coi trang chưa rõ cursor là catalog hoàn tất.");
        var next = result["last_id"]!.ToString();
        var complete = items.Count == 0 || string.IsNullOrEmpty(next);
        if(complete && cursor.Length==0 && long.TryParse(result["total"]?.ToString(),out var total) && total>items.Count)
            throw new InvalidDataException("Ozon catalog còn sản phẩm nhưng không trả cursor để tiếp tục.");
        if ((!complete && next == cursor) || (complete && items.Count >= 1000)) throw new InvalidDataException("Ozon cursor không tiến triển.");
        var advertisedTotal=long.TryParse(result["total"]?.ToString(),out var fullCount)?(long?)fullCount:null;
        if (items.Count == 0) return new(Array.Empty<ProductCatalogEntry>(), "", true,advertisedTotal);
        var details = await ReadCatalogJsonAsync(s, "https://api-seller.ozon.ru/v3/product/info/list",
            JsonSerializer.Serialize(new { product_id = identities.Select(i => i.Id).ToArray() }), ct, progress).ConfigureAwait(false);
        var info = details["items"] as JsonArray ?? details["result"]?["items"] as JsonArray
            ?? throw new InvalidDataException("Ozon thiếu chi tiết catalog.");
        var entries = new List<ProductCatalogEntry>();
        foreach (var identity in identities)
        {
            var matches = info.Where(i => (i?["id"]?.ToString() ?? i?["product_id"]?.ToString()) == identity.Id).ToArray();
            if (matches.Length != 1 || matches[0]?["offer_id"]?.ToString() != identity.Sku)
                throw new InvalidDataException($"Ozon thiếu/sai chi tiết SKU {identity.Sku}; trang hiện tại chưa được lưu.");
            var item = matches[0]!;
            entries.Add(ProductCatalog.Entry(new(s.Id, s.Marketplace, identity.Id, identity.Sku,
                item["name"]?.ToString() ?? identity.Sku, ParseDecimal(item["price"]?.ToString()),
                ProductCatalog.FirstMedia(item["primary_image"], item["images"]), item.ToJsonString())));
        }
        return new(entries, complete ? "" : next, complete,advertisedTotal);
    }

    private async Task<ProductCatalogPage> ReadYandexCatalogPageAsync(StoreProfile s, string cursor, CancellationToken ct, IProgress<string>? progress)
    {
        if (!long.TryParse(s.BusinessId, out var businessId) || businessId <= 0) throw new InvalidOperationException("Yandex cần Business ID hợp lệ.");
        var url = $"https://api.partner.market.yandex.ru/v2/businesses/{businessId}/offer-mappings?limit=100";
        if (!string.IsNullOrEmpty(cursor)) url += "&pageToken=" + Uri.EscapeDataString(cursor);
        var root = await ReadCatalogJsonAsync(s, url, "{}", ct, progress).ConfigureAwait(false);
        var result = root["result"] as JsonObject ?? throw new InvalidDataException("Yandex thiếu result catalog.");
        var offers = result["offerMappings"] as JsonArray ?? throw new InvalidDataException("Yandex thiếu offerMappings.");
        var entries = offers.Select(item =>
        {
            var offer = item?["offer"] as JsonObject ?? throw new InvalidDataException("Yandex thiếu offer.");
            var sku = RequiredCatalogId(offer, "offerId");
            return ProductCatalog.Entry(new(s.Id, s.Marketplace, sku,
                sku, offer["name"]?.ToString() ?? sku,
                ParseDecimal(offer["basicPrice"]?["value"]?.ToString()) ?? ParseDecimal(offer["price"]?["value"]?.ToString()),
                ProductCatalog.FirstMedia(offer["pictures"],offer["mediaFiles"]?["pictures"]), item!.ToJsonString()));
        }).ToArray();
        var next = result["paging"]?["nextPageToken"]?.ToString() ?? "";
        if (next.Length > 0 && (next == cursor || entries.Length == 0)) throw new InvalidDataException("Yandex cursor không tiến triển.");
        return new(entries, next, next.Length == 0);
    }

    private static string RequiredCatalogId(JsonNode? node, string key) => node?[key] is JsonValue value && !string.IsNullOrWhiteSpace(value.ToString())
        ? value.ToString() : throw new InvalidDataException($"Catalog thiếu {key}.");
}
