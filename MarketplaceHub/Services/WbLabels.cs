using MarketplaceHub.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Services;

public sealed partial class MarketplaceGateway
{
    private readonly SemaphoreSlim wbLabelGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, LabelResult Label)> wbLabelCache = new();
    private readonly Func<TimeSpan, CancellationToken, Task> wbLabelDelay;
    private DateTimeOffset wbLabelNotBefore;

    public MarketplaceGateway(Func<TimeSpan, CancellationToken, Task>? labelDelay = null)
        => wbLabelDelay = labelDelay ?? ((delay, ct) => Task.Delay(delay, ct));

    public async Task<IReadOnlyDictionary<string, LabelResult>> DownloadLabelsAsync(
        StoreProfile store, IEnumerable<string> orderIds, CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        var ids = orderIds.Distinct(StringComparer.Ordinal).ToArray();
        var results = new Dictionary<string, LabelResult>(StringComparer.Ordinal);
        if (store.Marketplace != Marketplace.Wildberries)
        {
            foreach (var id in ids) results[id] = await DownloadLabelAsync(store, id, ct).ConfigureAwait(false);
            return results;
        }
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var valid = new List<string>();
            foreach (var id in ids)
                if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
                    valid.Add(id);
                else results[id] = new(false, "ID đơn WB phải là số nguyên dương.");
            var credential = store.Token.Length > 0 ? store.Token : store.ApiKey;
            var shopKey = store.Id + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];
            foreach (var batch in valid.Chunk(100))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report($"Kiểm tra trạng thái WB: {results.Count}/{ids.Length}");
                    var statuses = await WbLabelRequestAsync(store, "/api/v3/orders/status", batch, ct, progress).ConfigureAwait(false);
                    var statusRows = JsonNode.Parse(statuses)?["orders"] as JsonArray
                        ?? throw new InvalidDataException("WB không trả danh sách trạng thái đơn hợp lệ.");
                    var eligible = new List<string>();
                    foreach (var id in batch)
                    {
                        var matching = statusRows.Where(x => x?["id"]?.ToString() == id).ToArray();
                        var supplier = matching.Length == 1 ? matching[0]?["supplierStatus"]?.ToString() : null;
                        var wb = matching.Length == 1 ? matching[0]?["wbStatus"]?.ToString() : null;
                        if (supplier is not ("confirm" or "complete") || wb is null || wb.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            results[id] = new(false, supplier == "new"
                                ? "Đơn đang ở trạng thái new. Hãy đóng đơn/thêm vào supply trước; WB chỉ cấp sticker ở confirm hoặc complete."
                                : $"Chưa được in: trạng thái WB {supplier ?? "không xác định"}/{wb ?? "không xác định"}. Cần confirm hoặc complete và đơn còn hiệu lực.");
                            continue;
                        }
                        var key = shopKey + "-" + id;
                        if (wbLabelCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10)
                            && (cached.Label.FilePath is null || File.Exists(cached.Label.FilePath))) results[id] = cached.Label;
                        else eligible.Add(id);
                    }
                    if (eligible.Count == 0) continue;
                    progress?.Report($"Lấy sticker WB theo lô {eligible.Count} đơn…");
                    var text = await WbLabelRequestAsync(store,
                        "/api/v3/orders/stickers?type=png&width=58&height=40", eligible.ToArray(), ct, progress).ConfigureAwait(false);
                    var stickers = JsonNode.Parse(text)?["stickers"] as JsonArray
                        ?? throw new InvalidDataException("WB trả dữ liệu sticker không hợp lệ.");
                    foreach (var id in eligible)
                    {
                        var matches = stickers.Where(x => x?["orderId"]?.ToString() == id).ToArray();
                        if (matches.Length != 1)
                        {
                            results[id] = new(false, matches.Length > 1 ? "WB trả trùng orderId; dừng để tránh in nhầm nhãn."
                                : "WB chưa cấp sticker cho đơn đã đóng. Kiểm tra metadata bắt buộc (nhất là số tờ khai customsDeclaration) trên Seller WB, rồi tải lại. Không tạo nhãn từ ID đơn.");
                            continue;
                        }
                        var sticker = matches[0]!;
                        var barcode = sticker["barcode"]?.ToString();
                        var partA = sticker["partA"]?.ToString();
                        var partB = sticker["partB"]?.ToString();
                        var encoded = sticker["file"]?.ToString();
                        LabelResult label;
                        if (string.IsNullOrWhiteSpace(encoded))
                            label = !string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(partA) && !string.IsNullOrWhiteSpace(partB)
                                ? new(true, "WB trả mã sticker chính thức; dựng nhãn QR theo quy trình wcode.", null, barcode, partA, partB)
                                : new(false, "WB trả sticker thiếu file và mã barcode/partA/partB. Chưa thể tạo nhãn hợp lệ.");
                        else
                        {
                            try
                            {
                                var bytes = Convert.FromBase64String(encoded);
                                if (bytes.Length <= 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
                                    label = new(false, "WB trả file không phải PNG hợp lệ; dừng để tránh in sai nhãn.");
                                else
                                {
                                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "Labels", shopKey);
                                    Directory.CreateDirectory(dir);
                                    var path = Path.Combine(dir, id + "-" + Guid.NewGuid().ToString("N") + ".png");
                                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                                    label = new(true, "Đã tải sticker WB.", path, barcode, partA, partB);
                                }
                            }
                            catch (FormatException) { label = new(false, "WB trả file sticker base64 không hợp lệ."); }
                        }
                        results[id] = label;
                        if (label.Success) wbLabelCache[shopKey + "-" + id] = (DateTimeOffset.UtcNow, label);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Stop the request stream on API/transport errors; never flood the remaining batches.
                    foreach (var id in valid.Where(id => !results.ContainsKey(id))) results[id] = new(false, ex.Message);
                    break;
                }
            }
            foreach (var key in wbLabelCache.Where(x => DateTimeOffset.UtcNow - x.Value.At > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToArray())
                wbLabelCache.Remove(key);
            return results;
        }
        finally { wbLabelGate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string,WbPrintKizMetadata>> GetWbPrintKizAsync(
        StoreProfile store,IEnumerable<string> orderIds,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        var ids=orderIds.Distinct().ToArray();
        if(store.Marketplace!=Marketplace.Wildberries || ids.Any(id=>!long.TryParse(id,out var value) || value<=0))
            throw new InvalidOperationException("ID đơn WB không hợp lệ khi kiểm tra KIZ.");
        var result=new Dictionary<string,WbPrintKizMetadata>();
        await wbLabelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach(var batch in ids.Chunk(100))
            {
                progress?.Report($"Kiểm tra KIZ đã gắn trên WB: {result.Count}/{ids.Length}…");
                var body=await WbLabelRequestAsync(store,"/api/marketplace/v3/orders/meta",batch,ct,progress).ConfigureAwait(false);
                var rows=JsonNode.Parse(body)?["orders"] as JsonArray
                    ?? throw new InvalidDataException("WB không trả metadata đơn hợp lệ; chưa thể xác nhận KIZ để in.");
                foreach(var id in batch)
                {
                    var matches=rows.Where(x=>x?["id"]?.ToString()==id).ToArray();
                    if(matches.Length!=1)throw new InvalidDataException($"{id}: WB thiếu hoặc trùng metadata. Dừng để tránh in sai KIZ.");
                    var row=matches[0]!;
                    var values=new List<string>();
                    var required=false;
                    if(row["metaDetails"] is JsonArray details)
                    {
                        foreach(var item in details.Where(x=>x?["key"]?.ToString()=="sgtin"))
                        {
                            var decision=item?["decision"]?.ToString()??"";
                            if(decision=="required")required=true;
                            else if(decision.Length>0 && decision!="optional" && decision!="filled" && !decision.EndsWith("MaySell",StringComparison.Ordinal))
                                throw new InvalidDataException($"{id}: WB chưa chấp nhận KIZ ({decision}). Kiểm tra metadata trên Seller WB trước khi in.");
                            if(item?["value"] is JsonArray array)values.AddRange(array.Select(x=>x?.GetValue<string>()??""));
                            else if(item?["value"] is JsonValue value && value.TryGetValue<string>(out var code) && !string.IsNullOrWhiteSpace(code))values.Add(code);
                        }
                    }
                    else if(row["meta"]?["sgtin"]?["value"] is JsonArray legacy)
                        values.AddRange(legacy.Select(x=>x?.GetValue<string>()??""));
                    if(values.Any(string.IsNullOrWhiteSpace) || values.Distinct().Count()!=values.Count)
                        throw new InvalidDataException($"{id}: WB trả KIZ rỗng hoặc trùng; chưa thể in.");
                    result[id]=new(required || values.Count>0,values);
                }
            }
            return result;
        }
        finally {wbLabelGate.Release();}
    }

    private async Task<string> WbLabelRequestAsync(StoreProfile store, string endpoint, string[] ids,
        CancellationToken ct, IProgress<string>? progress)
        => await WbMarketplaceRequestAsync(store, HttpMethod.Post, endpoint,
            JsonSerializer.Serialize(new { orders = ids.Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray() }), ct, progress).ConfigureAwait(false);

    private async Task<string> WbMarketplaceRequestAsync(StoreProfile store, HttpMethod method, string endpoint, string? json,
        CancellationToken ct, IProgress<string>? progress)
    {
        for (var attempt = 0; ; attempt++)
        {
            var wait = wbLabelNotBefore - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                if (wait > TimeSpan.FromMinutes(5)) throw new GtinQuotaException(endpoint,wait);
                await wbLabelDelay(wait, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            using var request = Request(method, "https://marketplace-api.wildberries.ru" + endpoint, store, json);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            wbLabelNotBefore = DateTimeOffset.UtcNow.AddMilliseconds(250);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode != 429)
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"WB HTTP {(int)response.StatusCode}. Kiểm tra quyền Marketplace của token và trạng thái đơn. {Short(body)}");
                return body;
            }
            var seconds = 1d;
            if (response.Headers.TryGetValues("X-Ratelimit-Retry", out var values)
                && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed) && parsed >= 0) seconds = Math.Max(seconds, parsed);
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta) seconds = Math.Max(seconds, delta.TotalSeconds);
            if (response.Headers.RetryAfter?.Date is DateTimeOffset at) seconds = Math.Max(seconds, (at - DateTimeOffset.UtcNow).TotalSeconds);
            wbLabelNotBefore = DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 86400));
            if (attempt >= 3 || seconds > 300) throw new GtinQuotaException(endpoint,wbLabelNotBefore-DateTimeOffset.UtcNow);
            progress?.Report($"WB giới hạn API: chờ {Math.Ceiling(seconds)} giây rồi thử lại ({attempt + 1}/3)…");
        }
    }
}
