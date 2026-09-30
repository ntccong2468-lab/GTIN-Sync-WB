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
            var byId = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

            async Task ReadOrdersUrl(string url)
            {
                using var res = await http.SendAsync(Request(HttpMethod.Get, url, s), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                foreach (var node in root?["orders"]?.AsArray() ?? new JsonArray())
                {
                    var id = node?["id"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(id) && node is not null) byId[id] = node.DeepClone();
                }
            }

            await ReadOrdersUrl("https://marketplace-api.wildberries.ru/api/v3/orders/new");

            var now = DateTimeOffset.UtcNow;
            var from = now.AddDays(-30).ToUnixTimeSeconds();
            var to = now.ToUnixTimeSeconds();
            long next = 0;
            for (var page = 0; page < 200; page++)
            {
                using var res = await http.SendAsync(Request(HttpMethod.Get,
                    $"https://marketplace-api.wildberries.ru/api/v3/orders?limit=1000&next={next}&dateFrom={from}&dateTo={to}", s), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                var arr = root?["orders"]?.AsArray() ?? new JsonArray();
                foreach (var node in arr)
                {
                    var id = node?["id"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(id) && node is not null) byId[id] = node.DeepClone();
                }
                var parsedNext = long.TryParse(root?["next"]?.ToString(), out var n) ? n : next;
                if (arr.Count < 1000 || parsedNext == next) break;
                next = parsedNext;
            }

            var statusById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = byId.Keys.Where(x => long.TryParse(x, out _)).Select(long.Parse).ToArray();
            foreach (var batch in ids.Chunk(1000))
            {
                using var res = await http.SendAsync(Request(HttpMethod.Post,
                    "https://marketplace-api.wildberries.ru/api/v3/orders/status", s,
                    JsonSerializer.Serialize(new { orders = batch })), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) continue;
                foreach (var st in JsonNode.Parse(text)?["orders"]?.AsArray() ?? new JsonArray())
                {
                    var id = st?["id"]?.ToString() ?? "";
                    var supplier = st?["supplierStatus"]?.ToString() ?? "";
                    var wb = st?["wbStatus"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(id))
                        statusById[id] = !string.IsNullOrWhiteSpace(supplier) ? supplier : wb;
                }
            }

            return byId.Select(kv =>
            {
                var x = kv.Value;
                var sku = x?["article"]?.ToString()
                          ?? x?["skus"]?.AsArray()?.FirstOrDefault()?.ToString()
                          ?? "";
                var status = statusById.TryGetValue(kv.Key, out var st) && !string.IsNullOrWhiteSpace(st) ? st : "new";
                var needsKiz = x?["requiredMeta"]?.AsArray()?.Any(m =>
                    string.Equals(m?.ToString(), "sgtin", StringComparison.OrdinalIgnoreCase)) ?? false;
                return new FbsOrderRow(
                    s.Id, s.Marketplace, kv.Key, sku, sku, 1, status, needsKiz,
                    x?.ToJsonString() ?? "{}");
            }).ToList();
        }

        if (s.Marketplace == Marketplace.Ozon)
        {
            var result = new Dictionary<string, List<FbsOrderRow>>(StringComparer.OrdinalIgnoreCase);
            var cutoffFrom = DateTimeOffset.UtcNow.AddDays(-180).ToString("O");
            var cutoffTo = DateTimeOffset.UtcNow.AddDays(180).ToString("O");
            var cursor = "";

            for (var page = 0; page < 20000; page++)
            {
                var body = JsonSerializer.Serialize(new
                {
                    filter = new { cutoff_from = cutoffFrom, cutoff_to = cutoffTo },
                    with = new { analytics_data = false, barcodes = false, financial_data = false, legal_info = false },
                    sort_dir = "asc",
                    translit = false,
                    cursor,
                    limit = 100
                });

                using var res = await http.SendAsync(Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v4/posting/fbs/unfulfilled/list", s, body), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                var postings = root?["result"]?["postings"]?.AsArray()
                               ?? root?["postings"]?.AsArray()
                               ?? new JsonArray();

                foreach (var post in postings)
                {
                    var number = post?["posting_number"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(number)) continue;
                    var rows = new List<FbsOrderRow>();
                    var requirementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var reqName in new[] { "products_requiring_gtd", "products_requiring_country", "products_requiring_jw_uin" })
                        foreach (var id in post?["requirements"]?[reqName]?.AsArray() ?? new JsonArray())
                            if (id is not null) requirementIds.Add(id.ToString());

                    foreach (var item in post?["products"]?.AsArray() ?? new JsonArray())
                    {
                        var offer = item?["offer_id"]?.ToString() ?? "";
                        var productId = item?["product_id"]?.ToString() ?? item?["sku"]?.ToString() ?? "";
                        rows.Add(new FbsOrderRow(
                            s.Id,
                            s.Marketplace,
                            number,
                            offer,
                            item?["name"]?.ToString() ?? offer,
                            item?["quantity"]?.GetValue<int>() ?? 1,
                            post?["status"]?.ToString() ?? "",
                            requirementIds.Contains(productId) || requirementIds.Contains(offer),
                            post?.ToJsonString() ?? "{}"));
                    }
                    result[number] = rows;
                }

                var next = root?["result"]?["cursor"]?.ToString()
                           ?? root?["cursor"]?.ToString()
                           ?? "";
                var hasNext = root?["result"]?["has_next"]?.GetValue<bool?>()
                              ?? root?["has_next"]?.GetValue<bool?>()
                              ?? !string.IsNullOrWhiteSpace(next);
                if (!hasNext || postings.Count == 0 || string.IsNullOrWhiteSpace(next) || next == cursor) break;
                cursor = next;
            }

            return result.Values.SelectMany(x => x).ToList();
        }

        if (string.IsNullOrWhiteSpace(s.CampaignId)) throw new InvalidOperationException("Thiếu Campaign ID Yandex.");
        using var yRes = await http.SendAsync(Request(HttpMethod.Get, $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders", s), ct);
        var yText = await yRes.Content.ReadAsStringAsync(ct);
        Ensure(yRes, yText);

        var orders = JsonNode.Parse(yText)?["orders"]?.AsArray()
                     ?? JsonNode.Parse(yText)?["result"]?["orders"]?.AsArray()
                     ?? new JsonArray();

        var yResult = new List<FbsOrderRow>();
        foreach (var order in orders)
        {
            var status = order?["status"]?.ToString() ?? "";
            var sub = order?["substatus"]?.ToString() ?? "";
            foreach (var item in order?["items"]?.AsArray() ?? new JsonArray())
                yResult.Add(new FbsOrderRow(
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
        return yResult;
    }

    public async Task<IReadOnlyList<FboSupplyRow>> SyncFboSuppliesAsync(StoreProfile s, CancellationToken ct = default)
    {
        if (s.Marketplace == Marketplace.Wildberries)
        {
            var rows = new List<FboSupplyRow>();
            for (var offset = 0; offset < 5000; offset += 100)
            {
                using var req = RequestWbRawAuth(HttpMethod.Post,
                    $"https://supplies-api.wildberries.ru/api/v1/supplies?limit=100&offset={offset}", s, "{}");
                using var res = await http.SendAsync(req, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                var arr = root?["supplies"]?.AsArray()
                          ?? root?["result"]?["supplies"]?.AsArray()
                          ?? (root is JsonArray a ? a : new JsonArray());
                foreach (var x in arr)
                {
                    var preorder = x?["preorderID"]?.ToString() ?? "";
                    var supply = x?["supplyID"]?.ToString() ?? "";
                    var orderId = !string.IsNullOrWhiteSpace(preorder) ? preorder : supply;
                    if (string.IsNullOrWhiteSpace(orderId)) continue;
                    var status = x?["statusID"]?.ToString() ?? "";
                    var warehouse = x?["warehouseName"]?.ToString()
                                   ?? x?["plannedWarehouseName"]?.ToString()
                                   ?? "";
                    var planned = x?["supplyDate"]?.ToString()
                                  ?? x?["plannedDate"]?.ToString()
                                  ?? "";
                    rows.Add(new FboSupplyRow(
                        s.Id, s.Marketplace, orderId, supply, status, warehouse, planned,
                        ParseInt(x?["quantity"]), ParseInt(x?["acceptedQuantity"]),
                        x?.ToJsonString() ?? "{}"));
                }
                if (arr.Count < 100) break;
            }
            return rows;
        }

        if (s.Marketplace == Marketplace.Ozon)
        {
            var ids = new List<string>();
            var cursor = "";
            var states = new[]
            {
                "DATA_FILLING", "READY_TO_SUPPLY", "ACCEPTED_AT_SUPPLY_WAREHOUSE", "IN_TRANSIT",
                "ACCEPTANCE_AT_STORAGE_WAREHOUSE", "REPORTS_CONFIRMATION_AWAITING", "REPORT_REJECTED",
                "COMPLETED", "REJECTED_AT_SUPPLY_WAREHOUSE", "CANCELLED", "OVERDUE"
            };
            for (var page = 0; page < 100; page++)
            {
                var body = JsonSerializer.Serialize(new
                {
                    filter = new { states },
                    last_id = cursor,
                    limit = 100,
                    sort_by = "ORDER_STATE_UPDATED_AT",
                    sort_dir = "DESC"
                });
                using var res = await http.SendAsync(Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v3/supply-order/list", s, body), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                foreach (var id in root?["order_ids"]?.AsArray() ?? root?["result"]?["order_ids"]?.AsArray() ?? new JsonArray())
                    if (id is not null && !string.IsNullOrWhiteSpace(id.ToString())) ids.Add(id.ToString());
                var next = root?["last_id"]?.ToString() ?? root?["result"]?["last_id"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(next) || next == cursor) break;
                cursor = next;
            }

            var rows = new List<FboSupplyRow>();
            foreach (var batch in ids.Distinct().Chunk(50))
            {
                using var res = await http.SendAsync(Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v3/supply-order/get", s,
                    JsonSerializer.Serialize(new { order_ids = batch })), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                Ensure(res, text);
                var root = JsonNode.Parse(text);
                var orders = root?["orders"]?.AsArray()
                             ?? root?["result"]?["orders"]?.AsArray()
                             ?? root?["items"]?.AsArray()
                             ?? new JsonArray();
                foreach (var x in orders)
                {
                    var orderId = x?["order_id"]?.ToString() ?? x?["order_number"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(orderId)) continue;
                    var supplies = x?["supplies"]?.AsArray() ?? new JsonArray();
                    var first = supplies.FirstOrDefault();
                    var supplyId = first?["supply_id"]?.ToString() ?? "";
                    var warehouse = x?["drop_off_warehouse"]?["name"]?.ToString()
                                   ?? first?["storage_warehouse"]?["name"]?.ToString()
                                   ?? "";
                    var planned = x?["timeslot"]?["timeslot"]?["from"]?.ToString()
                                  ?? x?["state_updated_date"]?.ToString()
                                  ?? "";
                    rows.Add(new FboSupplyRow(
                        s.Id, s.Marketplace, orderId, supplyId,
                        x?["state"]?.ToString() ?? first?["state"]?.ToString() ?? "",
                        warehouse, planned, 0, 0, x?.ToJsonString() ?? "{}"));
                }
            }
            return rows;
        }

        return Array.Empty<FboSupplyRow>();
    }

    public async Task<FinanceSnapshot> ReadFinanceAsync(StoreProfile s, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (s.Marketplace != Marketplace.Wildberries)
            throw new NotSupportedException("Bản 0.6.0 chỉ đọc quyết toán tài chính trực tiếp cho Wildberries.");

        decimal revenue = 0, payout = 0, delivery = 0, storage = 0, acceptance = 0;
        decimal deductions = 0, penalties = 0, additional = 0, cashback = 0;
        var count = 0;
        var fromText = from.ToString("yyyy-MM-dd");
        var toText = to.ToString("yyyy-MM-dd");

        for (var offset = 0; offset < 50000; offset += 1000)
        {
            var body = JsonSerializer.Serialize(new
            {
                dateFrom = fromText + "T00:00:00+03:00",
                dateTo = toText + "T23:59:59+03:00",
                period = "daily",
                limit = 1000,
                offset
            });
            using var req = RequestWbRawAuth(HttpMethod.Post,
                "https://finance-api.wildberries.ru/api/finance/v1/sales-reports/list", s, body);
            using var res = await http.SendAsync(req, ct);
            if ((int)res.StatusCode == 204) break;
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);
            var arr = JsonNode.Parse(text) as JsonArray ?? new JsonArray();
            foreach (var x in arr)
            {
                revenue += ParseDecimal(x?["retailAmountSum"]?.ToString()) ?? 0;
                payout += ParseDecimal(x?["forPaySum"]?.ToString()) ?? 0;
                delivery += ParseDecimal(x?["deliveryServiceSum"]?.ToString()) ?? 0;
                storage += ParseDecimal(x?["paidStorageSum"]?.ToString()) ?? 0;
                acceptance += ParseDecimal(x?["paidAcceptanceSum"]?.ToString()) ?? 0;
                deductions += ParseDecimal(x?["deductionSum"]?.ToString()) ?? 0;
                penalties += ParseDecimal(x?["penaltySum"]?.ToString()) ?? 0;
                additional += ParseDecimal(x?["additionalPaymentSum"]?.ToString()) ?? 0;
                cashback += ParseDecimal(x?["cashbackAmountSum"]?.ToString()) ?? 0;
                count++;
            }
            if (arr.Count < 1000) break;
        }

        return new FinanceSnapshot("RUB", revenue, payout, delivery, storage, acceptance,
            deductions, penalties, additional, cashback, count, fromText, toText);
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
                if (offer["pictures"] is null && !string.IsNullOrWhiteSpace(p.ImageUrl))
                    offer["pictures"] = new JsonArray(p.ImageUrl);

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
                if (!res.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"HTTP {(int)res.StatusCode}: {Short(text)}");

                var photoLinks = (c["photos"]?.AsArray() ?? new JsonArray())
                    .Select(x => x?["big"]?.ToString()
                              ?? x?["c516x688"]?.ToString()
                              ?? x?["square"]?.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct()
                    .Take(30)
                    .ToArray();

                if (photoLinks.Length == 0 && !string.IsNullOrWhiteSpace(p.ImageUrl))
                    photoLinks = new[] { p.ImageUrl };

                if (photoLinks.Length == 0)
                    return new PriceUpdateResult(true, "WB đã tạo card nhưng sản phẩm nguồn không có ảnh để sao chép.");

                var newNmId = await WaitForWbCardAsync(dst, destinationSku, ct);
                if (!newNmId.HasValue)
                    return new PriceUpdateResult(true, "WB đã tiếp nhận tạo card. Chưa lấy được nmID mới để gắn ảnh ngay; hãy chạy lại sao chép ảnh sau khi card xuất hiện.");

                var mediaBody = JsonSerializer.Serialize(new { nmId = newNmId.Value, data = photoLinks });
                using var mediaRes = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v3/media/save", dst, mediaBody), ct);
                var mediaText = await mediaRes.Content.ReadAsStringAsync(ct);
                if (!mediaRes.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"Card WB đã tạo nhưng tải ảnh thất bại. HTTP {(int)mediaRes.StatusCode}: {Short(mediaText)}");

                var verified = await VerifyWbMediaAsync(dst, newNmId.Value, ct);
                return new PriceUpdateResult(
                    verified,
                    verified
                        ? $"Đã sao chép card WB và {photoLinks.Length} ảnh."
                        : "WB đã nhận ảnh nhưng chưa xác minh được ảnh trên card mới. Hãy đồng bộ lại sau vài phút.",
                    newNmId.Value.ToString());
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
            if (!copyRes.IsSuccessStatusCode)
                return new PriceUpdateResult(false, $"HTTP {(int)copyRes.StatusCode}: {Short(copyText)}");

            var taskId = JsonNode.Parse(copyText)?["result"]?["task_id"]?.ToString();
            var imageUrls = item["images"]?.AsArray()
                ?.Select(x => x?.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct()
                .ToArray() ?? Array.Empty<string>();

            if (imageUrls.Length == 0 && !string.IsNullOrWhiteSpace(p.ImageUrl))
                imageUrls = new[] { p.ImageUrl };

            if (imageUrls.Length > 0)
            {
                var destinationProductId = await WaitForOzonProductAsync(dst, destinationSku, ct);
                if (destinationProductId.HasValue)
                {
                    var picturesBody = JsonSerializer.Serialize(new
                    {
                        product_id = destinationProductId.Value,
                        images = imageUrls,
                        color_image = ""
                    });
                    using var picRes = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/product/pictures/import", dst, picturesBody), ct);
                    var picText = await picRes.Content.ReadAsStringAsync(ct);
                    if (!picRes.IsSuccessStatusCode)
                        return new PriceUpdateResult(false, $"Sản phẩm Ozon đã tạo nhưng tải ảnh thất bại. HTTP {(int)picRes.StatusCode}: {Short(picText)}");

                    var verified = await VerifyOzonMediaAsync(dst, destinationProductId.Value, ct);
                    return new PriceUpdateResult(
                        verified,
                        verified
                            ? $"Đã sao chép sản phẩm Ozon và {imageUrls.Length} ảnh."
                            : "Ozon đã nhận ảnh nhưng ảnh còn đang xử lý. Hãy đồng bộ lại sau.",
                        taskId);
                }
            }

            return new PriceUpdateResult(true,
                imageUrls.Length == 0
                    ? "Ozon đã tạo sản phẩm nhưng nguồn không có ảnh để sao chép."
                    : "Ozon đã tạo sản phẩm; chưa lấy được product_id đích để tải ảnh ngay.",
                taskId);
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }


    private async Task<long?> WaitForWbCardAsync(StoreProfile store, string vendorCode, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var body = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["cursor"] = new JsonObject { ["limit"] = 100 },
                    ["filter"] = new JsonObject
                    {
                        ["textSearch"] = vendorCode,
                        ["withPhoto"] = -1
                    }
                }
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/get/cards/list", store, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                var cards = JsonNode.Parse(text)?["cards"]?.AsArray() ?? new JsonArray();
                var match = cards.FirstOrDefault(x => string.Equals(x?["vendorCode"]?.ToString(), vendorCode, StringComparison.OrdinalIgnoreCase));
                if (long.TryParse(match?["nmID"]?.ToString(), out var nmId)) return nmId;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    private async Task<bool> VerifyWbMediaAsync(StoreProfile store, long nmId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var body = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["cursor"] = new JsonObject { ["limit"] = 10 },
                    ["filter"] = new JsonObject
                    {
                        ["textSearch"] = nmId.ToString(),
                        ["withPhoto"] = -1
                    }
                }
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://content-api.wildberries.ru/content/v2/get/cards/list", store, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                var card = JsonNode.Parse(text)?["cards"]?.AsArray()?.FirstOrDefault(x => x?["nmID"]?.ToString() == nmId.ToString());
                if ((card?["photos"]?.AsArray()?.Count ?? 0) > 0) return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private async Task<long?> WaitForOzonProductAsync(StoreProfile store, string offerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var body = JsonSerializer.Serialize(new
            {
                filter = new { offer_id = new[] { offerId }, visibility = "ALL" },
                last_id = "",
                limit = 100
            });

            using var res = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/product/list", store, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                var items = JsonNode.Parse(text)?["result"]?["items"]?.AsArray() ?? new JsonArray();
                var match = items.FirstOrDefault(x => string.Equals(x?["offer_id"]?.ToString(), offerId, StringComparison.OrdinalIgnoreCase));
                if (long.TryParse(match?["product_id"]?.ToString(), out var id)) return id;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    private async Task<bool> VerifyOzonMediaAsync(StoreProfile store, long productId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var res = await http.SendAsync(Request(
                HttpMethod.Post,
                "https://api-seller.ozon.ru/v2/product/pictures/info",
                store,
                JsonSerializer.Serialize(new { product_id = new[] { productId.ToString() } })), ct);

            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                var item = JsonNode.Parse(text)?["items"]?.AsArray()?.FirstOrDefault();
                var count = (item?["primary_photo"]?.AsArray()?.Count ?? 0) + (item?["photo"]?.AsArray()?.Count ?? 0);
                if (count > 0) return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
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

    private HttpRequestMessage RequestWbRawAuth(HttpMethod method, string url, StoreProfile s, string? json = null)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Authorization", s.Token);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    private static int ParseInt(JsonNode? value) =>
        int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;

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
