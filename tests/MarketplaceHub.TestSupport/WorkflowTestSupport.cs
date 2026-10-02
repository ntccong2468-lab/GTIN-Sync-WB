using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services.Fbs;
using MarketplaceHub.Services.Suz;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Text.Json;

namespace MarketplaceHub.TestSupport;
public static class WorkflowTestSupport
{
    public static AppServices App(AppDatabase db,MarketplaceGateway api)=>new(db,api,SignedLicense(()=>DateTimeOffset.UtcNow),new(new OfflineSuz(),new IntroducedReader(db.CodeProtector),new WorkflowClock()));
    public static SuzProfile Profile(AppDatabase db,StoreProfile store)
    {var profile=new SuzProfile("fixture-"+store.Id,store.Id,1,"7701234567","Production","lp","PRODUCTION","UNIT",10,db.ZnakCredentialVersion(),false,false,new("https://suzgrid.crpt.ru"),new("https://markirovka.crpt.ru"));db.SaveSuzProfile(profile);return profile;}
    public static async Task<FbsWorkflowContext> PreparedContext(AppServices app,LabelTarget target,IReadOnlyList<string> physicalCodes,Func<FbsOrderRow,string>? wbGtin=null,Func<MarketplaceFbsItem,string>? marketGtin=null)
    {
        var store=app.Db.Stores().Single(x=>x.Id==target.StoreId);var profile=app.Db.SuzProfileForStore(store.Id)??Profile(app.Db,store);
        var adapter=new FbsLabelAdapter(app,store.Marketplace,wbGtin,marketGtin);var snapshot=await adapter.ReadSnapshotAsync(target,default);var job=app.Db.GetOrCreateLabelJob(snapshot);var context=new FbsWorkflowContext(job.Id,job.Revision,job.Snapshot,profile);
        foreach(var raw in physicalCodes)app.Db.ImportScopedKiz(new(store.Id,profile.OwnerInn,profile.Environment),raw,"FixturePhysicalScan");
        foreach(var pair in app.Db.ReserveScopedKiz(context,snapshot.Units))app.Db.ConfirmPhysicalMark(new(pair.Key,app.Db.CodeProtector.Identity(pair.Value),DateTimeOffset.UtcNow));return context;
    }
    private sealed class IntroducedReader(IKizCodeProtector protector):IKizLegalReader
    {public Task<IReadOnlyList<KizLegalProof>> ReadAsync(SuzProfile profile,IReadOnlyList<string> rawCodes,CancellationToken ct)=>Task.FromResult<IReadOnlyList<KizLegalProof>>(rawCodes.Select(raw=>new KizLegalProof(protector.Identity(raw),KizCodeIdentity.Gtin(raw),profile.OwnerInn,profile.Environment,"INTRODUCED","EMPTY","UNIT",DateTimeOffset.UtcNow,"FixtureTrueApi")).ToArray());}
    private sealed class OfflineSuz:ISuzClient
    {
        public Task<SuzOutcome<SuzOrderReceipt>> CreateOrderAsync(PurchaseIntent i,CancellationToken ct)=>throw new Exception("Fixture must not buy KIZ");
        public Task<SuzOutcome<SuzOrderStatus>> ReadOrderAsync(PurchaseIntent i,CancellationToken ct)=>throw new Exception("Fixture must not poll SUZ");
        public Task<SuzOutcome<IReadOnlyList<SuzOrderCandidate>>> ListOrdersAsync(PurchaseIntent i,CancellationToken ct)=>throw new Exception("Fixture must not list SUZ");
        public Task<SuzOutcome<SuzBlock>> ReceiveCodesAsync(PurchaseIntent i,int q,CancellationToken ct)=>throw new Exception("Fixture must not receive KIZ");
        public Task<SuzOutcome<IReadOnlyList<SuzBlock>>> ListBlocksAsync(PurchaseIntent i,CancellationToken ct)=>throw new Exception("Fixture must not list blocks");
        public Task<SuzOutcome<SuzBlock>> RecoverBlockAsync(PurchaseIntent i,string id,CancellationToken ct)=>throw new Exception("Fixture must not recover blocks");
    }
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
