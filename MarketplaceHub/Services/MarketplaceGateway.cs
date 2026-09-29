using MarketplaceHub.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed class MarketplaceGateway
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<ApiTestResult> TestAsync(StoreProfile s, CancellationToken ct = default)
    {
        try
        {
            using var req = s.Marketplace switch
            {
                Marketplace.Wildberries => Request(HttpMethod.Get, "https://common-api.wildberries.ru/api/v1/seller-info", s),
                Marketplace.Ozon => Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/seller/info", s, "{}"),
                Marketplace.Yandex => Request(HttpMethod.Get, "https://api.partner.market.yandex.ru/v2/campaigns", s),
                _ => throw new NotSupportedException()
            };
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return new ApiTestResult(res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "Kết nối API thành công." : $"HTTP {(int)res.StatusCode}: {Short(body)}");
        }
        catch (Exception ex) { return new ApiTestResult(false, ex.Message); }
    }

    public async Task<IReadOnlyList<ProductRow>> SyncProductsAsync(StoreProfile s, CancellationToken ct = default)
    {
        if (s.Marketplace == Marketplace.Wildberries) return await WbProducts(s, ct);
        if (s.Marketplace == Marketplace.Ozon) return await OzonProducts(s, ct);
        return await YandexProducts(s, ct);
    }

    private async Task<IReadOnlyList<ProductRow>> WbProducts(StoreProfile s, CancellationToken ct)
    {
        var body = "{\"settings\":{\"cursor\":{\"limit\":100},\"filter\":{\"withPhoto\":-1}}}";
        using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/get/cards/list", s, body), ct);
        var text = await res.Content.ReadAsStringAsync(ct); Ensure(res, text);
        var arr = JsonNode.Parse(text)?["cards"]?.AsArray() ?? new JsonArray();
        return arr.Where(x => x is not null).Select(x =>
        {
            var sku = x!["vendorCode"]?.ToString() ?? x["nmID"]?.ToString() ?? "";
            return new ProductRow(s.Id, s.Marketplace, x["nmID"]?.ToString() ?? sku, sku, x["title"]?.ToString() ?? sku, null, x["photos"]?[0]?["big"]?.ToString() ?? "", x.ToJsonString());
        }).ToList();
    }

    private async Task<IReadOnlyList<ProductRow>> OzonProducts(StoreProfile s, CancellationToken ct)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/product/list", s, "{\"filter\":{\"visibility\":\"ALL\"},\"last_id\":\"\",\"limit\":1000}"), ct);
        var text = await res.Content.ReadAsStringAsync(ct); Ensure(res, text);
        var arr = JsonNode.Parse(text)?["result"]?["items"]?.AsArray() ?? new JsonArray();
        return arr.Where(x => x is not null).Select(x =>
        {
            var sku = x!["offer_id"]?.ToString() ?? x["product_id"]?.ToString() ?? "";
            return new ProductRow(s.Id, s.Marketplace, x["product_id"]?.ToString() ?? sku, sku, sku, null, "", x.ToJsonString());
        }).ToList();
    }

    private async Task<IReadOnlyList<ProductRow>> YandexProducts(StoreProfile s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BusinessId)) throw new InvalidOperationException("Thiếu Business ID.");
        using var res = await http.SendAsync(Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v2/businesses/{s.BusinessId}/offer-mappings?limit=100", s, "{}"), ct);
        var text = await res.Content.ReadAsStringAsync(ct); Ensure(res, text);
        var arr = JsonNode.Parse(text)?["result"]?["offerMappings"]?.AsArray() ?? new JsonArray();
        return arr.Where(x => x is not null).Select(x =>
        {
            var offer = x!["offer"]; var sku = offer?["offerId"]?.ToString() ?? "";
            return new ProductRow(s.Id, s.Marketplace, x["mapping"]?["marketSku"]?.ToString() ?? sku, sku, offer?["name"]?.ToString() ?? sku, null, offer?["pictures"]?[0]?.ToString() ?? "", x.ToJsonString());
        }).ToList();
    }

    public async Task<PriceUpdateResult> UpdatePriceAsync(StoreProfile s, ProductRow p, decimal newPrice, CancellationToken ct = default)
    {
        try
        {
            HttpRequestMessage req;
            if (s.Marketplace == Marketplace.Wildberries)
            {
                if (!long.TryParse(p.ExternalId, out var nmId)) throw new InvalidOperationException("WB cần nmID hợp lệ.");
                req = Request(HttpMethod.Post, "https://discounts-prices-api.wildberries.ru/api/v2/upload/task", s,
                    JsonSerializer.Serialize(new { data = new[] { new { nmID = nmId, price = (long)Math.Round(newPrice) } } }));
            }
            else if (s.Marketplace == Marketplace.Ozon)
            {
                req = Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/product/import/prices", s,
                    JsonSerializer.Serialize(new { prices = new[] { new { offer_id = p.Sku, price = newPrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), old_price = "0", currency_code = "RUB" } } }));
            }
            else
            {
                var scope = string.IsNullOrWhiteSpace(s.CampaignId) ? $"businesses/{s.BusinessId}" : $"campaigns/{s.CampaignId}";
                req = Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v2/{scope}/offer-prices/updates", s,
                    JsonSerializer.Serialize(new { offers = new[] { new { offerId = p.Sku, price = new { value = newPrice, currencyId = "RUR" } } } }));
            }
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            return new PriceUpdateResult(res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "Sàn đã tiếp nhận yêu cầu thay giá." : $"HTTP {(int)res.StatusCode}: {Short(text)}");
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    public async Task<IReadOnlyList<FbsOrderRow>> SyncFbsAsync(StoreProfile s, CancellationToken ct = default)
    {
        if (s.Marketplace == Marketplace.Wildberries)
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, "https://marketplace-api.wildberries.ru/api/v3/orders/new", s), ct);
            var text = await res.Content.ReadAsStringAsync(ct); Ensure(res, text);
            var arr = JsonNode.Parse(text)?["orders"]?.AsArray() ?? new JsonArray();
            return arr.Where(x => x is not null).Select(x => new FbsOrderRow(s.Id, s.Marketplace, x!["id"]?.ToString() ?? "", x["article"]?.ToString() ?? "", x["article"]?.ToString() ?? "", 1, "new", false, x.ToJsonString())).ToList();
        }
        if (s.Marketplace == Marketplace.Ozon)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-14).ToString("O");
            var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
            var body = JsonSerializer.Serialize(new { dir = "ASC", filter = new { since, to, status = "awaiting_packaging" }, limit = 1000, offset = 0 });
            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/posting/fbs/list", s, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct); Ensure(res, text);
            var postings = JsonNode.Parse(text)?["result"]?["postings"]?.AsArray() ?? new JsonArray();
            var list = new List<FbsOrderRow>();
            foreach (var post in postings)
                foreach (var item in post?["products"]?.AsArray() ?? new JsonArray())
                    list.Add(new FbsOrderRow(s.Id, s.Marketplace, post?["posting_number"]?.ToString() ?? "", item?["offer_id"]?.ToString() ?? "", item?["name"]?.ToString() ?? "", item?["quantity"]?.GetValue<int>() ?? 1, post?["status"]?.ToString() ?? "", false, post?.ToJsonString() ?? "{}"));
            return list;
        }
        if (string.IsNullOrWhiteSpace(s.BusinessId)) throw new InvalidOperationException("Thiếu Business ID.");
        var yBody = JsonSerializer.Serialize(new { statuses = new[] { "PROCESSING" }, substatuses = new[] { "STARTED" }, limit = 50 });
        using var yRes = await http.SendAsync(Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v1/businesses/{s.BusinessId}/orders", s, yBody), ct);
        var yText = await yRes.Content.ReadAsStringAsync(ct); Ensure(yRes, yText);
        var orders = JsonNode.Parse(yText)?["result"]?["orders"]?.AsArray() ?? new JsonArray();
        var result = new List<FbsOrderRow>();
        foreach (var order in orders)
            foreach (var item in order?["items"]?.AsArray() ?? new JsonArray())
                result.Add(new FbsOrderRow(s.Id, s.Marketplace, order?["id"]?.ToString() ?? "", item?["offerId"]?.ToString() ?? "", item?["offerName"]?.ToString() ?? "", item?["count"]?.GetValue<int>() ?? 1, $"{order?["status"]}/{order?["substatus"]}", false, order?.ToJsonString() ?? "{}"));
        return result;
    }

    public async Task<LabelResult> DownloadLabelAsync(StoreProfile s, string orderId, CancellationToken ct = default)
    {
        try
        {
            HttpRequestMessage req; string ext;
            if (s.Marketplace == Marketplace.Wildberries)
            {
                req = Request(HttpMethod.Post, "https://marketplace-api.wildberries.ru/api/v3/orders/stickers?type=png&width=58&height=40", s, JsonSerializer.Serialize(new { orders = new[] { long.Parse(orderId) } }));
                ext = ".json";
            }
            else if (s.Marketplace == Marketplace.Ozon)
            {
                req = Request(HttpMethod.Post, "https://api-seller.ozon.ru/v2/posting/fbs/package-label", s, JsonSerializer.Serialize(new { posting_number = orderId }));
                ext = ".pdf";
            }
            else
            {
                req = Request(HttpMethod.Get, $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders/{orderId}/delivery/labels", s);
                ext = ".pdf";
            }
            using var res = await http.SendAsync(req, ct);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (!res.IsSuccessStatusCode) return new LabelResult(false, $"HTTP {(int)res.StatusCode}");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "Labels");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{s.Marketplace}-{orderId}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(path, bytes, ct);
            return new LabelResult(true, "Đã tải nhãn.", path);
        }
        catch (Exception ex) { return new LabelResult(false, ex.Message); }
    }

    public async Task<PriceUpdateResult> CopySameMarketplaceAsync(StoreProfile src, StoreProfile dst, ProductRow p, string destinationSku, CancellationToken ct = default)
    {
        try
        {
            if (src.Marketplace != dst.Marketplace) return new PriceUpdateResult(false, "Copy khác sàn cần mapping category/attribute trước khi publish.");
            if (src.Marketplace == Marketplace.Yandex)
            {
                var raw = JsonNode.Parse(p.RawJson); var offer = raw?["offer"]?.DeepClone()?.AsObject() ?? throw new InvalidOperationException("Thiếu dữ liệu offer.");
                offer["offerId"] = destinationSku;
                var body = new JsonObject { ["offerMappings"] = new JsonArray(new JsonObject { ["offer"] = offer }) }.ToJsonString();
                using var res = await http.SendAsync(Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v2/businesses/{dst.BusinessId}/offer-mappings/update", dst, body), ct);
                return new PriceUpdateResult(res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "Đã gửi yêu cầu copy Yandex." : $"HTTP {(int)res.StatusCode}");
            }
            if (src.Marketplace == Marketplace.Wildberries)
            {
                var c = JsonNode.Parse(p.RawJson)?.AsObject() ?? throw new InvalidOperationException("Card nguồn lỗi.");
                var subject = c["subjectID"]?.GetValue<long>() ?? 0;
                var variant = new JsonObject { ["vendorCode"] = destinationSku, ["title"] = c["title"]?.DeepClone(), ["description"] = c["description"]?.DeepClone(), ["brand"] = c["brand"]?.DeepClone(), ["dimensions"] = c["dimensions"]?.DeepClone(), ["characteristics"] = c["characteristics"]?.DeepClone(), ["sizes"] = c["sizes"]?.DeepClone(), ["kizMarked"] = c["kizMarked"]?.DeepClone() };
                var body = new JsonArray(new JsonObject { ["subjectID"] = subject, ["variants"] = new JsonArray(variant) }).ToJsonString();
                using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/cards/upload", dst, body), ct);
                return new PriceUpdateResult(res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "Đã gửi yêu cầu tạo card WB." : $"HTTP {(int)res.StatusCode}");
            }
            return new PriceUpdateResult(false, "Ozon copy cần lấy full attributes trước khi import; bản này không gửi dữ liệu thiếu.");
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, StoreProfile s, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (s.Marketplace == Marketplace.Wildberries) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        else if (s.Marketplace == Marketplace.Ozon) { r.Headers.TryAddWithoutValidation("Client-Id", s.ClientId); r.Headers.TryAddWithoutValidation("Api-Key", s.ApiKey); }
        else r.Headers.TryAddWithoutValidation("Api-Key", s.ApiKey);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    private static void Ensure(HttpResponseMessage r, string body)
    {
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)r.StatusCode}: {Short(body)}");
    }
    private static string Short(string s) => s.Length > 400 ? s[..400] : s;
}
