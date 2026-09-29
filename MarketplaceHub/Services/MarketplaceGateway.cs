using MarketplaceHub.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed class MarketplaceGateway
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<ApiTestResult> TestAsync(StoreProfile s, CancellationToken ct = default)
    {
        try
        {
            using var req = s.Marketplace switch
            {
                Marketplace.Wildberries => Request(HttpMethod.Get, "https://common-api.wildberries.ru/ping", s),
                Marketplace.Ozon => Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/seller/info", s, "{}"),
                Marketplace.Yandex => Request(HttpMethod.Post, "https://api.partner.market.yandex.ru/v2/auth/token", s, "{}"),
                _ => throw new NotSupportedException()
            };
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return new ApiTestResult(
                res.IsSuccessStatusCode,
                res.IsSuccessStatusCode ? "Kết nối API thành công." : $"HTTP {(int)res.StatusCode}: {Short(body)}");
        }
        catch (Exception ex) { return new ApiTestResult(false, ex.Message); }
    }

    public async Task<IReadOnlyList<ProductRow>> SyncProductsAsync(StoreProfile s, CancellationToken ct = default)
    {
        return s.Marketplace switch
        {
            Marketplace.Wildberries => await WbProducts(s, ct),
            Marketplace.Ozon => await OzonProducts(s, ct),
            Marketplace.Yandex => await YandexProducts(s, ct),
            _ => Array.Empty<ProductRow>()
        };
    }

    private async Task<IReadOnlyList<ProductRow>> WbProducts(StoreProfile s, CancellationToken ct)
    {
        var cards = new List<JsonObject>();
        string? updatedAt = null;
        long? nmId = null;

        for (var page = 0; page < 200; page++)
        {
            var cursor = new JsonObject { ["limit"] = 100 };
            if (!string.IsNullOrWhiteSpace(updatedAt)) cursor["updatedAt"] = updatedAt;
            if (nmId.HasValue) cursor["nmID"] = nmId.Value;

            var body = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["cursor"] = cursor,
                    ["filter"] = new JsonObject { ["withPhoto"] = -1 }
                }
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/get/cards/list", s, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);

            var root = JsonNode.Parse(text);
            var arr = root?["cards"]?.AsArray() ?? new JsonArray();
            foreach (var node in arr)
                if (node is JsonObject obj) cards.Add(obj);

            if (arr.Count < 100) break;
            updatedAt = root?["cursor"]?["updatedAt"]?.ToString();
            if (long.TryParse(root?["cursor"]?["nmID"]?.ToString(), out var parsed)) nmId = parsed;
            else break;
        }

        var priceByNm = await WbPrices(s, ct);
        return cards.Select(x =>
        {
            var external = x["nmID"]?.ToString() ?? "";
            var sku = x["vendorCode"]?.ToString() ?? external;
            decimal? price = null;
            if (long.TryParse(external, out var id) && priceByNm.TryGetValue(id, out var p)) price = p;

            return new ProductRow(
                s.Id,
                s.Marketplace,
                external,
                sku,
                x["title"]?.ToString() ?? sku,
                price,
                x["photos"]?[0]?["big"]?.ToString() ?? "",
                x.ToJsonString());
        }).ToList();
    }

    private async Task<Dictionary<long, decimal>> WbPrices(StoreProfile s, CancellationToken ct)
    {
        var result = new Dictionary<long, decimal>();
        for (var offset = 0; offset < 100000; offset += 1000)
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, $"https://discounts-prices-api.wildberries.ru/api/v2/list/goods/filter?limit=1000&offset={offset}", s), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);
            var arr = JsonNode.Parse(text)?["data"]?["listGoods"]?.AsArray() ?? new JsonArray();
            if (arr.Count == 0) break;

            foreach (var item in arr)
            {
                if (!long.TryParse(item?["nmID"]?.ToString(), out var id)) continue;
                var firstSize = item?["sizes"]?.AsArray()?.FirstOrDefault();
                if (decimal.TryParse(firstSize?["price"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                    result[id] = p;
            }
            if (arr.Count < 1000) break;
        }
        return result;
    }

    private async Task<IReadOnlyList<ProductRow>> OzonProducts(StoreProfile s, CancellationToken ct)
    {
        var ids = new List<long>();
        var offerIds = new List<string>();
        var lastId = "";

        for (var page = 0; page < 200; page++)
        {
            var body = JsonSerializer.Serialize(new
            {
                filter = new { visibility = "ALL" },
                last_id = lastId,
                limit = 1000
            });

            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/product/list", s, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);

            var root = JsonNode.Parse(text)?["result"];
            var arr = root?["items"]?.AsArray() ?? new JsonArray();
            foreach (var item in arr)
            {
                if (long.TryParse(item?["product_id"]?.ToString(), out var id)) ids.Add(id);
                offerIds.Add(item?["offer_id"]?.ToString() ?? "");
            }

            var next = root?["last_id"]?.ToString() ?? "";
            if (arr.Count == 0 || string.IsNullOrWhiteSpace(next) || next == lastId) break;
            lastId = next;
        }

        var result = new List<ProductRow>();
        foreach (var batch in ids.Chunk(1000))
        {
            var body = JsonSerializer.Serialize(new { product_id = batch.Select(x => x.ToString()).ToArray() });
            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/product/info/list", s, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);

            var arr = JsonNode.Parse(text)?["items"]?.AsArray()
                      ?? JsonNode.Parse(text)?["result"]?["items"]?.AsArray()
                      ?? new JsonArray();

            foreach (var x in arr)
            {
                var external = x?["id"]?.ToString() ?? x?["product_id"]?.ToString() ?? "";
                var sku = x?["offer_id"]?.ToString() ?? external;
                decimal? price = ParseDecimal(x?["price"]?.ToString());
                var name = x?["name"]?.ToString() ?? sku;
                var image = x?["images"]?.AsArray()?.FirstOrDefault()?.ToString() ?? "";
                result.Add(new ProductRow(s.Id, s.Marketplace, external, sku, name, price, image, x?.ToJsonString() ?? "{}"));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<ProductRow>> YandexProducts(StoreProfile s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BusinessId)) throw new InvalidOperationException("Thiếu Business ID.");

        var body = JsonSerializer.Serialize(new { limit = 1000 });
        using var res = await http.SendAsync(Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v2/businesses/{s.BusinessId}/offer-mappings", s, body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        Ensure(res, text);

        var arr = JsonNode.Parse(text)?["result"]?["offerMappings"]?.AsArray() ?? new JsonArray();
        return arr.Where(x => x is not null).Select(x =>
        {
            var offer = x!["offer"];
            var sku = offer?["offerId"]?.ToString() ?? "";
            var price = ParseDecimal(offer?["basicPrice"]?["value"]?.ToString())
                        ?? ParseDecimal(offer?["price"]?["value"]?.ToString());
            return new ProductRow(
                s.Id,
                s.Marketplace,
                x["mapping"]?["marketSku"]?.ToString() ?? sku,
                sku,
                offer?["name"]?.ToString() ?? sku,
                price,
                offer?["pictures"]?.AsArray()?.FirstOrDefault()?.ToString() ?? "",
                x.ToJsonString());
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
                    JsonSerializer.Serialize(new
                    {
                        prices = new[]
                        {
                            new
                            {
                                offer_id = p.Sku,
                                price = newPrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                                old_price = "0",
                                min_price = "0",
                                currency_code = "RUB"
                            }
                        }
                    }));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(s.BusinessId) && string.IsNullOrWhiteSpace(s.CampaignId))
                    throw new InvalidOperationException("Thiếu Business ID/Campaign ID Yandex.");

                var scope = string.IsNullOrWhiteSpace(s.CampaignId) ? $"businesses/{s.BusinessId}" : $"campaigns/{s.CampaignId}";
                req = Request(HttpMethod.Post, $"https://api.partner.market.yandex.ru/v2/{scope}/offer-prices/updates", s,
                    JsonSerializer.Serialize(new
                    {
                        offers = new[]
                        {
                            new
                            {
                                offerId = p.Sku,
                                price = new { value = newPrice, currencyId = "RUR" }
                            }
                        }
                    }));
            }

            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new PriceUpdateResult(false, $"HTTP {(int)res.StatusCode}: {Short(text)}");

            if (s.Marketplace == Marketplace.Ozon)
            {
                var first = JsonNode.Parse(text)?["result"]?.AsArray()?.FirstOrDefault();
                var updated = first?["updated"]?.GetValue<bool?>() ?? false;
                if (!updated)
                {
                    var errors = first?["errors"]?.ToJsonString() ?? "Không xác nhận được trạng thái cập nhật.";
                    return new PriceUpdateResult(false, errors);
                }
            }

            var task = JsonNode.Parse(text)?["data"]?["id"]?.ToString()
                       ?? JsonNode.Parse(text)?["result"]?["task_id"]?.ToString();

            return new PriceUpdateResult(true, "Sàn đã tiếp nhận yêu cầu thay giá. Hãy đồng bộ lại để xác minh giá sau xử lý.", task);
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    public async Task<IReadOnlyList<FbsOrderRow>> SyncFbsAsync(StoreProfile s, CancellationToken ct = default)
    {
        if (s.Marketplace == Marketplace.Wildberries)
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, "https://marketplace-api.wildberries.ru/api/v3/orders/new", s), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);
            var arr = JsonNode.Parse(text)?["orders"]?.AsArray() ?? new JsonArray();
            return arr.Where(x => x is not null).Select(x =>
                new FbsOrderRow(
                    s.Id,
                    s.Marketplace,
                    x!["id"]?.ToString() ?? "",
                    x["article"]?.ToString() ?? "",
                    x["article"]?.ToString() ?? "",
                    1,
                    "new",
                    x["requiredMeta"]?.AsArray()?.Any(m => string.Equals(m?.ToString(), "sgtin", StringComparison.OrdinalIgnoreCase)) ?? false,
                    x.ToJsonString())).ToList();
        }

        if (s.Marketplace == Marketplace.Ozon)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-14).ToString("O");
            var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
            var body = JsonSerializer.Serialize(new { dir = "ASC", filter = new { since, to, status = "awaiting_packaging" }, limit = 1000, offset = 0 });
            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/posting/fbs/list", s, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);

            var postings = JsonNode.Parse(text)?["result"]?["postings"]?.AsArray() ?? new JsonArray();
            var list = new List<FbsOrderRow>();
            foreach (var post in postings)
            {
                var markingRequired = post?["requirements"]?["products_requiring_gtd"]?.AsArray()?.Count > 0
                                      || post?["requirements"]?["products_requiring_country"]?.AsArray()?.Count > 0;
                foreach (var item in post?["products"]?.AsArray() ?? new JsonArray())
                    list.Add(new FbsOrderRow(
                        s.Id,
                        s.Marketplace,
                        post?["posting_number"]?.ToString() ?? "",
                        item?["offer_id"]?.ToString() ?? "",
                        item?["name"]?.ToString() ?? "",
                        item?["quantity"]?.GetValue<int>() ?? 1,
                        post?["status"]?.ToString() ?? "",
                        markingRequired,
                        post?.ToJsonString() ?? "{}"));
            }
            return list;
        }

        if (string.IsNullOrWhiteSpace(s.CampaignId)) throw new InvalidOperationException("Thiếu Campaign ID Yandex.");
        using var yRes = await http.SendAsync(Request(HttpMethod.Get, $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders", s), ct);
        var yText = await yRes.Content.ReadAsStringAsync(ct);
        Ensure(yRes, yText);

        var orders = JsonNode.Parse(yText)?["orders"]?.AsArray()
                     ?? JsonNode.Parse(yText)?["result"]?["orders"]?.AsArray()
                     ?? new JsonArray();

        var result = new List<FbsOrderRow>();
        foreach (var order in orders)
        {
            var status = order?["status"]?.ToString() ?? "";
            var sub = order?["substatus"]?.ToString() ?? "";
            if (!status.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var item in order?["items"]?.AsArray() ?? new JsonArray())
                result.Add(new FbsOrderRow(
                    s.Id,
                    s.Marketplace,
                    order?["id"]?.ToString() ?? "",
                    item?["offerId"]?.ToString() ?? "",
                    item?["offerName"]?.ToString() ?? "",
                    item?["count"]?.GetValue<int>() ?? 1,
                    $"{status}/{sub}",
                    false,
                    order?.ToJsonString() ?? "{}"));
        }

        return result;
    }

    public async Task<LabelResult> DownloadLabelAsync(StoreProfile s, string orderId, CancellationToken ct = default)
    {
        try
        {
            byte[] fileBytes;
            string ext;

            if (s.Marketplace == Marketplace.Wildberries)
            {
                using var req = Request(HttpMethod.Post,
                    "https://marketplace-api.wildberries.ru/api/v3/orders/stickers?type=png&width=58&height=40",
                    s,
                    JsonSerializer.Serialize(new { orders = new[] { long.Parse(orderId) } }));
                using var res = await http.SendAsync(req, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) return new LabelResult(false, $"HTTP {(int)res.StatusCode}: {Short(text)}");

                var base64 = JsonNode.Parse(text)?["stickers"]?.AsArray()?.FirstOrDefault()?["file"]?.ToString();
                if (string.IsNullOrWhiteSpace(base64)) return new LabelResult(false, "WB không trả file sticker.");
                fileBytes = Convert.FromBase64String(base64);
                ext = ".png";
            }
            else if (s.Marketplace == Marketplace.Ozon)
            {
                using var req = Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v2/posting/fbs/package-label",
                    s,
                    JsonSerializer.Serialize(new { posting_number = new[] { orderId } }));
                using var res = await http.SendAsync(req, ct);
                fileBytes = await res.Content.ReadAsByteArrayAsync(ct);
                if (!res.IsSuccessStatusCode) return new LabelResult(false, $"HTTP {(int)res.StatusCode}: {Short(Encoding.UTF8.GetString(fileBytes))}");
                ext = ".pdf";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(s.CampaignId)) throw new InvalidOperationException("Thiếu Campaign ID Yandex.");
                using var req = Request(HttpMethod.Get,
                    $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders/{orderId}/delivery/labels?format=A9_HORIZONTALLY",
                    s);
                using var res = await http.SendAsync(req, ct);
                fileBytes = await res.Content.ReadAsByteArrayAsync(ct);
                if (!res.IsSuccessStatusCode) return new LabelResult(false, $"HTTP {(int)res.StatusCode}: {Short(Encoding.UTF8.GetString(fileBytes))}");
                ext = ".pdf";
            }

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "Labels");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{s.Marketplace}-{SafeFile(orderId)}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(path, fileBytes, ct);
            return new LabelResult(true, "Đã tải nhãn.", path);
        }
        catch (Exception ex) { return new LabelResult(false, ex.Message); }
    }

    public async Task<PriceUpdateResult> CopySameMarketplaceAsync(StoreProfile src, StoreProfile dst, ProductRow p, string destinationSku, CancellationToken ct = default)
    {
        try
        {
            if (src.Marketplace != dst.Marketplace)
                return new PriceUpdateResult(false, "Copy khác sàn cần category/attribute mapper trước khi publish.");

            if (src.Marketplace == Marketplace.Yandex)
            {
                if (string.IsNullOrWhiteSpace(dst.BusinessId)) throw new InvalidOperationException("Destination Yandex thiếu Business ID.");
                var raw = JsonNode.Parse(p.RawJson);
                var offer = raw?["offer"]?.DeepClone()?.AsObject() ?? throw new InvalidOperationException("Thiếu dữ liệu offer.");
                offer["offerId"] = destinationSku;
                var body = new JsonObject
                {
                    ["offerMappings"] = new JsonArray(new JsonObject { ["offer"] = offer })
                }.ToJsonString();

                using var res = await http.SendAsync(Request(HttpMethod.Post,
                    $"https://api.partner.market.yandex.ru/v2/businesses/{dst.BusinessId}/offer-mappings/update",
                    dst, body), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                return new PriceUpdateResult(res.IsSuccessStatusCode,
                    res.IsSuccessStatusCode ? "Đã gửi yêu cầu copy Yandex." : $"HTTP {(int)res.StatusCode}: {Short(text)}");
            }

            if (src.Marketplace == Marketplace.Wildberries)
            {
                var c = JsonNode.Parse(p.RawJson)?.AsObject() ?? throw new InvalidOperationException("Card nguồn lỗi.");
                var subject = c["subjectID"]?.GetValue<long>() ?? c["subjectId"]?.GetValue<long>() ?? 0;
                if (subject == 0) throw new InvalidOperationException("Không tìm thấy subjectId của card nguồn.");

                var variant = new JsonObject
                {
                    ["vendorCode"] = destinationSku,
                    ["title"] = c["title"]?.DeepClone(),
                    ["description"] = c["description"]?.DeepClone(),
                    ["brand"] = c["brand"]?.DeepClone(),
                    ["dimensions"] = c["dimensions"]?.DeepClone(),
                    ["characteristics"] = c["characteristics"]?.DeepClone(),
                    ["sizes"] = c["sizes"]?.DeepClone(),
                    ["kizMarked"] = c["kizMarked"]?.DeepClone() ?? false
                };
                var body = new JsonArray(new JsonObject
                {
                    ["subjectID"] = subject,
                    ["variants"] = new JsonArray(variant)
                }).ToJsonString();

                using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/cards/upload", dst, body), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                return new PriceUpdateResult(res.IsSuccessStatusCode,
                    res.IsSuccessStatusCode ? "WB đã tiếp nhận yêu cầu tạo card. Card được tạo bất đồng bộ." : $"HTTP {(int)res.StatusCode}: {Short(text)}");
            }

            var infoBody = JsonSerializer.Serialize(new { offer_id = new[] { p.Sku } });
            using var infoRes = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/product/info/list", src, infoBody), ct);
            var infoText = await infoRes.Content.ReadAsStringAsync(ct);
            Ensure(infoRes, infoText);

            var item = JsonNode.Parse(infoText)?["items"]?.AsArray()?.FirstOrDefault()
                       ?? JsonNode.Parse(infoText)?["result"]?["items"]?.AsArray()?.FirstOrDefault();
            if (item is null) throw new InvalidOperationException("Không lấy được thông tin sản phẩm Ozon nguồn.");

            var ozonSku = item["sources"]?.AsArray()?.Select(x => x?["sku"]?.ToString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                          ?? item["sku"]?.ToString();
            if (!long.TryParse(ozonSku, out var skuId))
                throw new InvalidOperationException("Ozon nguồn không có SKU nội bộ có thể dùng cho import-by-sku.");

            var sourcePrice = item["price"]?.ToString() ?? p.Price?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            var oldPrice = item["old_price"]?.ToString() ?? "0";
            var name = item["name"]?.ToString() ?? p.Name;
            var vat = item["vat"]?.ToString() ?? "0";
            var currency = item["currency_code"]?.ToString() ?? "RUB";

            var copyBody = JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new
                    {
                        sku = skuId,
                        name,
                        offer_id = destinationSku,
                        currency_code = currency,
                        old_price = oldPrice,
                        price = sourcePrice,
                        vat
                    }
                }
            });

            using var copyRes = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/product/import-by-sku", dst, copyBody), ct);
            var copyText = await copyRes.Content.ReadAsStringAsync(ct);
            return new PriceUpdateResult(copyRes.IsSuccessStatusCode,
                copyRes.IsSuccessStatusCode ? "Ozon đã tiếp nhận yêu cầu copy theo Ozon SKU." : $"HTTP {(int)copyRes.StatusCode}: {Short(copyText)}",
                JsonNode.Parse(copyText)?["result"]?["task_id"]?.ToString());
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }


    public async Task<PriceUpdateResult> PackOrderAsync(StoreProfile s, FbsOrderRow order, CancellationToken ct = default)
    {
        try
        {
            if (s.Marketplace == Marketplace.Wildberries)
            {
                if (!long.TryParse(order.ExternalOrderId, out var orderId))
                    throw new InvalidOperationException("WB order ID không hợp lệ.");

                using var create = await http.SendAsync(
                    Request(HttpMethod.Post, "https://marketplace-api.wildberries.ru/api/v3/supplies", s,
                        JsonSerializer.Serialize(new { name = "MarketplaceHub " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") })), ct);
                var createText = await create.Content.ReadAsStringAsync(ct);
                Ensure(create, createText);

                var supplyId = JsonNode.Parse(createText)?["id"]?.ToString()
                               ?? JsonNode.Parse(createText)?["supplyId"]?.ToString();
                if (string.IsNullOrWhiteSpace(supplyId))
                    throw new InvalidOperationException("WB không trả supplyId.");

                using var add = await http.SendAsync(
                    Request(new HttpMethod("PATCH"), $"https://marketplace-api.wildberries.ru/api/marketplace/v3/supplies/{supplyId}/orders", s,
                        JsonSerializer.Serialize(new { orders = new[] { orderId } })), ct);
                var addText = await add.Content.ReadAsStringAsync(ct);
                if (!add.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"Không thêm được order vào supply: HTTP {(int)add.StatusCode}: {Short(addText)}");

                return new PriceUpdateResult(true, $"Đã tạo supply {supplyId} và chuyển order sang trạng thái confirm.", supplyId);
            }

            if (s.Marketplace == Marketplace.Ozon)
            {
                var raw = JsonNode.Parse(order.RawJson);
                var products = raw?["products"]?.AsArray() ?? new JsonArray();
                var packProducts = new JsonArray();

                foreach (var p in products)
                {
                    if (!long.TryParse(p?["sku"]?.ToString(), out var productId))
                    {
                        if (!long.TryParse(p?["product_id"]?.ToString(), out productId))
                            continue;
                    }

                    packProducts.Add(new JsonObject
                    {
                        ["product_id"] = productId,
                        ["quantity"] = p?["quantity"]?.GetValue<int>() ?? 1
                    });
                }

                if (packProducts.Count == 0)
                    throw new InvalidOperationException("Không lấy được product_id Ozon từ order.");

                var body = new JsonObject
                {
                    ["posting_number"] = order.ExternalOrderId,
                    ["packages"] = new JsonArray(new JsonObject { ["products"] = packProducts }),
                    ["with"] = new JsonObject { ["additional_data"] = true }
                }.ToJsonString();

                using var ship = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v4/posting/fbs/ship", s, body), ct);
                var shipText = await ship.Content.ReadAsStringAsync(ct);
                if (!ship.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"Ozon ship HTTP {(int)ship.StatusCode}: {Short(shipText)}");

                await Task.Delay(1200, ct);
                using var verify = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/posting/fbs/get", s,
                    JsonSerializer.Serialize(new { posting_number = order.ExternalOrderId, with = new { analytics_data = false, financial_data = false } })), ct);
                var verifyText = await verify.Content.ReadAsStringAsync(ct);
                if (!verify.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"Ozon verify HTTP {(int)verify.StatusCode}: {Short(verifyText)}");

                var status = JsonNode.Parse(verifyText)?["result"]?["status"]?.ToString() ?? "";
                var substatus = JsonNode.Parse(verifyText)?["result"]?["substatus"]?.ToString() ?? "";
                if (!status.Equals("awaiting_deliver", StringComparison.OrdinalIgnoreCase) &&
                    substatus.Equals("ship_failed", StringComparison.OrdinalIgnoreCase))
                    return new PriceUpdateResult(false, "Ozon trả ship_failed sau khi gửi lệnh đóng hàng.");

                return new PriceUpdateResult(true, $"Ozon đã tiếp nhận đóng hàng. Status: {status}/{substatus}");
            }

            if (string.IsNullOrWhiteSpace(s.CampaignId))
                throw new InvalidOperationException("Thiếu Campaign ID Yandex.");

            var yBody = JsonSerializer.Serialize(new
            {
                order = new { status = "PROCESSING", substatus = "READY_TO_SHIP" }
            });
            using var y = await http.SendAsync(Request(HttpMethod.Put,
                $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders/{order.ExternalOrderId}/status",
                s, yBody), ct);
            var yText = await y.Content.ReadAsStringAsync(ct);
            return new PriceUpdateResult(
                y.IsSuccessStatusCode,
                y.IsSuccessStatusCode ? "Yandex đã tiếp nhận trạng thái READY_TO_SHIP." : $"Yandex HTTP {(int)y.StatusCode}: {Short(yText)}");
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, StoreProfile s, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (s.Marketplace == Marketplace.Wildberries)
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        else if (s.Marketplace == Marketplace.Ozon)
        {
            r.Headers.TryAddWithoutValidation("Client-Id", s.ClientId);
            r.Headers.TryAddWithoutValidation("Api-Key", s.ApiKey);
        }
        else
            r.Headers.TryAddWithoutValidation("Api-Key", s.ApiKey);

        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string SafeFile(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static void Ensure(HttpResponseMessage r, string body)
    {
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)r.StatusCode}: {Short(body)}");
    }

    private static string Short(string s) => s.Length > 500 ? s[..500] : s;
}
