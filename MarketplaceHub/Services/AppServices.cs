using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    public AppDatabase Db { get; }
    public MarketplaceGateway Api { get; }
    public LicenseAccessService License { get; }
    private readonly HttpClient znakHttp = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly ConcurrentDictionary<long, SemaphoreSlim> syncLocks = new();

    public AppServices() : this(new AppDatabase(), new MarketplaceGateway(), LicenseAccessService.CreateDefault()) { }

    public AppServices(AppDatabase db, MarketplaceGateway api, LicenseAccessService license)
    {
        Db = db;
        Api = api;
        License = license;
    }

    public Task<(bool Ok, string Message)> SyncProductsAsync(StoreProfile store, CancellationToken ct = default) =>
        RunStoreSyncAsync(store, () => SyncProductsCoreAsync(store, ct), ct);

    private async Task<(bool Ok, string Message)> SyncProductsCoreAsync(StoreProfile store, CancellationToken ct)
    {
        return await SyncProductsResumableCoreAsync(store,ct);
    }

    public Task<(bool Ok, string Message)> SyncOrdersAsync(StoreProfile store, CancellationToken ct = default) =>
        RunStoreSyncAsync(store, () => SyncOrdersCoreAsync(store, ct), ct);

    private async Task<(bool Ok, string Message)> SyncOrdersCoreAsync(StoreProfile store, CancellationToken ct)
    {
        var run = Db.StartSyncRun(store.Id, "fbs_orders");
        var cached=Db.Orders(store.Id).Where(x=>x.Marketplace==store.Marketplace).ToArray();
        var oldStates=Db.OrderRemoteStates(store.Id);
        var members=store.Marketplace==Marketplace.Wildberries?Db.WbReceivedOrderIds(store.Id):Db.MarketplaceReceivedOrderIds(store);
        var candidates=new OrderTruthService().Build(store,cached,members,oldStates).Orders
            .Where(x=>x.State is not (OrderTruthState.Completed or OrderTruthState.Cancelled)).Select(x=>x.ExternalOrderId).ToArray();
        var started=DateTimeOffset.UtcNow;
        // Invalidate active cache before network I/O, including a failed queue refresh.
        Db.UpsertOrderRemoteStates(store.Id,store.Marketplace,candidates.ToDictionary(id=>id,id=>
            oldStates.TryGetValue(id,out var state)?state with{Complete=false}:new OrderRemoteState("","",false,started),StringComparer.Ordinal));
        try
        {
            var rows=await Api.SyncFbsAsync(store,ct);
            var written=await Task.Run(()=>Db.UpsertOrders(store.Id,store.Marketplace,rows),ct);
            var returned=rows.GroupBy(x=>x.ExternalOrderId,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.First(),StringComparer.Ordinal);
            if(store.Marketplace==Marketplace.Wildberries)
            {
                var ids=returned.Keys.Concat(candidates).Distinct(StringComparer.Ordinal).ToArray();
                Db.UpsertOrderRemoteStates(store.Id,store.Marketplace,ids.ToDictionary(id=>id,id=>new OrderRemoteState("","",false,started),StringComparer.Ordinal));
                var statuses=await Api.GetWbOrderStatusesAsync(store,ids,ct).ConfigureAwait(false);
                var observed=DateTimeOffset.UtcNow;
                Db.UpsertOrderRemoteStates(store.Id,store.Marketplace,statuses.ToDictionary(x=>x.Key,
                    x=>new OrderRemoteState(x.Value.SupplierStatus,x.Value.WbStatus,true,observed),StringComparer.Ordinal));
            }
            else
            {
                var observed=DateTimeOffset.UtcNow;
                Db.UpsertOrderRemoteStates(store.Id,store.Marketplace,returned.ToDictionary(x=>x.Key,x=>{
                    var raw=JsonNode.Parse(x.Value.RawJson);var cancelled=raw?["cancelRequested"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true;
                    return new OrderRemoteState(x.Value.Status,cancelled?"cancel_requested":"",!string.IsNullOrWhiteSpace(x.Value.Status),observed);
                },StringComparer.Ordinal));
                foreach(var id in candidates.Where(id=>!returned.ContainsKey(id)))
                {
                    ct.ThrowIfCancellationRequested();
                    var snapshot=await Api.ReadMarketplaceFbsAsync(store,id,ct).ConfigureAwait(false);
                    Db.UpsertOrders(store.Id,store.Marketplace,snapshot.Rows);
                    Db.MarkOrderRemoteState(store.Id,store.Marketplace,id,snapshot.Rows[0].Status,
                        snapshot.CancelRequested?"cancel_requested":"",true,DateTimeOffset.UtcNow);
                }
            }
            Db.SaveSyncState(store.Id,"fbs_orders","",DateTimeOffset.UtcNow.AddDays(-30).ToString("O"),DateTimeOffset.UtcNow.ToString("O"),"");
            Db.FinishSyncRun(run,true,rows.Count,written);
            return (true,$"Đã đồng bộ {written} dòng đơn FBS và đối soát các đơn đang xử lý trong cache.");
        }
        catch(Exception ex)
        {
            var message="Chưa đồng bộ đủ trạng thái đơn hiện tại. Các đơn chưa đối soát được đánh dấu dữ liệu một phần. "+ex.Message;
            Db.SaveSyncState(store.Id,"fbs_orders","","","",message);Db.FinishSyncRun(run,false,0,0,message);
            return (false,message);
        }
    }

    public Task<(bool Ok, string Message)> SyncFboSuppliesAsync(StoreProfile store, CancellationToken ct = default) =>
        RunStoreSyncAsync(store, () => SyncFboSuppliesCoreAsync(store, ct), ct);

    private async Task<(bool Ok, string Message)> SyncFboSuppliesCoreAsync(StoreProfile store, CancellationToken ct)
    {
        var run = Db.StartSyncRun(store.Id, "fbo_supplies");
        try
        {
            var rows = await Api.SyncFboSuppliesAsync(store, ct);
            await Task.Run(() => Db.ReplaceFboSupplies(store.Id, store.Marketplace, rows), ct);
            Db.SaveSyncState(store.Id, "fbo_supplies", "", "", "", "");
            Db.FinishSyncRun(run, true, rows.Count, rows.Count);
            return (true, $"Đã đồng bộ {rows.Count} yêu cầu nhập kho FBO/FBW.");
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "fbo_supplies", "", "", "", ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message);
        }
    }


    private async Task<(bool Ok, string Message)> RunStoreSyncAsync(
        StoreProfile store,
        Func<Task<(bool Ok, string Message)>> operation,
        CancellationToken ct)
    {
        var gate = syncLocks.GetOrAdd(store.Id, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            entered = await gate.WaitAsync(0, ct);
            if (!entered) return (false, $"Cửa hàng {store.Name} đang có một luồng đồng bộ khác chạy.");
            return await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "Đã hủy đồng bộ.");
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    public Task<(bool Ok, string Message)> SyncStoreAsync(
        StoreProfile store,
        CancellationToken ct = default) => RunStoreSyncAsync(store, async () =>
        {
            var messages = new List<string>();
            ct.ThrowIfCancellationRequested();
            var products = await SyncProductsCoreAsync(store, ct);
            messages.Add("Sản phẩm: " + products.Message);

            ct.ThrowIfCancellationRequested();
            var orders = await SyncOrdersCoreAsync(store, ct);
            messages.Add("FBS: " + orders.Message);

            var ok = products.Ok && orders.Ok;
            if (store.Marketplace is Marketplace.Wildberries or Marketplace.Ozon)
            {
                ct.ThrowIfCancellationRequested();
                var fbo = await SyncFboSuppliesCoreAsync(store, ct);
                messages.Add("FBO/FBW: " + fbo.Message);
                ok &= fbo.Ok;
            }

            Db.Audit("Đồng bộ", ok ? "Hoàn tất" : "Có lỗi", $"{store.Marketplace}:{store.Name}");
            return (ok, string.Join(Environment.NewLine, messages));
        }, ct);



    public async Task<(bool Ok, string Message, FinanceSnapshot? Snapshot)> ReadFinanceAsync(
        StoreProfile store, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var run = Db.StartSyncRun(store.Id, "finance");
        try
        {
            var snapshot = await Api.ReadFinanceAsync(store, from, to, ct);
            Db.SaveSyncState(store.Id, "finance", "", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), "");
            Db.FinishSyncRun(run, true, snapshot.ReportCount, snapshot.ReportCount);
            return (true, $"Đã đọc {snapshot.ReportCount} báo cáo quyết toán.", snapshot);
        }
        catch (Exception ex)
        {
            Db.SaveSyncState(store.Id, "finance", "", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), ex.Message);
            Db.FinishSyncRun(run, false, 0, 0, ex.Message);
            return (false, ex.Message, null);
        }
    }

    public async Task<PriceUpdateResult> ChangePriceAsync(StoreProfile store, ProductRow product, decimal price, CancellationToken ct = default)
    {
        var result = await Api.UpdatePriceAsync(store, product, price, ct);
        Db.AddPriceHistory(store.Id, product.Sku, product.Price, price, result.Message);
        Db.Audit("Price", result.Success ? "Accepted" : "Failed", $"{store.Marketplace}:{product.Sku}:{price}:{result.Message}");
        return result;
    }

    public static string NormalizeGtin14(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 13) return "0" + digits;
        return digits.Length == 14 ? digits : "";
    }

    public async Task<(bool Ok, string Message, IReadOnlyList<string> Codes)> EnsureKizQuantityAsync(
        long storeId,
        string sku,
        string gtin,
        int quantity,
        CancellationToken ct = default)
    {
        gtin = NormalizeGtin14(gtin);
        if (string.IsNullOrWhiteSpace(gtin))
            return (false, "Barcode/GTIN của sản phẩm không thể chuẩn hóa thành GTIN-14.", Array.Empty<string>());

        quantity = Math.Max(1, quantity);
        var available = Db.Kiz()
            .Where(x => x.Gtin == gtin && x.Status == "AVAILABLE")
            .Select(x => x.Code)
            .Take(quantity)
            .ToList();
        if (available.Count >= quantity)
            return (true, $"Kho KIZ đã có {available.Count} mã sẵn sàng.", available);

        var missing = quantity - available.Count;
        var config = Db.GetZnakConfig();
        if (!config.Enabled)
            return (false, $"Thiếu {missing} KIZ và chức năng Znack chưa được bật.", available);
        if (string.IsNullOrWhiteSpace(config.OmsId) || string.IsNullOrWhiteSpace(config.OmsConnection))
            return (false, $"Thiếu {missing} KIZ. Hãy cấu hình omsId và omsConnection.", available);
        if (string.IsNullOrWhiteSpace(config.CertificateThumbprint))
            return (false, $"Thiếu {missing} KIZ. Hãy chọn chứng thư số CryptoPro có private key.", available);

        var persisted = Db.ZnakPipelines(storeId)
            .FirstOrDefault(x => x.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase)
                              && x.Gtin.Equals(gtin, StringComparison.OrdinalIgnoreCase));
        if (persisted is not null &&
            (persisted.Stage == "BUYING" || persisted.Stage == "CREATE_AMBIGUOUS") &&
            string.IsNullOrWhiteSpace(persisted.ExternalOrderId))
        {
            return (false,
                "Yêu cầu mua KIZ trước có kết quả không chắc chắn. Ứng dụng chặn tự động mua lại để tránh trừ tiền hai lần. Hãy đối soát SUZ trước.",
                available);
        }

        try
        {
            var token = await GetSuzTokenAsync(config, ct);
            var orderId = persisted is not null && persisted.Stage == "POLLING" &&
                          !string.IsNullOrWhiteSpace(persisted.ExternalOrderId)
                ? persisted.ExternalOrderId
                : "";

            if (string.IsNullOrWhiteSpace(orderId))
            {
                Db.UpsertZnakPipeline(storeId, sku, gtin, "BUYING", "", $"Tự động mua {missing} KIZ");
                try
                {
                    orderId = await CreateSuzOrderAsync(config, token, gtin, missing, ct);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException ||
                    ex is TaskCanceledException ||
                    ex is TimeoutException ||
                    ex.Message.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase))
                {
                    Db.UpsertZnakPipeline(storeId, sku, gtin, "CREATE_AMBIGUOUS", "", ex.Message);
                    Db.Audit("Znack", "Mua KIZ chưa xác định", $"{gtin}:{ex.Message}");
                    return (false,
                        "Kết quả tạo order SUZ chưa xác định do lỗi mạng/server. Ứng dụng không tự tạo lại để tránh mua trùng. Hãy đối soát SUZ.",
                        available);
                }
                Db.UpsertZnakPipeline(storeId, sku, gtin, "POLLING", orderId, "Đang chờ SUZ cấp mã");
            }

            var ready = await WaitSuzCodesReadyAsync(config, token, orderId, ct);
            if (!ready.Ok)
            {
                Db.UpsertZnakPipeline(storeId, sku, gtin, "ERROR", orderId, ready.Message);
                return (false, ready.Message, available);
            }

            var downloaded = await DownloadSuzCodesAsync(config, token, orderId, gtin, missing, ct);
            if (downloaded.Count == 0)
            {
                const string message = "SUZ báo sẵn sàng nhưng không trả mã KIZ.";
                Db.UpsertZnakPipeline(storeId, sku, gtin, "ERROR", orderId, message);
                return (false, message, available);
            }

            foreach (var code in downloaded)
                Db.UpsertKiz(code, gtin, "AVAILABLE");

            available.AddRange(downloaded);
            Db.UpsertZnakPipeline(storeId, sku, gtin, "CODES_DOWNLOADED", orderId, $"Đã tải {downloaded.Count} mã KIZ");
            Db.Audit("Znack", "Tự động mua KIZ", $"{gtin}:{downloaded.Count}:{orderId}");
            return (available.Count >= quantity,
                available.Count >= quantity
                    ? $"Đã tự động mua và tải {downloaded.Count} KIZ."
                    : $"Đã tải {downloaded.Count} KIZ nhưng vẫn chưa đủ số lượng cần dùng.",
                available.Take(quantity).ToList());
        }
        catch (Exception ex)
        {
            Db.UpsertZnakPipeline(storeId, sku, gtin, "ERROR", "", ex.Message);
            Db.Audit("Znack", "Lỗi mua KIZ", $"{gtin}:{ex.Message}");
            return (false, ex.Message, available);
        }
    }

    private async Task<string> GetSuzTokenAsync(ZnakConfig config, CancellationToken ct)
    {
        const string baseUrl = "https://markirovka.crpt.ru/api/v3/true-api";
        using var challengeRes = await znakHttp.GetAsync(baseUrl + "/auth/key", ct);
        var challengeText = await challengeRes.Content.ReadAsStringAsync(ct);
        if (!challengeRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"Znack auth/key HTTP {(int)challengeRes.StatusCode}: {TrimDiagnostic(challengeText)}");

        var challenge = JsonNode.Parse(challengeText)?.AsObject()
                        ?? throw new InvalidOperationException("Znack auth/key trả dữ liệu không hợp lệ.");
        var uuid = challenge["uuid"]?.ToString() ?? "";
        var data = challenge["data"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(data))
            throw new InvalidOperationException("Znack auth/key thiếu uuid/data.");

        var cms = await SignCryptoProAsync(Encoding.UTF8.GetBytes(data), config.CertificateThumbprint, detached: false, ct);
        var body = new JsonObject
        {
            ["uuid"] = uuid,
            ["data"] = Convert.ToBase64String(cms)
        };
        var inn = string.IsNullOrWhiteSpace(config.Inn)
            ? Certificates().FirstOrDefault(x => x.Thumbprint.Equals(config.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))?.Inn ?? ""
            : config.Inn;
        if (!string.IsNullOrWhiteSpace(inn)) body["inn"] = inn;

        var signInUrl = baseUrl + "/auth/simpleSignIn/" + Uri.EscapeDataString(config.OmsConnection.Trim());
        using var signIn = new HttpRequestMessage(HttpMethod.Post, signInUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        signIn.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await znakHttp.SendAsync(signIn, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Znack signIn HTTP {(int)res.StatusCode}: {TrimDiagnostic(text)}");

        var root = JsonNode.Parse(text);
        var token = root?["clientToken"]?.ToString()
                    ?? root?["token"]?.ToString()
                    ?? root?["sessionToken"]?.ToString()
                    ?? root?["jwt"]?.ToString()
                    ?? "";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Znack signIn không trả clientToken.");
        return token;
    }

    private async Task<string> CreateSuzOrderAsync(
        ZnakConfig config,
        string token,
        string gtin,
        int quantity,
        CancellationToken ct)
    {
        const string suzBase = "https://suzgrid.crpt.ru";
        var order = new JsonObject
        {
            ["productGroup"] = "lp",
            ["attributes"] = new JsonObject { ["releaseMethodType"] = "PRODUCTION" },
            ["products"] = new JsonArray(new JsonObject
            {
                ["gtin"] = gtin,
                ["quantity"] = quantity,
                ["serialNumberType"] = "OPERATOR",
                ["templateId"] = 10,
                ["cisType"] = "UNIT"
            })
        };
        var payload = Encoding.UTF8.GetBytes(order.ToJsonString());
        var signature = Convert.ToBase64String(
            await SignCryptoProAsync(payload, config.CertificateThumbprint, detached: true, ct));

        var url = $"{suzBase}/api/v3/order?omsId={Uri.EscapeDataString(config.OmsId.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload)
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("clientToken", token);
        req.Headers.TryAddWithoutValidation("X-Signature", signature);

        using var res = await znakHttp.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"SUZ tạo order HTTP {(int)res.StatusCode}: {TrimDiagnostic(text)}");

        var root = JsonNode.Parse(text);
        var orderId = root?["orderId"]?.ToString() ?? root?["id"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(orderId))
            throw new InvalidOperationException("SUZ không trả orderId.");
        return orderId;
    }

    private async Task<(bool Ok, string Message)> WaitSuzCodesReadyAsync(
        ZnakConfig config,
        string token,
        string orderId,
        CancellationToken ct)
    {
        const string suzBase = "https://suzgrid.crpt.ru";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var url = $"{suzBase}/api/v3/order/status?omsId={Uri.EscapeDataString(config.OmsId.Trim())}&orderId={Uri.EscapeDataString(orderId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("clientToken", token);
            using var res = await znakHttp.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (!res.IsSuccessStatusCode)
                return (false, $"SUZ status HTTP {(int)res.StatusCode}: {TrimDiagnostic(text)}");

            var node = JsonNode.Parse(text);
            var entries = node as JsonArray ?? new JsonArray(node);
            foreach (var entry in entries)
            {
                var state = entry?["bufferStatus"]?.ToString()
                            ?? entry?["status"]?.ToString()
                            ?? "";
                if (state.Equals("REJECTED", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("DECLINED", StringComparison.OrdinalIgnoreCase))
                {
                    var reason = entry?["rejectionReason"]?.ToString() ?? "SUZ từ chối yêu cầu.";
                    return (false, reason);
                }
                var available = int.TryParse(entry?["availableCodes"]?.ToString(), out var n) ? n : 0;
                if ((state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                     state.Equals("READY", StringComparison.OrdinalIgnoreCase)) && available > 0)
                    return (true, "KIZ đã sẵn sàng.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return (false, "Hết thời gian chờ SUZ cấp KIZ. Yêu cầu đã được lưu để kiểm tra lại.");
    }

    private async Task<IReadOnlyList<string>> DownloadSuzCodesAsync(
        ZnakConfig config,
        string token,
        string orderId,
        string gtin,
        int quantity,
        CancellationToken ct)
    {
        const string suzBase = "https://suzgrid.crpt.ru";
        var url = $"{suzBase}/api/v3/codes?omsId={Uri.EscapeDataString(config.OmsId.Trim())}&orderId={Uri.EscapeDataString(orderId)}&quantity={quantity}&gtin={Uri.EscapeDataString(gtin)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("clientToken", token);
        using var res = await znakHttp.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"SUZ codes HTTP {(int)res.StatusCode}: {TrimDiagnostic(text)}");

        var root = JsonNode.Parse(text);
        JsonArray values = root as JsonArray
                           ?? root?["codes"]?.AsArray()
                           ?? new JsonArray();
        var result = new List<string>();
        foreach (var item in values)
        {
            var code = item is JsonValue ? item.ToString() : item?["cis"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(code)) result.Add(code);
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static async Task<byte[]> SignCryptoProAsync(
        byte[] payload,
        string thumbprint,
        bool detached,
        CancellationToken ct)
    {
        var exe = FindCryptoProExecutable();
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("Không tìm thấy cryptcp.exe. Hãy cài CryptoPro CSP/CryptoPro Tools trên máy seller.");

        var dir = Path.Combine(Path.GetTempPath(), "MarketplaceHub-Znack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "payload.bin");
        var output = Path.Combine(dir, "signature.p7s");
        await File.WriteAllBytesAsync(input, payload, ct);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-sign");
            psi.ArgumentList.Add("-uMy");
            psi.ArgumentList.Add("-thumbprint");
            psi.ArgumentList.Add(thumbprint.Replace(" ", ""));
            psi.ArgumentList.Add("-der");
            psi.ArgumentList.Add(detached ? "-detached" : "-attached");
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add(output);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Không khởi động được cryptcp.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw new TimeoutException("CryptoPro ký dữ liệu quá 60 giây.");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"CryptoPro ký thất bại ({process.ExitCode}): {TrimDiagnostic(stderr + " " + stdout)}");
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("CryptoPro không tạo file chữ ký.");

            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static string? FindCryptoProExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Crypto Pro", "CSP", "cryptcp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Crypto Pro", "CSP", "cryptcp.exe")
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim().Trim('"'), "cryptcp.exe");
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }

    private static string TrimDiagnostic(string value)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 500 ? value[..500] : value;
    }

    public static (bool Ok, string Gtin, string Message) ParseKiz(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, "", "Mã KIZ/DataMatrix trống.");
        var s = code.Trim();
        var index = s.IndexOf("01", StringComparison.Ordinal);
        if (index < 0 || s.Length < index + 16) return (false, "", "Không tìm thấy AI(01) + GTIN-14.");
        var gtin = s.Substring(index + 2, 14);
        if (!gtin.All(char.IsDigit)) return (false, "", "GTIN trong DataMatrix không hợp lệ.");
        return (true, gtin, "Cấu trúc mã hợp lệ. Trạng thái pháp lý cần kiểm tra qua True API.");
    }

    public static IReadOnlyList<CertificateInfo> Certificates()
    {
        var result = new List<CertificateInfo>();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var cert in store.Certificates)
        {
            var subject = cert.Subject ?? "";
            var owner = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(owner)) owner = SubjectValue(subject, "CN");
            if (string.IsNullOrWhiteSpace(owner)) owner = subject;
            result.Add(new CertificateInfo(
                subject,
                cert.Thumbprint ?? "",
                cert.NotAfter,
                cert.HasPrivateKey,
                owner,
                ExtractInn(subject)));
        }
        return result.OrderByDescending(x => x.NotAfter).ToList();
    }

    internal static string ExtractInn(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        var labels = new[]
        {
            "INN FL", "ИНН ФЛ", "ИНН ЮЛ", "INN", "ИНН",
            "OID.1.2.643.3.131.1.1", "1.2.643.3.131.1.1"
        };

        foreach (var label in labels)
        {
            var pattern = $@"(?:^|[,\r\n])\s*{Regex.Escape(label)}\s*=\s*(?<v>[^,\r\n]+)";
            var m = Regex.Match(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) continue;
            var value = DecodeDnValue(m.Groups["v"].Value.Trim());
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length is 10 or 12) return digits;
        }

        var fallback = Regex.Match(subject, @"(?<!\d)(\d{10}|\d{12})(?!\d)");
        return fallback.Success ? fallback.Groups[1].Value : "";
    }

    private static string SubjectValue(string subject, string label)
    {
        var pattern = $@"(?:^|[,\r\n])\s*{Regex.Escape(label)}\s*=\s*(?<v>[^,\r\n]+)";
        var m = Regex.Match(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? DecodeDnValue(m.Groups["v"].Value.Trim()) : "";
    }

    private static string DecodeDnValue(string value)
    {
        value = value.Trim().Trim('"');
        var hex = value.StartsWith("#", StringComparison.Ordinal) ? value[1..] : value;
        if (hex.Length >= 4 && hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
        {
            try
            {
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length > 2 && bytes[0] is 0x0C or 0x13 or 0x16)
                {
                    var length = bytes[1];
                    if (length > 0 && length <= bytes.Length - 2)
                        bytes = bytes.Skip(2).Take(length).ToArray();
                }
                var decoded = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(decoded)) return decoded;
            }
            catch { }
        }
        return value;
    }

    public static bool SelfTest()
    {
        var report = new List<string>
        {
            $"Marketplace Hub E2E self-test - {DateTimeOffset.Now:O}"
        };
        var allOk = true;
        AppDatabase? db = null;
        StoreProfile? createdStore = null;
        ZnakConfig? originalZnak = null;

        void Check(string name, Func<bool> test)
        {
            try
            {
                var ok = test();
                report.Add($"{(ok ? "[PASS]" : "[FAIL]")} {name}");
                if (!ok) allOk = false;
            }
            catch (Exception ex)
            {
                allOk = false;
                report.Add($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            db = new AppDatabase();

            Check("01. Khởi tạo SQLite và migration", () => File.Exists(db.DbPath));

            var storeName = $"__SELFTEST_{Guid.NewGuid():N}";
            Check("02. Lưu/đọc cửa hàng + DPAPI bảo vệ thông tin xác thực", () =>
            {
                createdStore = db.SaveStore(new StoreProfile(
                    0, Marketplace.Wildberries, storeName, "",
                    "selftest-api-key", "", "", "selftest-token", true));
                var loaded = db.Stores().Single(x => x.Id == createdStore.Id);
                return loaded.Name == storeName
                       && loaded.Marketplace == Marketplace.Wildberries
                       && loaded.ApiKey == "selftest-api-key"
                       && loaded.Token == "selftest-token"
                       && loaded.Enabled;
            });

            Check("03. Đồng bộ giả lập sản phẩm -> SQLite -> đọc lại", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceProducts(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new ProductRow(createdStore.Id, Marketplace.Wildberries, "123456789",
                        "SKU-SELFTEST", "Sản phẩm kiểm thử", 12345m, "", "{\"source\":\"selftest\"}")
                });
                var p = db.Product(createdStore.Id, "SKU-SELFTEST");
                return p is not null
                       && p.ExternalId == "123456789"
                       && p.Price == 12345m
                       && p.Name == "Sản phẩm kiểm thử";
            });

            Check("04. Đồng bộ giả lập đơn FBS -> SQLite -> đọc lại", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceOrders(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new FbsOrderRow(createdStore.Id, Marketplace.Wildberries, "900000001",
                        "SKU-SELFTEST", "Sản phẩm kiểm thử", 1, "new", true, "{\"source\":\"selftest\"}")
                });
                var o = db.Orders(createdStore.Id).SingleOrDefault(x => x.ExternalOrderId == "900000001");
                return o is not null && o.Sku == "SKU-SELFTEST" && o.NeedsKiz && o.Status == "new";
            });

            Check("05. Phân tích DataMatrix/KIZ hợp lệ", () =>
            {
                var parsed = ParseKiz("014601234567890221SELFTEST");
                return parsed.Ok && parsed.Gtin == "46012345678902";
            });

            Check("06. Từ chối DataMatrix/KIZ không hợp lệ", () => !ParseKiz("INVALID-CODE").Ok);

            Check("07. Lưu/đọc cấu hình Znack và khôi phục cấu hình cũ", () =>
            {
                originalZnak = db.GetZnakConfig();
                var testConfig = new ZnakConfig(
                    "7701234567", "Test", "SELFTEST-THUMBPRINT", "CN=MarketplaceHub SelfTest",
                    "Thủ công", true, "SELFTEST-OMS", "SELFTEST-CONNECTION", true);
                db.SaveZnakConfig(testConfig);
                var loaded = db.GetZnakConfig();
                var ok = loaded == testConfig;
                db.SaveZnakConfig(originalZnak);
                originalZnak = null;
                return ok;
            });

            Check("08. Ghi và đọc nhật ký audit", () =>
            {
                var marker = Guid.NewGuid().ToString("N");
                db.Audit("SelfTest", "E2E", marker);
                return db.AuditRows(100).Any(x => x.Module == "SelfTest" && x.Detail == marker);
            });

            Check("09. Sync run/state được lưu và đọc lại", () =>
            {
                if (createdStore is null) return false;
                var run = db.StartSyncRun(createdStore.Id, "selftest");
                db.SaveSyncState(createdStore.Id, "selftest", "cursor-1", "from", "to", "");
                db.FinishSyncRun(run, true, 2, 2);
                var state = db.SyncState(createdStore.Id, "selftest");
                var latest = db.SyncRuns(createdStore.Id, 10).FirstOrDefault(x => x.Id == run);
                return state.Cursor == "cursor-1" && latest is not null && latest.Success && latest.WrittenCount == 2;
            });

            Check("10. FBO cache và Znack pipeline tồn tại qua vòng đọc", () =>
            {
                if (createdStore is null) return false;
                db.ReplaceFboSupplies(createdStore.Id, Marketplace.Wildberries, new[]
                {
                    new FboSupplyRow(createdStore.Id, Marketplace.Wildberries, "PRE-1", "SUP-1", "READY", "Kho kiểm thử", "2026-09-30", 10, 2, "{}")
                });
                db.UpsertZnakPipeline(createdStore.Id, "SKU-SELFTEST", "46012345678902", "QUEUED", "", "selftest");
                return db.FboSupplies(createdStore.Id).Any(x => x.OrderId == "PRE-1")
                       && db.ZnakPipelines(createdStore.Id).Any(x => x.Sku == "SKU-SELFTEST" && x.Stage == "QUEUED");
            });

            Check("11. Đọc kho chứng thư Windows không làm ứng dụng lỗi", () =>
            {
                _ = Certificates();
                return true;
            });

            Check("12. Parser INN chứng thư nhận nhãn Nga/OID", () =>
            {
                return ExtractInn("CN=Тест, ИНН ФЛ=123456789012")
                       == "123456789012"
                       && ExtractInn("CN=Test, OID.1.2.643.3.131.1.1=7701234567")
                       == "7701234567";
            });

            Check("13. Chuẩn hóa EAN-13 thành GTIN-14", () =>
            {
                return NormalizeGtin14("4681005807182") == "04681005807182"
                       && NormalizeGtin14("04681005807182") == "04681005807182";
            });

            Check("14. Xóa cửa hàng và toàn bộ dữ liệu liên quan", () =>
            {
                if (createdStore is null) return false;
                var id = createdStore.Id;
                db.DeleteStore(id);
                createdStore = null;
                return db.Stores().All(x => x.Id != id)
                       && db.Products(id).Count == 0
                       && db.Orders(id).Count == 0
                       && db.FboSupplies(id).Count == 0
                       && db.ZnakPipelines(id).Count == 0
                       && db.SyncRuns(id, 10).Count == 0;
            });
        }
        finally
        {
            if (db is not null && originalZnak is not null)
            {
                try { db.SaveZnakConfig(originalZnak); }
                catch (Exception ex) { allOk = false; report.Add($"[FAIL] Khôi phục cấu hình Znack: {ex.Message}"); }
            }

            if (db is not null && createdStore is not null)
            {
                try { db.DeleteStore(createdStore.Id); }
                catch (Exception ex) { allOk = false; report.Add($"[FAIL] Dọn dữ liệu self-test: {ex.Message}"); }
            }

            report.Add(allOk ? "KẾT QUẢ: PASS" : "KẾT QUẢ: FAIL");
            try
            {
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "MarketplaceHub-self-test-report.txt"), report);
            }
            catch { }
        }

        return allOk;
    }
}
