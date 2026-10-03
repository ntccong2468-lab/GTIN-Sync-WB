using MarketplaceHub.Core;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed partial class MarketplaceGateway
{
    private const int OzonLabelPollLimit = 12;
    private const long OzonDocumentLimit = 100L * 1024 * 1024;

    public async Task<string> CreateOzonLabelTaskAsync(
        StoreProfile store, IEnumerable<string> postingNumbers, CancellationToken ct = default)
    {
        RequireOzon(store);
        var postings = postingNumbers.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (postings.Length is < 1 or > 100 || postings.Any(x => x.Length > 256 || x.Any(char.IsControl)))
            throw new InvalidOperationException("Danh sách posting Ozon không hợp lệ hoặc vượt quá 100 đơn.");

        var body = new JsonObject { ["posting_number"] = new JsonArray(postings.Select(x => (JsonNode?)x).ToArray()) };
        using var request = Request(HttpMethod.Post,
            "https://api-seller.ozon.ru/v2/posting/fbs/package-label/create", store, body.ToJsonString());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var text = await ReadBoundedTextAsync(response, 2 * 1024 * 1024, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(SafeOzonHttpMessage(response.StatusCode, response.Headers.RetryAfter?.ToString()));

        JsonNode root;
        try { root = JsonNode.Parse(text) ?? throw new InvalidDataException(); }
        catch { throw new InvalidOperationException("Ozon trả dữ liệu tạo nhãn không hợp lệ. Không tự tạo lại job nhãn."); }
        var result = root["result"];
        var direct = SafeTaskId(result?["task_id"]?.ToString());
        if (direct.Length > 0) return direct;
        var tasks = result?["tasks"] as JsonArray ?? new JsonArray();
        var sole = tasks.Count == 1 ? SafeTaskId(tasks[0]?["task_id"]?.ToString()) : "";
        foreach (var task in tasks)
            if (string.Equals(task?["task_type"]?.ToString(), "big_label", StringComparison.OrdinalIgnoreCase))
            {
                var id = SafeTaskId(task?["task_id"]?.ToString());
                if (id.Length > 0) return id;
            }
        if (sole.Length > 0) return sole;
        throw new InvalidOperationException("Ozon không trả task_id cho nhãn lớn. Không tự tạo lại job nhãn.");
    }

    public async Task<LabelResult> DownloadOzonLabelTaskAsync(
        StoreProfile store, string postingNumber, string taskId, CancellationToken ct = default)
    {
        try
        {
            RequireOzon(store);
            var safeTask = SafeTaskId(taskId);
            if (safeTask.Length == 0) return new(false, "Task ID nhãn Ozon không hợp lệ.");
            for (var attempt = 0; attempt < OzonLabelPollLimit; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                JsonNode taskValue = long.TryParse(safeTask, out var numeric) && numeric > 0
                    ? JsonValue.Create(numeric)! : JsonValue.Create(safeTask)!;
                using var request = Request(HttpMethod.Post,
                    "https://api-seller.ozon.ru/v1/posting/fbs/package-label/get", store,
                    new JsonObject { ["task_id"] = taskValue }.ToJsonString());
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var text = await ReadBoundedTextAsync(response, 2 * 1024 * 1024, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new(false, SafeOzonHttpMessage(response.StatusCode, response.Headers.RetryAfter?.ToString()));
                JsonNode root;
                try { root = JsonNode.Parse(text) ?? throw new InvalidDataException(); }
                catch { return new(false, "Ozon trả trạng thái job nhãn không hợp lệ."); }
                var result = root["result"];
                var status = result?["status"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
                var error = result?["error"]?.ToString();
                if (!string.IsNullOrWhiteSpace(error) || status is "failed" or "error" or "rejected")
                    return new(false, "Ozon từ chối tạo nhãn. Mở bảng kiểm tra API để xem quyền và trạng thái posting.");
                var fileUrl = result?["file_url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(fileUrl))
                    return await DownloadVerifiedOzonPdfAsync(postingNumber, fileUrl, ct).ConfigureAwait(false);
                if (attempt + 1 < OzonLabelPollLimit)
                    await wbLabelDelay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            return new(false, "Ozon vẫn đang tạo nhãn. Lần thử sau sẽ tiếp tục đúng task_id hiện tại.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public async Task<OzonDiagnosticReport> DiagnoseOzonAsync(
        StoreProfile store, string? postingNumber = null, CancellationToken ct = default)
    {
        var steps = new List<OzonDiagnosticStep>();
        if (store.Marketplace != Marketplace.Ozon || string.IsNullOrWhiteSpace(store.ClientId) || string.IsNullOrWhiteSpace(store.ApiKey))
        {
            steps.Add(new("Thông tin đăng nhập", "local", false, 0, 0, "missing_credentials",
                "Thiếu Ozon Client ID hoặc API Key.", "Nhập đủ thông tin rồi kiểm tra lại; khóa chưa được lưu."));
            return new(steps);
        }

        var seller = await DiagnosticPostAsync(store, "Tài khoản", "/v1/seller/info", new JsonObject(), ct).ConfigureAwait(false);
        steps.Add(seller.Step);
        if (!seller.Step.Success) return new(steps);

        var roles = await DiagnosticPostAsync(store, "Quyền API", "/v1/roles", new JsonObject(), ct).ConfigureAwait(false);
        steps.Add(roles.Step);
        var warehouses = await DiagnosticPostAsync(store, "Kho FBS", "/v2/warehouse/list",
            new JsonObject { ["limit"] = 1 }, ct).ConfigureAwait(false);
        steps.Add(warehouses.Step);

        var now = DateTimeOffset.UtcNow;
        var queueBody = new JsonObject
        {
            ["filter"] = new JsonObject { ["cutoff_from"] = now.AddDays(-180).ToString("O"), ["cutoff_to"] = now.AddDays(180).ToString("O") },
            ["with"] = new JsonObject { ["analytics_data"] = false, ["barcodes"] = false, ["financial_data"] = false, ["legal_info"] = false },
            ["sort_dir"] = "asc", ["translit"] = false, ["cursor"] = "", ["limit"] = 1
        };
        var queue = await DiagnosticPostAsync(store, "Hàng đợi FBS", "/v4/posting/fbs/unfulfilled/list", queueBody, ct).ConfigureAwait(false);
        steps.Add(queue.Step);

        if (!string.IsNullOrWhiteSpace(postingNumber))
        {
            var id = postingNumber.Trim();
            if (id.Length > 256 || id.Any(char.IsControl))
                steps.Add(new("Chi tiết posting", "/v3/posting/fbs/get", false, 0, 0, "invalid_posting",
                    "Posting Number không hợp lệ.", "Sao chép đúng Posting Number từ Seller Ozon."));
            else
            {
                var posting = await DiagnosticPostAsync(store, "Chi tiết posting", "/v3/posting/fbs/get",
                    new JsonObject { ["posting_number"] = id, ["with"] = new JsonObject { ["analytics_data"] = false, ["financial_data"] = false, ["product_exemplars"] = true } }, ct).ConfigureAwait(false);
                steps.Add(posting.Step);
            }
        }
        return new(steps);
    }

    private async Task<(OzonDiagnosticStep Step, JsonNode? Json)> DiagnosticPostAsync(
        StoreProfile store, string stage, string path, JsonObject body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = Request(HttpMethod.Post, "https://api-seller.ozon.ru" + path, store, body.ToJsonString());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var text = await ReadBoundedTextAsync(response, 2 * 1024 * 1024, ct).ConfigureAwait(false);
            watch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var (code, message, action) = ClassifyDiagnostic(response.StatusCode, response.Headers.RetryAfter?.ToString());
                return (new(stage, path, false, (int)response.StatusCode, watch.ElapsedMilliseconds, code, message, action), null);
            }
            JsonNode? json;
            try { json = JsonNode.Parse(text); }
            catch
            {
                return (new(stage, path, false, (int)response.StatusCode, watch.ElapsedMilliseconds, "invalid_json",
                    "Ozon trả nội dung không phải JSON hợp lệ.", "Kiểm tra proxy/mạng rồi thử lại."), null);
            }
            if (json is null)
                return (new(stage, path, false, (int)response.StatusCode, watch.ElapsedMilliseconds, "empty_response",
                    "Ozon trả phản hồi rỗng.", "Thử lại và kiểm tra trạng thái dịch vụ Ozon."), null);
            if (!ValidDiagnosticShape(path, json))
                return (new(stage, path, false, (int)response.StatusCode, watch.ElapsedMilliseconds, "invalid_schema",
                    "Ozon trả JSON nhưng thiếu cấu trúc bắt buộc của endpoint.", "Không coi bước này là thành công; kiểm tra phiên bản API/contract rồi thử lại."), null);
            return (new(stage, path, true, (int)response.StatusCode, watch.ElapsedMilliseconds, "ok",
                "Endpoint phản hồi đúng định dạng.", "Không cần xử lý."), json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            watch.Stop();
            return (new(stage, path, false, 0, watch.ElapsedMilliseconds, "timeout",
                "Hết thời gian chờ Ozon.", "Kiểm tra mạng và chạy lại bước chỉ đọc."), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            watch.Stop();
            return (new(stage, path, false, 0, watch.ElapsedMilliseconds, "network",
                "Không kết nối được tới Ozon.", "Kiểm tra Internet, DNS/TLS và tường lửa."), null);
        }
        catch
        {
            watch.Stop();
            return (new(stage, path, false, 0, watch.ElapsedMilliseconds, "local_error",
                "Không thể hoàn tất bước kiểm tra.", "Đóng bảng và thử lại; báo cáo không chứa bí mật."), null);
        }
    }

    private async Task<LabelResult> DownloadVerifiedOzonPdfAsync(string postingNumber, string rawUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsOzonDocumentHost(uri.Host))
            return new(false, "Ozon trả địa chỉ tài liệu không an toàn; ứng dụng đã chặn tải.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/pdf");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new(false, SafeOzonHttpMessage(response.StatusCode, response.Headers.RetryAfter?.ToString()));
        if (response.Content.Headers.ContentLength is > OzonDocumentLimit)
            return new(false, "Tệp nhãn Ozon vượt giới hạn an toàn 100 MB.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > OzonDocumentLimit) return new(false, "Tệp nhãn Ozon vượt giới hạn an toàn 100 MB.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            return new(false, "Ozon chưa trả nhãn PDF hợp lệ. Task nhãn được giữ để tiếp tục kiểm tra.");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "Labels");
        Directory.CreateDirectory(dir);
        var safeName = string.Concat(postingNumber.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var target = Path.Combine(dir, $"Ozon-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(staging, bytes, ct).ConfigureAwait(false); File.Move(staging, target, true); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new(true, "Đã tải nhãn Ozon chính thức.", target);
    }

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("Phản hồi Ozon vượt giới hạn an toàn.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidDataException("Phản hồi Ozon vượt giới hạn an toàn.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static (string Code, string Message, string Action) ClassifyDiagnostic(HttpStatusCode status, string? retryAfter) => ((int)status) switch
    {
        401 => ("unauthorized", "Ozon không chấp nhận Client ID/API Key.", "Kiểm tra lại cặp khóa của đúng tài khoản seller."),
        403 => ("forbidden", "API key không có quyền cho endpoint này.", "Cấp quyền FBS/warehouse/exemplar/label tương ứng trong Seller Ozon."),
        400 => ("bad_request", "Ozon từ chối cấu trúc yêu cầu.", "Dùng báo cáo này để cập nhật contract endpoint, không gửi lặp yêu cầu cũ."),
        404 => ("not_found", "Endpoint hoặc posting không tồn tại.", "Kiểm tra Posting Number và phiên bản API."),
        429 => ("rate_limited", "Ozon giới hạn tần suất gọi API" + (string.IsNullOrWhiteSpace(retryAfter) ? "." : $"; Retry-After: {retryAfter}."), "Chờ đúng thời gian rồi kiểm tra lại; không quét lại từ đầu."),
        >= 500 => ("ozon_unavailable", "Dịch vụ Ozon đang lỗi tạm thời.", "Thử lại bước chỉ đọc sau; không lặp mutation."),
        _ => ("http_error", $"Ozon trả HTTP {(int)status}.", "Kiểm tra quyền và trạng thái endpoint.")
    };

    private static string SafeOzonHttpMessage(HttpStatusCode status, string? retryAfter)
    {
        var classified = ClassifyDiagnostic(status, retryAfter);
        return classified.Message + " " + classified.Action;
    }
    private static string SafeTaskId(string? value)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= 256 && normalized.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or ':' or '-') ? normalized : "";
    }
    private static bool ValidDiagnosticShape(string path, JsonNode json)
    {
        if (json is not JsonObject root || root.Count == 0) return false;
        return path switch
        {
            "/v1/seller/info" => root["seller_id"] is not null || root["company"] is not null || root["result"] is JsonObject { Count: > 0 },
            "/v1/roles" => root["result"] is JsonArray { Count: > 0 } || root["roles"] is JsonArray { Count: > 0 },
            "/v2/warehouse/list" => root["result"] is JsonArray { Count: > 0 },
            "/v4/posting/fbs/unfulfilled/list" => root["result"] is JsonObject result && result["postings"] is JsonArray
                && (result["cursor"] is not null || result["has_next"] is not null),
            "/v3/posting/fbs/get" => root["result"] is JsonObject posting && posting["posting_number"] is not null
                && posting["products"] is JsonArray && posting["requirements"] is JsonObject,
            _ => false
        };
    }
    private static bool IsOzonDocumentHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return normalized == "ozon.ru" || normalized.EndsWith(".ozon.ru", StringComparison.Ordinal)
            || normalized == "ozone.ru" || normalized.EndsWith(".ozone.ru", StringComparison.Ordinal);
    }
    private static void RequireOzon(StoreProfile store)
    {
        if (store.Marketplace != Marketplace.Ozon) throw new InvalidOperationException("Thao tác này chỉ dành cho Ozon.");
        if (string.IsNullOrWhiteSpace(store.ClientId) || string.IsNullOrWhiteSpace(store.ApiKey))
            throw new InvalidOperationException("Thiếu Ozon Client ID hoặc API Key.");
    }
}
