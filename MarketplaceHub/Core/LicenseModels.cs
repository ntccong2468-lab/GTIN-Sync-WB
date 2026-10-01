using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Core;

public sealed record SignedLicenseFile(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("algorithm")] string Algorithm);

public sealed record LicensePayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("licenseKey")] string LicenseKey,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("maxDevices")] int MaxDevices,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("issuedAt")] long IssuedAt,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("maxStores")] int? MaxStores = null,
    [property: JsonPropertyName("marketplaceLimits")] IReadOnlyDictionary<string, int>? MarketplaceLimits = null);

public enum LicenseGateCode
{
    ValidCached,
    ValidOnline,
    NotActivated,
    Expired,
    Invalid,
    InvalidSignature,
    DeviceMismatch,
    ClockTampered,
    NetworkUnavailable,
    Maintenance,
    DeviceLimitReached,
    StoreLimitReached
}

public sealed record LicenseGateResult(bool Allowed, LicenseGateCode Code, string Message)
{
    public static LicenseGateResult Allow(LicenseGateCode code, string message) => new(true, code, message);
    public static LicenseGateResult Block(LicenseGateCode code, string message) => new(false, code, message);
}

public sealed record LicenseValidationRequest(
    string LicenseKey,
    string Fingerprint,
    string AppVersion,
    string Marketplace,
    int CurrentStoreCount,
    int CurrentMarketplaceStoreCount);

public sealed record LicenseServerResult(bool Success, SignedLicenseFile? LicenseFile, string ErrorCode)
{
    public static LicenseServerResult Accepted(SignedLicenseFile file) => new(true, file, "");
    public static LicenseServerResult Rejected(string code) => new(false, null, code ?? "invalid_license");
}

public static class LicenseJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
