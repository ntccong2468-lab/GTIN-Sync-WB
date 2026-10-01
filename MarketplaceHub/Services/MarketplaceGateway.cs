using MarketplaceHub.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed partial class MarketplaceGateway
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<ApiTestResult> TestAsync(StoreProfile s, CancellationToken ct = default)
    {
        try
        {
            using var req = s.Marketplace switch
            {
                Marketplace.Wildberries => Request(HttpMethod.Get, "https://common-api.wildberries.ru/ping", s),
                Marketplace.Ozon => Request(HttpMethod.Post, "https://api-seller.ozon.ru/v1/seller/info", s, "{}"),
                Marketplace.Yandex => Request(HttpMethod.Get, "https://api.partner.market.yandex.ru/v2/campaigns", s),
                _ => throw new NotSupportedException()
            };
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return new ApiTestResult(
                res.IsSuccessStatusCode,
                res.IsSuccessStatusCode ? "Kết nối API thành công." : s.Marketplace==Marketplace.Ozon
                    ? SafeOzonHttpMessage(res.StatusCode,res.Headers.RetryAfter?.ToString())
                    : $"HTTP {(int)res.StatusCode}: {Short(body)}");
        }
        catch (Exception ex) { return new ApiTestResult(false, ex.Message); }
    }

    public async Task<IReadOnlyList<ProductRow>> SyncProductsAsync(StoreProfile s, CancellationToken ct = default)
    {
        return await ReadAllCatalogProductsAsync(s,ct);
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
                FirstImageUrl(x["photos"]),
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
                var image = FirstImageUrl(x?["images"]);
                result.Add(new ProductRow(s.Id, s.Marketplace, external, sku, name, price, image, x?.ToJsonString() ?? "{}"));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<ProductRow>> YandexProducts(StoreProfile s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BusinessId)) throw new InvalidOperationException("Thiếu Business ID.");

        var result = new List<ProductRow>();
        string? pageToken = null;

        for (var page = 0; page < 10000; page++)
        {
            var url = $"https://api.partner.market.yandex.ru/v2/businesses/{s.BusinessId}/offer-mappings?limit=100";
            if (!string.IsNullOrWhiteSpace(pageToken))
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);

            using var res = await http.SendAsync(Request(HttpMethod.Post, url, s, "{}"), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            Ensure(res, text);

            var root = JsonNode.Parse(text);
            var arr = root?["result"]?["offerMappings"]?.AsArray() ?? new JsonArray();
            foreach (var x in arr.Where(x => x is not null))
            {
                var offer = x!["offer"];
                var sku = offer?["offerId"]?.ToString() ?? "";
                var price = ParseDecimal(offer?["basicPrice"]?["value"]?.ToString())
                            ?? ParseDecimal(offer?["price"]?["value"]?.ToString());
                result.Add(new ProductRow(
                    s.Id,
                    s.Marketplace,
                    x?["mapping"]?["marketSku"]?.ToString() ?? sku,
                    sku,
                    offer?["name"]?.ToString() ?? sku,
                    price,
                    FirstImageUrl(offer?["pictures"] ?? x?["mediaFiles"]?["pictures"]),
                    x!.ToJsonString()));
            }

            var next = root?["result"]?["paging"]?["nextPageToken"]?.ToString()
                       ?? root?["result"]?["nextPageToken"]?.ToString()
                       ?? root?["paging"]?["nextPageToken"]?.ToString()
                       ?? "";
            if (arr.Count == 0 || string.IsNullOrWhiteSpace(next) || next == pageToken) break;
            pageToken = next;
        }

        return result;
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
                Ensure(res, text);
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
                if (!statusById.TryGetValue(kv.Key, out var st) || string.IsNullOrWhiteSpace(st))
                    throw new InvalidOperationException($"WB chưa trả trạng thái cho đơn {kv.Key}. Giữ dữ liệu trước đó và đồng bộ lại.");
                var status = st;
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
                    foreach (var reqName in new[] { "products_requiring_mandatory_mark", "products_requiring_mark" })
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

        var yResult = new List<FbsOrderRow>();
        var pageToken = "";
        for (var page = 0; page < 10000; page++)
        {
            var url = $"https://api.partner.market.yandex.ru/v2/campaigns/{s.CampaignId}/orders?limit=50";
            if (!string.IsNullOrWhiteSpace(pageToken))
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);

            using var yRes = await http.SendAsync(Request(HttpMethod.Get, url, s), ct);
            var yText = await yRes.Content.ReadAsStringAsync(ct);
            Ensure(yRes, yText);
            var root = JsonNode.Parse(yText);
            var orders = root?["orders"]?.AsArray()
                         ?? root?["result"]?["orders"]?.AsArray()
                         ?? new JsonArray();

            foreach (var order in orders)
            {
                var status = order?["status"]?.ToString() ?? "";
                var sub = order?["substatus"]?.ToString() ?? "";
                foreach (var item in order?["items"]?.AsArray() ?? new JsonArray())
                {
                    var instances = item?["instances"]?.AsArray() ?? new JsonArray();
                    var requiresMark = item?["hasCis"]?.GetValue<bool?>() == true || instances.Count > 0 ||
                        item?["requiredMeta"]?.AsArray()?.Any(x =>
                            string.Equals(x?.ToString(), "CIS", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x?.ToString(), "SGTIN", StringComparison.OrdinalIgnoreCase)) == true;

                    yResult.Add(new FbsOrderRow(
                        s.Id,
                        s.Marketplace,
                        order?["id"]?.ToString() ?? "",
                        item?["offerId"]?.ToString() ?? "",
                        item?["offerName"]?.ToString() ?? "",
                        item?["count"]?.GetValue<int>() ?? 1,
                        $"{status}/{sub}",
                        requiresMark,
                        order?.ToJsonString() ?? "{}"));
                }
            }

            var next = root?["paging"]?["nextPageToken"]?.ToString()
                       ?? root?["result"]?["paging"]?["nextPageToken"]?.ToString()
                       ?? "";
            if (orders.Count == 0 || string.IsNullOrWhiteSpace(next) || next == pageToken) break;
            pageToken = next;
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
        if (s.Marketplace == Marketplace.Wildberries)
            return (await DownloadLabelsAsync(s, new[] { orderId }, ct))[orderId];
        try
        {
            byte[] fileBytes;
            string ext;

            if (s.Marketplace == Marketplace.Ozon)
            {
                var taskId=await CreateOzonLabelTaskAsync(s,new[]{orderId},ct).ConfigureAwait(false);
                return await DownloadOzonLabelTaskAsync(s,orderId,taskId,ct).ConfigureAwait(false);
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

            if (ext == ".pdf" && (fileBytes.Length < 5 || Encoding.ASCII.GetString(fileBytes, 0, 5) != "%PDF-"))
                return new LabelResult(false, "Sàn chưa trả nhãn PDF hợp lệ. Hãy đồng bộ trạng thái đóng gói rồi tải nhãn lại.");
            if (ext == ".png" && (fileBytes.Length < 8 || !fileBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
                return new LabelResult(false, "WB chưa trả sticker PNG hợp lệ.");

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "Labels");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{s.Marketplace}-{SafeFile(orderId)}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllBytesAsync(path, fileBytes, ct);
            return new LabelResult(true, "Đã tải nhãn.", path);
        }
        catch (Exception ex) { return new LabelResult(false, ex.Message); }
    }


    private async Task<PriceUpdateResult> CopyWbToYandexAsync(
        StoreProfile dst, ProductRow p, string destinationSku, CancellationToken ct)
    {
        try
        {
            RequireCredentials(dst);
            if (string.IsNullOrWhiteSpace(dst.BusinessId))
                return new PriceUpdateResult(false, "Cửa hàng Yandex đích chưa có Business ID.");

            var card = JsonNode.Parse(p.RawJson)?.AsObject()
                       ?? throw new InvalidOperationException("Dữ liệu card WB nguồn không hợp lệ.");
            var categoryText = card["subjectName"]?.ToString()
                               ?? card["subjectNameTranslated"]?.ToString()
                               ?? p.Name;
            var category = await FindYandexCategoryAsync(dst, categoryText, p.Name, ct);
            if (category.Id <= 0)
                return new PriceUpdateResult(false, $"Không tự ánh xạ được danh mục Yandex cho '{categoryText}'.");

            var photos = await ExternalImageLinksAsync(WbPhotoLinks(card, p.ImageUrl), ct);
            if (photos.Count == 0)
                return new PriceUpdateResult(false, "Card WB nguồn không có ảnh công khai có thể gửi sang Yandex.");

            var brand = card["brand"]?.ToString();
            if (string.IsNullOrWhiteSpace(brand)) brand = "Без бренда";
            var description = card["description"]?.ToString();
            if (string.IsNullOrWhiteSpace(description)) description = p.Name;

            var offer = new JsonObject
            {
                ["offerId"] = destinationSku,
                ["name"] = Limit(p.Name, 256),
                ["marketCategoryId"] = category.Id,
                ["category"] = category.Name,
                ["pictures"] = new JsonArray(photos.Select(x => (JsonNode?)x).ToArray()),
                ["vendor"] = Limit(brand, 100),
                ["description"] = Limit(description, 6000),
                ["vendorCode"] = destinationSku
            };

            var barcode = WbBarcode(card);
            if (!string.IsNullOrWhiteSpace(barcode))
                offer["barcodes"] = new JsonArray(barcode);

            if (p.Price is > 0)
                offer["basicPrice"] = new JsonObject
                {
                    ["value"] = p.Price.Value,
                    ["currencyId"] = "RUR"
                };

            var body = new JsonObject
            {
                ["offerMappings"] = new JsonArray(new JsonObject { ["offer"] = offer }),
                ["onlyPartnerMediaContent"] = true
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Post,
                $"https://api.partner.market.yandex.ru/v2/businesses/{dst.BusinessId}/offer-mappings/update",
                dst, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new PriceUpdateResult(false, AuthFriendly(dst, res.StatusCode, text));

            return new PriceUpdateResult(true,
                $"Yandex đã nhận bài đăng '{destinationSku}'. Danh mục tự ánh xạ: {category.Name} ({category.Id}); {photos.Count} ảnh.");
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    private async Task<PriceUpdateResult> CopyWbToOzonAsync(
        StoreProfile dst, ProductRow p, string destinationSku, CancellationToken ct)
    {
        try
        {
            RequireCredentials(dst);
            var card = JsonNode.Parse(p.RawJson)?.AsObject()
                       ?? throw new InvalidOperationException("Dữ liệu card WB nguồn không hợp lệ.");
            var categoryText = card["subjectName"]?.ToString()
                               ?? card["subjectNameTranslated"]?.ToString()
                               ?? p.Name;
            var category = await FindOzonCategoryAsync(dst, categoryText, p.Name, ct);
            if (category.CategoryId <= 0 || category.TypeId <= 0)
                return new PriceUpdateResult(false, $"Không tự ánh xạ được danh mục Ozon cho '{categoryText}'.");

            var attrs = await BuildOzonAttributesAsync(dst, card, p, category, ct);
            if (!attrs.Ok)
                return new PriceUpdateResult(false, attrs.Message);

            var dims = WbDimensions(card);
            if (!dims.Ok)
                return new PriceUpdateResult(false,
                    "Không thể tạo card Ozon an toàn vì card WB chưa có đủ kích thước/khối lượng đóng gói. " +
                    "Hãy cập nhật dimensions trên WB rồi đồng bộ lại.");

            if (p.Price is null or <= 0)
                return new PriceUpdateResult(false, "Sản phẩm WB chưa có giá hợp lệ để tạo card Ozon.");

            var photos = await ExternalImageLinksAsync(WbPhotoLinks(card, p.ImageUrl), ct);
            if (photos.Count == 0)
                return new PriceUpdateResult(false, "Card WB nguồn không có ảnh công khai có thể gửi sang Ozon.");

            var item = new JsonObject
            {
                ["attributes"] = attrs.Attributes,
                ["barcode"] = WbBarcode(card),
                ["complex_attributes"] = new JsonArray(),
                ["currency_code"] = "RUB",
                ["depth"] = dims.DepthMm,
                ["description_category_id"] = category.CategoryId,
                ["dimension_unit"] = "mm",
                ["height"] = dims.HeightMm,
                ["images"] = new JsonArray(photos.Take(30).Select(x => (JsonNode?)x).ToArray()),
                ["name"] = Limit(p.Name, 500),
                ["offer_id"] = destinationSku,
                ["old_price"] = "0",
                ["price"] = p.Price.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                ["type_id"] = category.TypeId,
                ["weight"] = dims.WeightG,
                ["weight_unit"] = "g",
                ["width"] = dims.WidthMm
            };

            var body = new JsonObject
            {
                ["items"] = new JsonArray(item)
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Post,
                "https://api-seller.ozon.ru/v3/product/import", dst, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new PriceUpdateResult(false, AuthFriendly(dst, res.StatusCode, text));

            var taskId = JsonNode.Parse(text)?["result"]?["task_id"]?.ToString();
            return new PriceUpdateResult(true,
                $"Ozon đã nhận card '{destinationSku}'. Danh mục tự ánh xạ: {category.CategoryName} / {category.TypeName}; {photos.Count} ảnh.",
                taskId);
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    private async Task<(long Id, string Name)> FindYandexCategoryAsync(
        StoreProfile dst, string sourceCategory, string productName, CancellationToken ct)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Post,
            "https://api.partner.market.yandex.ru/v2/categories/tree", dst,
            JsonSerializer.Serialize(new { language = "RU" })), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(AuthFriendly(dst, res.StatusCode, text));

        var root = JsonNode.Parse(text)?["result"];
        var candidates = new List<(long Id, string Name, int Score)>();
        void Walk(JsonNode? node)
        {
            if (node is null) return;
            var children = node["children"]?.AsArray();
            if (children is null || children.Count == 0)
            {
                if (long.TryParse(node["id"]?.ToString(), out var id))
                {
                    var name = node["name"]?.ToString() ?? "";
                    candidates.Add((id, name, CategoryScore(sourceCategory + " " + productName, name)));
                }
                return;
            }
            foreach (var child in children) Walk(child);
        }
        Walk(root);
        var best = candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Name.Length).FirstOrDefault();
        return best.Score > 0 ? (best.Id, best.Name) : (0, "");
    }

    private async Task<OzonCategoryMatch> FindOzonCategoryAsync(
        StoreProfile dst, string sourceCategory, string productName, CancellationToken ct)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Post,
            "https://api-seller.ozon.ru/v1/description-category/tree", dst,
            JsonSerializer.Serialize(new { language = "RU" })), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(AuthFriendly(dst, res.StatusCode, text));

        var result = JsonNode.Parse(text)?["result"]?.AsArray() ?? new JsonArray();
        var candidates = new List<OzonCategoryMatch>();
        void Walk(JsonNode? node, long inheritedCategoryId, string inheritedCategoryName)
        {
            if (node is null) return;
            var disabled = bool.TryParse(node["disabled"]?.ToString(), out var d) && d;
            if (disabled) return;

            var categoryId = long.TryParse(node["description_category_id"]?.ToString(), out var parsedCategory)
                ? parsedCategory : inheritedCategoryId;
            var categoryName = node["category_name"]?.ToString();
            if (string.IsNullOrWhiteSpace(categoryName)) categoryName = inheritedCategoryName;
            var typeId = long.TryParse(node["type_id"]?.ToString(), out var parsedType) ? parsedType : 0;
            var typeName = node["type_name"]?.ToString() ?? "";

            if (categoryId > 0 && typeId > 0)
            {
                var score = CategoryScore(sourceCategory + " " + productName, categoryName + " " + typeName);
                candidates.Add(new OzonCategoryMatch(categoryId, typeId, categoryName, typeName, score));
            }

            foreach (var child in node["children"]?.AsArray() ?? new JsonArray())
                Walk(child, categoryId, categoryName);
        }
        foreach (var node in result) Walk(node, 0, "");
        return candidates.OrderByDescending(x => x.Score).ThenBy(x => x.CategoryName.Length + x.TypeName.Length)
            .FirstOrDefault() ?? new OzonCategoryMatch(0, 0, "", "", 0);
    }

    private async Task<(bool Ok, string Message, JsonArray Attributes)> BuildOzonAttributesAsync(
        StoreProfile dst, JsonObject card, ProductRow product, OzonCategoryMatch category, CancellationToken ct)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Post,
            "https://api-seller.ozon.ru/v1/description-category/attribute", dst,
            JsonSerializer.Serialize(new
            {
                description_category_id = category.CategoryId,
                type_id = category.TypeId,
                language = "RU"
            })), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return (false, AuthFriendly(dst, res.StatusCode, text), new JsonArray());

        var defs = JsonNode.Parse(text)?["result"]?.AsArray() ?? new JsonArray();
        var output = new JsonArray();
        var missing = new List<string>();

        foreach (var def in defs)
        {
            var required = bool.TryParse(def?["is_required"]?.ToString(), out var req) && req;
            if (!required) continue;
            if (!long.TryParse(def?["id"]?.ToString(), out var id)) continue;

            var name = def?["name"]?.ToString() ?? "";
            var value = MapWbValueForOzon(card, product, category, name);
            var dictionaryId = long.TryParse(def?["dictionary_id"]?.ToString(), out var dict) ? dict : 0;
            var complexId = long.TryParse(def?["attribute_complex_id"]?.ToString(), out var cx) ? cx : 0;

            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
                continue;
            }

            var values = new JsonArray();
            if (dictionaryId > 0)
            {
                using var search = await http.SendAsync(Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v1/description-category/attribute/values/search", dst,
                    JsonSerializer.Serialize(new
                    {
                        attribute_id = id,
                        description_category_id = category.CategoryId,
                        type_id = category.TypeId,
                        value,
                        limit = 20
                    })), ct);
                var searchText = await search.Content.ReadAsStringAsync(ct);
                if (!search.IsSuccessStatusCode)
                {
                    missing.Add(name);
                    continue;
                }

                var options = JsonNode.Parse(searchText)?["result"]?.AsArray() ?? new JsonArray();
                var best = options
                    .Select(x => new
                    {
                        Node = x,
                        Score = CategoryScore(value, x?["value"]?.ToString() ?? "")
                    })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault()?.Node;
                if (best is null || !long.TryParse(best["id"]?.ToString(), out var optionId))
                {
                    missing.Add(name);
                    continue;
                }
                values.Add(new JsonObject
                {
                    ["dictionary_value_id"] = optionId,
                    ["value"] = best["value"]?.ToString() ?? value
                });
            }
            else
            {
                values.Add(new JsonObject { ["value"] = value });
            }

            output.Add(new JsonObject
            {
                ["complex_id"] = complexId,
                ["id"] = id,
                ["values"] = values
            });
        }

        if (missing.Count > 0)
            return (false,
                "Ozon yêu cầu thêm thuộc tính mà WB chưa cung cấp đủ: " +
                string.Join(", ", missing.Distinct().Take(12)) +
                ". Hãy bổ sung các thuộc tính này trên card WB rồi đồng bộ lại.",
                output);

        return (true, "OK", output);
    }

    private static string MapWbValueForOzon(
        JsonObject card, ProductRow product, OzonCategoryMatch category, string ozonName)
    {
        var n = ozonName.ToLowerInvariant();
        if (n.Contains("бренд") || n.Contains("brand")) return card["brand"]?.ToString() ?? "Нет бренда";
        if (n.Contains("тип") || n.Contains("вид товара")) return !string.IsNullOrWhiteSpace(category.TypeName) ? category.TypeName : category.CategoryName;
        if (n.Contains("модель") || n.Contains("название")) return product.Name;
        if (n.Contains("артикул")) return product.Sku;
        if (n.Contains("цвет")) return FindWbCharacteristic(card, "цвет", "color");
        if (n.Contains("пол") || n.Contains("гендер")) return FindWbCharacteristic(card, "пол", "gender");
        if (n.Contains("размер")) return FindWbCharacteristic(card, "размер", "size");
        if (n.Contains("материал") || n.Contains("состав")) return FindWbCharacteristic(card, "материал", "состав", "fabric");
        if (n.Contains("страна")) return FindWbCharacteristic(card, "страна", "country");
        if (n.Contains("описан") || n.Contains("аннотац")) return card["description"]?.ToString() ?? product.Name;

        var byName = FindWbCharacteristic(card, ozonName);
        return byName;
    }

    private static string FindWbCharacteristic(JsonObject card, params string[] names)
    {
        foreach (var c in card["characteristics"]?.AsArray() ?? new JsonArray())
        {
            var n = c?["name"]?.ToString() ?? "";
            if (!names.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase) ||
                                x.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
            var value = c?["value"];
            if (value is JsonArray arr)
                return string.Join(", ", arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            return value?.ToString() ?? "";
        }
        return "";
    }

    private static (bool Ok, int WidthMm, int HeightMm, int DepthMm, int WeightG) WbDimensions(JsonObject card)
    {
        var d = card["dimensions"];
        decimal Read(string key) => decimal.TryParse(d?[key]?.ToString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        var width = Read("width");
        var height = Read("height");
        var depth = Read("length");
        if (depth <= 0) depth = Read("depth");
        var weightKg = Read("weightBrutto");
        if (weightKg <= 0) weightKg = Read("weight");

        if (width <= 0 || height <= 0 || depth <= 0 || weightKg <= 0)
            return (false, 0, 0, 0, 0);

        return (true,
            Math.Max(1, (int)Math.Round(width * 10m)),
            Math.Max(1, (int)Math.Round(height * 10m)),
            Math.Max(1, (int)Math.Round(depth * 10m)),
            Math.Max(1, (int)Math.Round(weightKg * 1000m)));
    }

    private static IReadOnlyList<string> WbPhotoLinks(JsonObject card, string fallback)
    {
        var links = new List<string>();
        foreach (var photo in card["photos"]?.AsArray() ?? new JsonArray())
        {
            var link = photo?["big"]?.ToString()
                       ?? photo?["c516x688"]?.ToString()
                       ?? photo?["square"]?.ToString();
            if (!string.IsNullOrWhiteSpace(link)) links.Add(link);
        }
        if (links.Count == 0 && !string.IsNullOrWhiteSpace(fallback)) links.Add(fallback);
        return links.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
    }

    private async Task<IReadOnlyList<string>> ExternalImageLinksAsync(
        IReadOnlyList<string> links, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var original in links)
        {
            var url = original.Trim();
            if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                var jpg = url[..^5] + ".jpg";
                try
                {
                    using var probe = new HttpRequestMessage(HttpMethod.Get, jpg);
                    using var res = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (res.IsSuccessStatusCode &&
                        (res.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
                        url = jpg;
                }
                catch { }
            }
            result.Add(url);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string WbBarcode(JsonObject card)
    {
        foreach (var size in card["sizes"]?.AsArray() ?? new JsonArray())
            foreach (var sku in size?["skus"]?.AsArray() ?? new JsonArray())
            {
                var value = sku?.ToString() ?? "";
                if (value.Length is >= 8 and <= 14 && value.All(char.IsDigit)) return value;
            }
        return "";
    }

    private static int CategoryScore(string source, string target)
    {
        static string[] Tokens(string value) => value
            .ToLowerInvariant()
            .Replace("ё", "е")
            .Split(new[] { ' ', '-', '_', '/', '\\', ',', '.', '(', ')', '[', ']', ':' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .Distinct()
            .ToArray();

        var a = Tokens(source);
        var b = Tokens(target);
        if (a.Length == 0 || b.Length == 0) return 0;
        var score = a.Count(x => b.Any(y => y.Equals(x, StringComparison.OrdinalIgnoreCase))) * 10;
        var src = string.Join(" ", a);
        var dst = string.Join(" ", b);
        if (dst.Contains(src, StringComparison.OrdinalIgnoreCase) || src.Contains(dst, StringComparison.OrdinalIgnoreCase))
            score += 50;
        return score;
    }

    private static string Limit(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Length <= max ? value : value[..max];

    private static void RequireCredentials(StoreProfile s)
    {
        if (s.Marketplace == Marketplace.Wildberries && string.IsNullOrWhiteSpace(s.Token))
            throw new InvalidOperationException("Cửa hàng Wildberries chưa có Token API. Hãy mở Cài đặt và lưu token trước.");
        if (s.Marketplace == Marketplace.Ozon &&
            (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(s.ApiKey)))
            throw new InvalidOperationException("Cửa hàng Ozon đích thiếu Client-Id hoặc Api-Key.");
        if (s.Marketplace == Marketplace.Yandex && string.IsNullOrWhiteSpace(s.ApiKey))
            throw new InvalidOperationException("Cửa hàng Yandex đích chưa có API Key.");
    }

    private static string AuthFriendly(StoreProfile store, System.Net.HttpStatusCode status, string body)
    {
        if(store.Marketplace==Marketplace.Ozon)return SafeOzonHttpMessage(status,null);
        if ((int)status == 401 || (int)status == 403)
            return $"{store.Marketplace} từ chối thông tin API (HTTP {(int)status}). " +
                   "Hãy kiểm tra token/API key của đúng cửa hàng đích và quyền quản lý sản phẩm. " +
                   Short(body);
        return $"HTTP {(int)status}: {Short(body)}";
    }

    private sealed record OzonCategoryMatch(
        long CategoryId, long TypeId, string CategoryName, string TypeName, int Score);


    public async Task<PriceUpdateResult> CopyListingAsync(StoreProfile src, StoreProfile dst, ProductRow p, string destinationSku, CancellationToken ct = default)
    {
        try
        {
            if (src.Marketplace != dst.Marketplace)
            {
                if (src.Marketplace == Marketplace.Wildberries && dst.Marketplace == Marketplace.Ozon)
                    return await CopyWbToOzonAsync(dst, p, destinationSku, ct);
                if (src.Marketplace == Marketplace.Wildberries && dst.Marketplace == Marketplace.Yandex)
                    return await CopyWbToYandexAsync(dst, p, destinationSku, ct);
                return new PriceUpdateResult(false, "Hiện luồng copy khác sàn hỗ trợ trực tiếp WB → Ozon và WB → Yandex Market.");
            }

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
                        return new PriceUpdateResult(false, "Sản phẩm Ozon đã tạo nhưng tải ảnh thất bại. "+SafeOzonHttpMessage(picRes.StatusCode,picRes.Headers.RetryAfter?.ToString()));

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


    public async Task<PriceUpdateResult> PrepareOzonKizAsync(
        StoreProfile store,
        string postingNumber,
        IReadOnlyDictionary<string, IReadOnlyList<string>> codesByOffer,
        CancellationToken ct = default,
        Func<bool>? beforeSet = null)
    {
        try
        {
            if (store.Marketplace != Marketplace.Ozon)
                return new PriceUpdateResult(false, "Luồng exemplar KIZ này chỉ áp dụng cho Ozon.");

            using var detailRes = await http.SendAsync(Request(HttpMethod.Post,
                "https://api-seller.ozon.ru/v3/posting/fbs/get", store,
                JsonSerializer.Serialize(new
                {
                    posting_number = postingNumber,
                    with = new { analytics_data = false, financial_data = false, product_exemplars = true }
                })), ct);
            var detailText = await detailRes.Content.ReadAsStringAsync(ct);
            if (!detailRes.IsSuccessStatusCode)
                return new PriceUpdateResult(false, SafeOzonHttpMessage(detailRes.StatusCode, detailRes.Headers.RetryAfter?.ToString()));

            var posting = JsonNode.Parse(detailText)?["result"] ?? JsonNode.Parse(detailText);
            var requirements = posting?["requirements"];
            var requiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "products_requiring_mandatory_mark", "products_requiring_mark" })
                foreach (var id in requirements?[name]?.AsArray() ?? new JsonArray())
                    if (id is not null) requiredIds.Add(id.ToString());

            if (requiredIds.Count == 0)
                return new PriceUpdateResult(true, "Ozon không yêu cầu exemplar KIZ cho posting này.", postingNumber);

            var products = posting?["products"]?.AsArray() ?? new JsonArray();
            var wanted = new List<(string ProductId, string Offer, int Quantity, IReadOnlyList<string> Codes)>();
            foreach (var product in products)
            {
                var productId = product?["product_id"]?.ToString() ?? product?["sku"]?.ToString() ?? "";
                var offer = product?["offer_id"]?.ToString() ?? "";
                if (!requiredIds.Contains(productId) && !requiredIds.Contains(offer)) continue;
                var qty = product?["quantity"]?.GetValue<int?>() ?? 1;
                if (!codesByOffer.TryGetValue(offer, out var codes) || codes.Count < qty)
                    return new PriceUpdateResult(false, $"Ozon cần {qty} KIZ cho {offer}, nhưng kho KIZ chưa đủ.");
                wanted.Add((productId, offer, qty, codes.Take(qty).ToArray()));
            }

            if (wanted.Count == 0)
                return new PriceUpdateResult(false, "Ozon báo cần KIZ nhưng không ánh xạ được product_id/offer_id của posting.");

            using var createRes = await http.SendAsync(Request(HttpMethod.Post,
                "https://api-seller.ozon.ru/v6/fbs/posting/product/exemplar/create-or-get", store,
                JsonSerializer.Serialize(new { posting_number = postingNumber })), ct);
            var createText = await createRes.Content.ReadAsStringAsync(ct);
            if (!createRes.IsSuccessStatusCode)
                return new PriceUpdateResult(false, SafeOzonHttpMessage(createRes.StatusCode, createRes.Headers.RetryAfter?.ToString()));

            var idsByProduct = CollectOzonExemplarIdsByProduct(JsonNode.Parse(createText));
            var validateProducts = new JsonArray();
            var setProducts = new JsonArray();

            foreach (var row in wanted)
            {
                if (!idsByProduct.TryGetValue(row.ProductId, out var exemplarIds) || exemplarIds.Count < row.Quantity)
                    return new PriceUpdateResult(false, $"Ozon trả thiếu exemplar_id cho product {row.ProductId}.");

                var validateExemplars = new JsonArray();
                var setExemplars = new JsonArray();
                for (var i = 0; i < row.Quantity; i++)
                {
                    var safeCode = ScannerSafeKiz(row.Codes[i]);
                    var marks = new JsonArray(new JsonObject
                    {
                        ["mark"] = safeCode,
                        ["mark_type"] = "mandatory_mark"
                    });
                    validateExemplars.Add(new JsonObject { ["marks"] = marks.DeepClone() });
                    setExemplars.Add(new JsonObject
                    {
                        ["exemplar_id"] = long.TryParse(exemplarIds[i], out var numeric) ? numeric : exemplarIds[i],
                        ["marks"] = marks.DeepClone()
                    });
                }
                validateProducts.Add(new JsonObject
                {
                    ["product_id"] = long.TryParse(row.ProductId, out var p1) ? p1 : row.ProductId,
                    ["exemplars"] = validateExemplars
                });
                setProducts.Add(new JsonObject
                {
                    ["product_id"] = long.TryParse(row.ProductId, out var p2) ? p2 : row.ProductId,
                    ["exemplars"] = setExemplars
                });
            }

            var validateBody = new JsonObject
            {
                ["posting_number"] = postingNumber,
                ["products"] = validateProducts
            }.ToJsonString();

            using var validateRes = await http.SendAsync(Request(HttpMethod.Post,
                "https://api-seller.ozon.ru/v5/fbs/posting/product/exemplar/validate", store, validateBody), ct);
            var validateText = await validateRes.Content.ReadAsStringAsync(ct);
            if (!validateRes.IsSuccessStatusCode)
                return new PriceUpdateResult(false, SafeOzonHttpMessage(validateRes.StatusCode, validateRes.Headers.RetryAfter?.ToString()));
            if (OzonHasRejectedExemplar(JsonNode.Parse(validateText)))
                return new PriceUpdateResult(false, "Ozon từ chối ít nhất một KIZ/exemplar. Không chuyển đơn sang giao hàng.");

            var setBody = new JsonObject
            {
                ["posting_number"] = postingNumber,
                ["products"] = setProducts
            }.ToJsonString();
            if(beforeSet is not null&&!beforeSet())
                return new PriceUpdateResult(false,"Một luồng khác đã bắt đầu gửi KIZ/exemplar cho posting này.");
            using var setRes = await http.SendAsync(Request(HttpMethod.Post,
                "https://api-seller.ozon.ru/v6/fbs/posting/product/exemplar/set", store, setBody), ct);
            var setText = await setRes.Content.ReadAsStringAsync(ct);
            if (!setRes.IsSuccessStatusCode)
                return new PriceUpdateResult(false, SafeOzonHttpMessage(setRes.StatusCode, setRes.Headers.RetryAfter?.ToString()));

            for (var attempt = 0; attempt < 12; attempt++)
            {
                using var statusRes = await http.SendAsync(Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v5/fbs/posting/product/exemplar/status", store,
                    JsonSerializer.Serialize(new { posting_number = postingNumber })), ct);
                var statusText = await statusRes.Content.ReadAsStringAsync(ct);
                if (!statusRes.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, SafeOzonHttpMessage(statusRes.StatusCode, statusRes.Headers.RetryAfter?.ToString()));

                var statusNode = JsonNode.Parse(statusText);
                if (OzonHasRejectedExemplar(statusNode))
                    return new PriceUpdateResult(false, "Ozon trả trạng thái KIZ/exemplar bị từ chối.");
                if (OzonExemplarAccepted(statusNode, setProducts))
                    return new PriceUpdateResult(true, "Ozon đã xác thực và nhận KIZ/exemplar.", postingNumber);

                await Task.Delay(1000, ct);
            }

            return new PriceUpdateResult(false,
                "Ozon chưa xác nhận KIZ/exemplar trong thời gian chờ. Ứng dụng không ship để tránh đóng đơn khi mã chưa hợp lệ.",
                postingNumber);
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    public async Task<PriceUpdateResult> PrepareYandexBoxesAsync(
        StoreProfile store,
        FbsOrderRow sample,
        IReadOnlyDictionary<string, IReadOnlyList<string>> codesByOffer,
        CancellationToken ct = default)
    {
        try
        {
            if (store.Marketplace != Marketplace.Yandex)
                return new PriceUpdateResult(false, "Luồng boxes/KIZ này chỉ áp dụng cho Yandex Market.");
            if (string.IsNullOrWhiteSpace(store.CampaignId))
                return new PriceUpdateResult(false, "Thiếu Campaign ID Yandex.");

            var order = JsonNode.Parse(sample.RawJson);
            var items = order?["items"]?.AsArray() ?? new JsonArray();
            if (items.Count == 0)
                return new PriceUpdateResult(false, "Yandex order không có danh sách items để tạo box.");

            var boxItems = new JsonArray();
            var expectedCodes = 0;
            foreach (var item in items)
            {
                if (!long.TryParse(item?["id"]?.ToString(), out var itemId))
                    return new PriceUpdateResult(false, "Yandex item không có id hợp lệ.");
                var offer = item?["offerId"]?.ToString() ?? "";
                var count = item?["count"]?.GetValue<int?>() ?? 1;

                var boxItem = new JsonObject
                {
                    ["id"] = itemId,
                    ["fullCount"] = count
                };

                if (codesByOffer.TryGetValue(offer, out var codes) && codes.Count > 0)
                {
                    if (codes.Count < count)
                        return new PriceUpdateResult(false, $"Yandex cần {count} KIZ cho {offer}, nhưng kho KIZ chưa đủ.");
                    var instances = new JsonArray();
                    for (var i = 0; i < count; i++)
                    {
                        instances.Add(new JsonObject { ["cis"] = ScannerSafeKiz(codes[i]) });
                        expectedCodes++;
                    }
                    boxItem["instances"] = instances;
                }
                boxItems.Add(boxItem);
            }

            var body = new JsonObject
            {
                ["boxes"] = new JsonArray(new JsonObject { ["items"] = boxItems }),
                ["allowRemove"] = false
            }.ToJsonString();

            using var res = await http.SendAsync(Request(HttpMethod.Put,
                $"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{sample.ExternalOrderId}/boxes",
                store, body), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new PriceUpdateResult(false, $"Yandex boxes HTTP {(int)res.StatusCode}: {Short(text)}");

            if (expectedCodes == 0)
                return new PriceUpdateResult(true, "Yandex đã nhận layout hộp.", sample.ExternalOrderId);

            for (var attempt = 0; attempt < 15; attempt++)
            {
                using var check = await http.SendAsync(Request(HttpMethod.Post,
                    $"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{sample.ExternalOrderId}/identifiers/status",
                    store, "{}"), ct);
                var checkText = await check.Content.ReadAsStringAsync(ct);
                if (!check.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, $"Yandex kiểm tra KIZ HTTP {(int)check.StatusCode}: {Short(checkText)}");

                var statuses = CollectStringValues(JsonNode.Parse(checkText), "status");
                if (statuses.Any(x => x.Equals("INVALID", StringComparison.OrdinalIgnoreCase) ||
                                      x.Equals("FAILED", StringComparison.OrdinalIgnoreCase)))
                    return new PriceUpdateResult(false, "Yandex từ chối ít nhất một mã KIZ.");
                var okCount = statuses.Count(x => x.Equals("OK", StringComparison.OrdinalIgnoreCase));
                if (okCount >= expectedCodes)
                    return new PriceUpdateResult(true, $"Yandex đã xác minh {expectedCodes} KIZ.", sample.ExternalOrderId);

                await Task.Delay(1000, ct);
            }

            return new PriceUpdateResult(false,
                "Yandex vẫn đang kiểm tra KIZ. Ứng dụng chưa chuyển READY_TO_SHIP để tránh đơn bị từ chối.",
                sample.ExternalOrderId);
        }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
    }

    private static Dictionary<string, List<string>> CollectOzonExemplarIdsByProduct(JsonNode? node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                var productId = obj["product_id"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(productId) && obj["exemplars"] is JsonArray exemplars)
                {
                    var ids = new List<string>();
                    foreach (var ex in exemplars)
                    {
                        var id = ex?["exemplar_id"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                    }
                    if (ids.Count > 0)
                    {
                        if (!result.TryGetValue(productId, out var existing))
                            result[productId] = existing = new List<string>();
                        existing.AddRange(ids);
                    }
                }
                foreach (var pair in obj) Walk(pair.Value);
            }
            else if (current is JsonArray arr)
                foreach (var child in arr) Walk(child);
        }
        Walk(node);
        return result;
    }

    private static bool OzonHasRejectedExemplar(JsonNode? node)
    {
        var statuses = CollectStringValues(node, "status")
            .Concat(CollectStringValues(node, "check_status"))
            .Concat(CollectStringValues(node, "mark_status"))
            .ToArray();
        if (statuses.Any(x => x.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
                              x.Equals("invalid", StringComparison.OrdinalIgnoreCase) ||
                              x.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                              x.Equals("error", StringComparison.OrdinalIgnoreCase)))
            return true;
        return CollectBooleanValues(node, "valid").Any(x => !x);
    }

    private static bool OzonExemplarAccepted(JsonNode? node, JsonArray expectedProducts)
    {
        var confirmed = new HashSet<(string Product, string Exemplar)>();
        void Walk(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                var productId = obj["product_id"]?.ToString() ?? "";
                if (productId.Length > 0 && obj["exemplars"] is JsonArray exemplars)
                    foreach (var exemplar in exemplars)
                    {
                        var id = exemplar?["exemplar_id"]?.ToString() ?? "";
                        var statuses = CollectStringValues(exemplar, "status")
                            .Concat(CollectStringValues(exemplar, "check_status"))
                            .Concat(CollectStringValues(exemplar, "mark_status")).ToArray();
                        if (id.Length > 0 && statuses.Length > 0 && statuses.All(x =>
                                x.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
                                x.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                                x.Equals("success", StringComparison.OrdinalIgnoreCase) || x.Equals("valid",StringComparison.OrdinalIgnoreCase)))
                            confirmed.Add((productId, id));
                    }
                foreach (var pair in obj) Walk(pair.Value);
            }
            else if (current is JsonArray array)
                foreach (var child in array) Walk(child);
        }
        Walk(node);
        var expected = expectedProducts.SelectMany(product =>
            (product?["exemplars"]?.AsArray() ?? new JsonArray()).Select(exemplar =>
                (Product: product?["product_id"]?.ToString() ?? "", Exemplar: exemplar?["exemplar_id"]?.ToString() ?? ""))).ToArray();
        return expected.Length > 0 && expected.All(confirmed.Contains);
    }

    private static IReadOnlyList<string> CollectStringValues(JsonNode? node, string key)
    {
        var values = new List<string>();
        void Walk(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue)
                        values.Add(pair.Value.ToString());
                    Walk(pair.Value);
                }
            }
            else if (current is JsonArray arr)
                foreach (var child in arr) Walk(child);
        }
        Walk(node);
        return values;
    }

    private static IReadOnlyList<bool> CollectBooleanValues(JsonNode? node, string key)
    {
        var values = new List<bool>();
        void Walk(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(pair.Value?.ToString(), out var value))
                        values.Add(value);
                    Walk(pair.Value);
                }
            }
            else if (current is JsonArray arr)
                foreach (var child in arr) Walk(child);
        }
        Walk(node);
        return values;
    }

    private static string ScannerSafeKiz(string code) =>
        string.IsNullOrEmpty(code) ? "" : code[0] == '\u001d' ? code[1..] : code;


    public async Task<PriceUpdateResult> PackOrderAsync(StoreProfile s, FbsOrderRow order, CancellationToken ct = default)
    {
        try
        {
            if (s.Marketplace == Marketplace.Wildberries)
            {
                return await CreateShipmentAsync(s, new[] { order }, ct);
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
                    return new PriceUpdateResult(false, SafeOzonHttpMessage(ship.StatusCode,ship.Headers.RetryAfter?.ToString()));

                await Task.Delay(1200, ct);
                using var verify = await http.SendAsync(Request(HttpMethod.Post, "https://api-seller.ozon.ru/v3/posting/fbs/get", s,
                    JsonSerializer.Serialize(new { posting_number = order.ExternalOrderId, with = new { analytics_data = false, financial_data = false } })), ct);
                var verifyText = await verify.Content.ReadAsStringAsync(ct);
                if (!verify.IsSuccessStatusCode)
                    return new PriceUpdateResult(false, SafeOzonHttpMessage(verify.StatusCode,verify.Headers.RetryAfter?.ToString()));

                var status = JsonNode.Parse(verifyText)?["result"]?["status"]?.ToString() ?? "";
                var substatus = JsonNode.Parse(verifyText)?["result"]?["substatus"]?.ToString() ?? "";
                if (!new[] { "awaiting_deliver", "delivering", "delivered" }.Contains(status, StringComparer.OrdinalIgnoreCase))
                    return new PriceUpdateResult(false, $"Ozon chưa xác nhận đóng hàng: {status}/{substatus}. Đồng bộ lại trạng thái trước khi gửi lệnh tiếp theo.");

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

    public async Task<PriceUpdateResult> CreateShipmentAsync(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> orders,
        CancellationToken ct = default, string? existingSupplyId = null, string? name = null, IProgress<string>? progress = null)
    {
        try
        {
            if (orders.Count == 0) return new PriceUpdateResult(false, "Chưa chọn đơn hàng.");

            if (store.Marketplace != Marketplace.Wildberries)
            {
                var messages = new List<string>();
                foreach (var group in orders.GroupBy(x => x.ExternalOrderId, StringComparer.OrdinalIgnoreCase))
                {
                    var order = group.First();
                    var r = await PackOrderAsync(store, order, ct);
                    messages.Add($"{order.ExternalOrderId}: {r.Message}");
                    if (!r.Success) return new PriceUpdateResult(false, string.Join(Environment.NewLine, messages));
                }
                return new PriceUpdateResult(true, string.Join(Environment.NewLine, messages));
            }

            return await CreateWbShipmentAsync(store, orders, existingSupplyId, name, ct, progress);
        }
        catch (Exception ex)
        {
            return new PriceUpdateResult(false, ex.Message);
        }
    }

    public async Task<PriceUpdateResult> AttachWbSgtinAsync(
        StoreProfile store,
        string orderId,
        string code,
        CancellationToken ct = default)
    {
        try
        {
            if (store.Marketplace != Marketplace.Wildberries)
                return new PriceUpdateResult(false, "SGTIN chỉ được gắn trực tiếp bằng luồng WB FBS.");
            if (!long.TryParse(orderId, out var parsedId))
                return new PriceUpdateResult(false, "WB order ID không hợp lệ.");
            if (string.IsNullOrWhiteSpace(code))
                return new PriceUpdateResult(false, "Mã KIZ/SGTIN trống.");

            await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                await WbMarketplaceRequestAsync(store,HttpMethod.Put,$"/api/v3/orders/{parsedId}/meta/sgtin",
                    JsonSerializer.Serialize(new {sgtins=new[]{code.Trim()}}),ct,null).ConfigureAwait(false);
            } finally {wbLabelGate.Release();}
            return new PriceUpdateResult(true, "WB đã nhận mã KIZ/SGTIN cho đơn.", orderId);
        }
        catch (Exception ex)
        {
            return new PriceUpdateResult(false, ex.Message);
        }
    }

    public async Task<PriceUpdateResult> DeliverSupplyAsync(StoreProfile store, string supplyId, CancellationToken ct = default)
    {
        try
        {
            if (store.Marketplace != Marketplace.Wildberries)
                return new PriceUpdateResult(false, "Chuyển lô sang giao hàng trực tiếp hiện chỉ áp dụng cho Wildberries.");
            if (string.IsNullOrWhiteSpace(supplyId))
                return new PriceUpdateResult(false, "Không tìm thấy mã supply của lô.");

            using var res = await http.SendAsync(Request(new HttpMethod("PATCH"),
                $"https://marketplace-api.wildberries.ru/api/v3/supplies/{Uri.EscapeDataString(supplyId)}/deliver",
                store, "{}"), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new PriceUpdateResult(false, $"WB deliver HTTP {(int)res.StatusCode}: {Short(text)}");
            return new PriceUpdateResult(true, $"Đã chuyển supply {supplyId} sang giao hàng.", supplyId);
        }
        catch (Exception ex)
        {
            return new PriceUpdateResult(false, ex.Message);
        }
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
        RequireCredentials(s);
        var r = new HttpRequestMessage(method, url);
        if (s.Marketplace == Marketplace.Wildberries)
        {
            var token = (s.Token ?? "").Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
            r.Headers.TryAddWithoutValidation("Authorization", token);
        }
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

    private static string FirstImageUrl(JsonNode? node)
    {
        if (node is null) return "";

        if (node is JsonValue)
        {
            var value = node.ToString().Trim().Trim('"');
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value
                : "";
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var url = FirstImageUrl(item);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
            return "";
        }

        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "url", "file_name", "image_url", "big", "c516x688", "square", "primary_photo", "photo" })
            {
                if (obj[key] is not null)
                {
                    var url = FirstImageUrl(obj[key]);
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }

            foreach (var child in obj)
            {
                var url = FirstImageUrl(child.Value);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }

        return "";
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
        if (!r.IsSuccessStatusCode)
        {
            var host=r.RequestMessage?.RequestUri?.Host??"";
            if(host.Equals("ozon.ru",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".ozon.ru",StringComparison.OrdinalIgnoreCase)
                ||host.Equals("ozone.ru",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".ozone.ru",StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException(SafeOzonHttpMessage(r.StatusCode,r.Headers.RetryAfter?.ToString()));
            throw new HttpRequestException($"HTTP {(int)r.StatusCode}: {Short(body)}");
        }
    }

    private static string Short(string s) => s.Length > 500 ? s[..500] : s;
}
