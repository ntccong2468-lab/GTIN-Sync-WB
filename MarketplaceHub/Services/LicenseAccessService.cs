using MarketplaceHub.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public interface ILicenseStorage
{
    SignedLicenseFile? Load();
    void Save(SignedLicenseFile value);
    DateTimeOffset? LoadLastSeen();
    void SaveLastSeen(DateTimeOffset value);
}

public interface ILicenseServerClient
{
    Task<LicenseServerResult> ValidateStoreCreationAsync(LicenseValidationRequest request, CancellationToken ct);
}

public sealed class Ed25519LicenseVerifier
{
    private readonly Ed25519PublicKeyParameters publicKey;

    public Ed25519LicenseVerifier(string publicKeyBase64)
    {
        var encoded = Convert.FromBase64String(publicKeyBase64);
        if (encoded.Length == Ed25519PublicKeyParameters.KeySize)
            publicKey = new Ed25519PublicKeyParameters(encoded);
        else
            publicKey = PublicKeyFactory.CreateKey(encoded) as Ed25519PublicKeyParameters
                ?? throw new InvalidOperationException("License public key is not Ed25519.");
    }

    public LicensePayload? Verify(SignedLicenseFile? file)
    {
        if (file is null || !file.Algorithm.Equals("Ed25519", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var payload = Convert.FromBase64String(file.Payload);
            var signature = Convert.FromBase64String(file.Signature);
            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(payload, 0, payload.Length);
            if (!verifier.VerifySignature(signature)) return null;
            return JsonSerializer.Deserialize<LicensePayload>(payload, LicenseJson.Options);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed class FileLicenseStorage : ILicenseStorage
{
    private readonly string primaryPath;
    private readonly string? fallbackPath;
    private readonly object gate = new();

    public FileLicenseStorage(string primaryPath, string? fallbackPath = null)
    {
        this.primaryPath = primaryPath;
        this.fallbackPath = fallbackPath;
    }

    public SignedLicenseFile? Load()
    {
        lock (gate)
        {
            foreach (var path in new[] { primaryPath, fallbackPath })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                try
                {
                    return JsonSerializer.Deserialize<SignedLicenseFile>(File.ReadAllText(path), LicenseJson.Options);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            return null;
        }
    }

    public void Save(SignedLicenseFile value)
    {
        lock (gate)
        {
            var directory = Path.GetDirectoryName(primaryPath) ?? throw new InvalidOperationException("License path has no directory.");
            Directory.CreateDirectory(directory);
            var staging = primaryPath + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(value, LicenseJson.Options), Encoding.UTF8);
            File.Move(staging, primaryPath, true);
        }
    }

    public DateTimeOffset? LoadLastSeen()
    {
        lock(gate)
        {
            try
            {
                var path=primaryPath+".clock";if(!File.Exists(path))return null;
                var protectedBytes=File.ReadAllBytes(path);
                var bytes=System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes,
                    Encoding.UTF8.GetBytes("MarketplaceHub-license-clock"),System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(bytes));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException){return null;}
        }
    }

    public void SaveLastSeen(DateTimeOffset value)
    {
        lock(gate)
        {
            var directory=Path.GetDirectoryName(primaryPath)??throw new InvalidOperationException("License path has no directory.");Directory.CreateDirectory(directory);
            var bytes=BitConverter.GetBytes(value.ToUnixTimeMilliseconds());
            var protectedBytes=System.Security.Cryptography.ProtectedData.Protect(bytes,
                Encoding.UTF8.GetBytes("MarketplaceHub-license-clock"),System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var path=primaryPath+".clock";var staging=path+".tmp";File.WriteAllBytes(staging,protectedBytes);File.Move(staging,path,true);
        }
    }
}

public sealed class HttpLicenseServerClient : ILicenseServerClient
{
    private readonly HttpClient http;
    private readonly Uri validateUri;

    public HttpLicenseServerClient(HttpClient http, string baseUrl)
    {
        this.http = http;
        validateUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/v1/validate");
    }

    public async Task<LicenseServerResult> ValidateStoreCreationAsync(LicenseValidationRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, validateUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, LicenseJson.Options), Encoding.UTF8, "application/json")
        };
        message.Headers.UserAgent.ParseAdd("MarketplaceHub/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0"));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return LicenseServerResult.Rejected(ReadErrorCode(body, response.StatusCode));
        try
        {
            var root = JsonNode.Parse(body);
            var file = root?["licenseFile"]?.Deserialize<SignedLicenseFile>(LicenseJson.Options);
            return file is null ? LicenseServerResult.Rejected("invalid_response") : LicenseServerResult.Accepted(file);
        }
        catch (JsonException)
        {
            return LicenseServerResult.Rejected("invalid_response");
        }
    }

    private static string ReadErrorCode(string body, HttpStatusCode status)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() ?? "http_" + (int)status;
        }
        catch (JsonException) { return "http_" + (int)status; }
    }
}

public sealed class LicenseAccessService
{
    private const string ProductionPublicKey = "MCowBQYDK2VwAyEANb0850xNtBhlYwnNaHU6Lh9RpULajzI/akwrnGk5dpc=";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromHours(2);
    private readonly ILicenseStorage storage;
    private readonly Ed25519LicenseVerifier verifier;
    private readonly ILicenseServerClient server;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string> fingerprint;

    public LicenseAccessService(ILicenseStorage storage, Ed25519LicenseVerifier verifier,
        ILicenseServerClient server, Func<DateTimeOffset>? clock = null, Func<string>? fingerprint = null)
    {
        this.storage = storage;
        this.verifier = verifier;
        this.server = server;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.fingerprint = fingerprint ?? DeviceFingerprint.Get;
    }

    public static LicenseAccessService CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var primary = Path.Combine(local, "MarketplaceHub", "license.json");
        var wcode = Path.Combine(local, "WCodeData", "license.json");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var url = Environment.GetEnvironmentVariable("MARKETPLACEHUB_LICENSE_URL") ?? "https://wcode.online";
        return new LicenseAccessService(new FileLicenseStorage(primary, wcode),
            new Ed25519LicenseVerifier(ProductionPublicKey), new HttpLicenseServerClient(http, url), fingerprint:DeviceFingerprint.Get);
    }

    public LicenseGateResult CanOpenAddStore() => EvaluateCached(storage.Load(), LicenseGateCode.ValidCached);

    public async Task<LicenseGateResult> ValidateBeforeCreateStoreAsync(
        Marketplace marketplace, int currentStoreCount, int currentMarketplaceStoreCount, CancellationToken ct = default)
    {
        var file = storage.Load();
        var cached = EvaluateCached(file, LicenseGateCode.ValidCached);
        if (!cached.Allowed) return cached;
        var payload = verifier.Verify(file)!;
        var localFingerprint=fingerprint();
        LicenseServerResult response;
        try
        {
            response = await server.ValidateStoreCreationAsync(new LicenseValidationRequest(
                payload.LicenseKey, localFingerprint,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0",
                marketplace.ToString(), currentStoreCount, currentMarketplaceStoreCount), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
        {
            return LicenseGateResult.Block(LicenseGateCode.NetworkUnavailable,
                "Không thể kết nối máy chủ license. Chưa lưu cửa hàng; hãy kiểm tra mạng và thử lại.");
        }

        if (!response.Success) return MapServerError(response.ErrorCode);
        var onlinePayload = verifier.Verify(response.LicenseFile);
        if (onlinePayload is null)
            return LicenseGateResult.Block(LicenseGateCode.InvalidSignature,
                "Phản hồi license không có chữ ký hợp lệ. Chưa lưu cửa hàng.");
        if(!string.Equals(onlinePayload.LicenseKey,payload.LicenseKey,StringComparison.Ordinal)
            ||!string.Equals(onlinePayload.Fingerprint,localFingerprint,StringComparison.Ordinal))
            return LicenseGateResult.Block(LicenseGateCode.DeviceMismatch,
                "Phản hồi license không thuộc license hoặc thiết bị hiện tại. Chưa lưu cửa hàng.");
        var validity = EvaluatePayload(onlinePayload, LicenseGateCode.ValidOnline);
        if (!validity.Allowed) return validity;
        var limit = CheckStoreLimit(onlinePayload, marketplace, currentStoreCount, currentMarketplaceStoreCount);
        if (!limit.Allowed) return limit;
        try { storage.Save(response.LicenseFile!); storage.SaveLastSeen(clock()); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        return LicenseGateResult.Allow(LicenseGateCode.ValidOnline, "License đã được xác thực online.");
    }

    private LicenseGateResult EvaluateCached(SignedLicenseFile? file, LicenseGateCode allowedCode)
    {
        if (file is null)
            return LicenseGateResult.Block(LicenseGateCode.NotActivated,
                "Chưa tìm thấy license đã kích hoạt trên máy này.");
        var payload = verifier.Verify(file);
        if(payload is null)
            return LicenseGateResult.Block(LicenseGateCode.InvalidSignature,
                "License lưu trên máy không có chữ ký hợp lệ.");
        if(!string.Equals(payload.Fingerprint,fingerprint(),StringComparison.Ordinal))
            return LicenseGateResult.Block(LicenseGateCode.DeviceMismatch,
                "License lưu trên máy thuộc thiết bị khác. Hãy kích hoạt license trên máy này.");
        var result=EvaluatePayload(payload, allowedCode);
        if(result.Allowed)
            try { storage.SaveLastSeen(clock()); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        return result;
    }

    private LicenseGateResult EvaluatePayload(LicensePayload payload, LicenseGateCode allowedCode)
    {
        var now = clock();
        var issued = DateTimeOffset.FromUnixTimeMilliseconds(payload.IssuedAt);
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresAt);
        var lastSeen=storage.LoadLastSeen();
        if (now + ClockSkew < issued || lastSeen is not null && now + ClockSkew < lastSeen.Value)
            return LicenseGateResult.Block(LicenseGateCode.ClockTampered,
                "Thời gian hệ thống không khớp với lần xác thực license.");
        if (!payload.Status.Equals("valid", StringComparison.OrdinalIgnoreCase) || now >= expires)
            return LicenseGateResult.Block(LicenseGateCode.Expired,
                "License đã hết hạn. Không thể thêm cửa hàng mới.");
        return LicenseGateResult.Allow(allowedCode, "License đã ký còn hiệu lực.");
    }

    private static LicenseGateResult CheckStoreLimit(LicensePayload payload, Marketplace marketplace,
        int currentStoreCount, int currentMarketplaceStoreCount)
    {
        if (payload.MaxStores is > 0 && currentStoreCount >= payload.MaxStores.Value)
            return LicenseGateResult.Block(LicenseGateCode.StoreLimitReached,
                $"License đã đạt giới hạn {payload.MaxStores.Value} cửa hàng.");
        int? marketLimit = null;
        if (payload.MarketplaceLimits is not null)
            foreach (var entry in payload.MarketplaceLimits)
                if (entry.Key.Equals(marketplace.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    marketLimit = entry.Value;
                    break;
                }
        if (marketLimit is > 0 && currentMarketplaceStoreCount >= marketLimit.Value)
            return LicenseGateResult.Block(LicenseGateCode.StoreLimitReached,
                $"License đã đạt giới hạn {marketLimit.Value} cửa hàng {marketplace}.");
        return LicenseGateResult.Allow(LicenseGateCode.ValidOnline, "Còn quyền thêm cửa hàng.");
    }

    private static LicenseGateResult MapServerError(string code) => code.ToLowerInvariant() switch
    {
        "maintenance" or "service_maintenance" => LicenseGateResult.Block(LicenseGateCode.Maintenance,
            "Máy chủ license đang bảo trì. Chưa lưu cửa hàng; vui lòng thử lại sau."),
        "license_expired" or "expired" => LicenseGateResult.Block(LicenseGateCode.Expired,
            "License đã hết hạn. Không thể thêm cửa hàng mới."),
        "device_limit_reached" => LicenseGateResult.Block(LicenseGateCode.DeviceLimitReached,
            "License đã đạt giới hạn thiết bị."),
        "shop_limit_reached" or "store_limit_reached" => LicenseGateResult.Block(LicenseGateCode.StoreLimitReached,
            "License đã đạt giới hạn cửa hàng."),
        "http_408" or "http_429" or "http_500" or "http_502" or "http_503" or "http_504" =>
            LicenseGateResult.Block(LicenseGateCode.NetworkUnavailable,
                "Máy chủ license tạm thời không phản hồi. Chưa lưu cửa hàng; hãy thử lại."),
        _ => LicenseGateResult.Block(LicenseGateCode.Invalid,
            "License không còn hợp lệ cho thao tác thêm cửa hàng.")
    };
}
