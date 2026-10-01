using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Text;
using System.Text.Json;

var failures = new List<string>();
var checks = 0;

async Task Check(string name, Func<Task> test)
{
    checks++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
}

void Expect(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var random = new SecureRandom();
var generator = new Ed25519KeyPairGenerator();
generator.Init(new Ed25519KeyGenerationParameters(random));
var keys = generator.GenerateKeyPair();
var publicKey = ((Ed25519PublicKeyParameters)keys.Public).GetEncoded();
var privateKey = (Ed25519PrivateKeyParameters)keys.Private;
var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

SignedLicenseFile Signed(string status = "valid", DateTimeOffset? expiresAt = null, int? maxStores = 3,
    IReadOnlyDictionary<string, int>? marketplaceLimits = null)
{
    var payload = new LicensePayload(
        1, "WC-TEST1-TEST1-TEST1-TEST1", "fp-test", "standard", 1, status,
        now.AddDays(-1).ToUnixTimeMilliseconds(),
        (expiresAt ?? now.AddDays(30)).ToUnixTimeMilliseconds(),
        maxStores,
        marketplaceLimits);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseJson.Options);
    var signer = new Ed25519Signer();
    signer.Init(true, privateKey);
    signer.BlockUpdate(bytes, 0, bytes.Length);
    return new SignedLicenseFile(
        Convert.ToBase64String(bytes),
        Convert.ToBase64String(signer.GenerateSignature()),
        "Ed25519");
}

LicenseAccessService Service(
    SignedLicenseFile? cached,
    Func<LicenseValidationRequest, CancellationToken, Task<LicenseServerResult>> online) =>
    new(
        new MemoryLicenseStorage(cached),
        new Ed25519LicenseVerifier(Convert.ToBase64String(publicKey)),
        new DelegateLicenseServerClient(online),
        () => now);

await Check("signed unexpired cache opens Add Store without an online request", async () =>
{
    var calls = 0;
    var service = Service(Signed(), (_, _) => { calls++; throw new HttpRequestException("offline"); });
    var result = service.CanOpenAddStore();
    Expect(result.Allowed && result.Code == LicenseGateCode.ValidCached, "Valid signed cache did not open the form.");
    Expect(calls == 0, "Opening Add Store contacted the license server.");
    await Task.CompletedTask;
});

await Check("expired cache blocks opening Add Store", async () =>
{
    var result = Service(Signed(expiresAt: now.AddSeconds(-1)), (_, _) => throw new Exception()).CanOpenAddStore();
    Expect(!result.Allowed && result.Code == LicenseGateCode.Expired, "Expired cache was accepted.");
    await Task.CompletedTask;
});

await Check("invalid signature blocks opening Add Store", async () =>
{
    var file = Signed();
    var bytes = Convert.FromBase64String(file.Payload);
    bytes[^1] ^= 1;
    var result = Service(file with { Payload = Convert.ToBase64String(bytes) }, (_, _) => throw new Exception()).CanOpenAddStore();
    Expect(!result.Allowed && result.Code == LicenseGateCode.InvalidSignature, "Tampered payload was accepted.");
    await Task.CompletedTask;
});

await Check("temporary outage blocks save as network error, never maintenance", async () =>
{
    var service = Service(Signed(), (_, _) => throw new HttpRequestException("connection refused"));
    var result = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 1, 1);
    Expect(!result.Allowed && result.Code == LicenseGateCode.NetworkUnavailable, "Offline save was not blocked as a network error.");
    Expect(!result.Message.Contains("bảo trì", StringComparison.OrdinalIgnoreCase), "Network outage was mislabeled as maintenance.");
});

await Check("explicit server maintenance remains distinct", async () =>
{
    var service = Service(Signed(), (_, _) => Task.FromResult(LicenseServerResult.Rejected("maintenance")));
    var result = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 1, 1);
    Expect(!result.Allowed && result.Code == LicenseGateCode.Maintenance, "Explicit maintenance was not preserved.");
});

await Check("online signed response allows save while capacity remains", async () =>
{
    var online = Signed(maxStores: 3, marketplaceLimits: new Dictionary<string, int> { ["Ozon"] = 2 });
    var service = Service(Signed(), (_, _) => Task.FromResult(LicenseServerResult.Accepted(online)));
    var result = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 1, 1);
    Expect(result.Allowed && result.Code == LicenseGateCode.ValidOnline, "Online validation should allow the second store.");
});

await Check("online validation blocks total and marketplace store limits", async () =>
{
    var online = Signed(maxStores: 2, marketplaceLimits: new Dictionary<string, int> { ["Wildberries"] = 1 });
    var service = Service(Signed(), (_, _) => Task.FromResult(LicenseServerResult.Accepted(online)));
    var total = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 2, 0);
    var market = await service.ValidateBeforeCreateStoreAsync(Marketplace.Wildberries, 1, 1);
    Expect(!total.Allowed && total.Code == LicenseGateCode.StoreLimitReached, "Total store limit was ignored.");
    Expect(!market.Allowed && market.Code == LicenseGateCode.StoreLimitReached, "Marketplace store limit was ignored.");
});

await Check("server device/store limit codes are not collapsed into maintenance", async () =>
{
    foreach (var pair in new[]
    {
        (ServerCode: "device_limit_reached", Gate: LicenseGateCode.DeviceLimitReached),
        (ServerCode: "shop_limit_reached", Gate: LicenseGateCode.StoreLimitReached)
    })
    {
        var service = Service(Signed(), (_, _) => Task.FromResult(LicenseServerResult.Rejected(pair.ServerCode)));
        var result = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 0, 0);
        Expect(!result.Allowed && result.Code == pair.Gate, $"{pair.ServerCode} mapped to {result.Code}.");
    }
});

await Check("malformed online signature blocks save and keeps previous cache", async () =>
{
    var cached = Signed();
    var storage = new MemoryLicenseStorage(cached);
    var bad = Signed() with { Signature = Convert.ToBase64String(Encoding.UTF8.GetBytes("bad")) };
    var service = new LicenseAccessService(storage, new Ed25519LicenseVerifier(Convert.ToBase64String(publicKey)),
        new DelegateLicenseServerClient((_, _) => Task.FromResult(LicenseServerResult.Accepted(bad))), () => now);
    var result = await service.ValidateBeforeCreateStoreAsync(Marketplace.Ozon, 0, 0);
    Expect(!result.Allowed && result.Code == LicenseGateCode.InvalidSignature, "Bad online signature was accepted.");
    Expect(ReferenceEquals(storage.Load(), cached), "A bad online response replaced the known-good cache.");
});

await Check("failed online validation writes no store row", async () =>
{
    var databasePath = Path.Combine(Path.GetTempPath(), "MarketplaceHub-license-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var db = new AppDatabase(databasePath);
        var license = Service(Signed(), (_, _) => throw new HttpRequestException("offline"));
        var app = new AppServices(db, new MarketplaceGateway(), license);
        var result = await app.CreateStoreAsync(new StoreProfile(0, Marketplace.Ozon, "blocked", "client", "key", "", "", "", true));
        Expect(!result.Success && db.Stores().Count == 0, "Store row was written before online validation succeeded.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }
});

await Check("concurrent creates are serialized against the signed store limit", async () =>
{
    var databasePath = Path.Combine(Path.GetTempPath(), "MarketplaceHub-license-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var db = new AppDatabase(databasePath);
        var online = Signed(maxStores: 1);
        var license = Service(Signed(), async (_, ct) =>
        {
            await Task.Delay(20, ct);
            return LicenseServerResult.Accepted(online);
        });
        var app = new AppServices(db, new MarketplaceGateway(), license);
        var template = new StoreProfile(0, Marketplace.Ozon, "one", "client", "key", "", "", "", true);
        var results = await Task.WhenAll(app.CreateStoreAsync(template), app.CreateStoreAsync(template with { Name = "two" }));
        Expect(results.Count(x => x.Success) == 1 && db.Stores().Count == 1,
            "Concurrent clicks bypassed the one-store license limit.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }
});

Console.WriteLine($"License gate checks: {checks - failures.Count}/{checks}");
return failures.Count == 0 ? 0 : 1;

sealed class MemoryLicenseStorage(SignedLicenseFile? value) : ILicenseStorage
{
    private SignedLicenseFile? current = value;
    public SignedLicenseFile? Load() => current;
    public void Save(SignedLicenseFile value) => current = value;
}

sealed class DelegateLicenseServerClient(
    Func<LicenseValidationRequest, CancellationToken, Task<LicenseServerResult>> callback) : ILicenseServerClient
{
    public Task<LicenseServerResult> ValidateStoreCreationAsync(LicenseValidationRequest request, CancellationToken ct) => callback(request, ct);
}
