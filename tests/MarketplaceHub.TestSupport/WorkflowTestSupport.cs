using MarketplaceHub.Core;
using MarketplaceHub.Services;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Text.Json;

namespace MarketplaceHub.TestSupport;
public static class WorkflowTestSupport
{
    // Ephemeral test key. Production verifier and signed-cache policy remain real.
    public static LicenseAccessService SignedLicense(Func<DateTimeOffset> clock, bool expired = false, bool tampered = false)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keys = generator.GenerateKeyPair(); var now = clock();
        var payload = new LicensePayload(1, "WC-FIXTURE", "workflow-test-device", "standard", 1, "valid",
            now.AddDays(-1).ToUnixTimeMilliseconds(), now.AddDays(expired ? -1 : 30).ToUnixTimeMilliseconds(), 100, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseJson.Options);
        var signer = new Ed25519Signer(); signer.Init(true, keys.Private); signer.BlockUpdate(bytes, 0, bytes.Length);
        var signature = signer.GenerateSignature(); if (tampered) signature[0] ^= 1;
        var file = new SignedLicenseFile(Convert.ToBase64String(bytes), Convert.ToBase64String(signature), "Ed25519");
        return new(new FixtureLicenseStorage(file), new Ed25519LicenseVerifier(Convert.ToBase64String(((Ed25519PublicKeyParameters)keys.Public).GetEncoded())),
            new OfflineLicenseServer(), clock, () => "workflow-test-device");
    }
    private sealed class FixtureLicenseStorage(SignedLicenseFile file) : ILicenseStorage
    {
        private DateTimeOffset? seen;
        public SignedLicenseFile? Load() => file;
        public void Save(SignedLicenseFile value) => file = value;
        public DateTimeOffset? LoadLastSeen() => seen;
        public void SaveLastSeen(DateTimeOffset value) => seen = value;
    }
    private sealed class OfflineLicenseServer : ILicenseServerClient
    {
        public Task<LicenseServerResult> ValidateStoreCreationAsync(LicenseValidationRequest request, CancellationToken ct) => throw new Exception("Workflow tests must use verified cache, never online licensing");
    }
}
