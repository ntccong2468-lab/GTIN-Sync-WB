using MarketplaceHub.Core;
using MarketplaceHub.Services.Suz;
namespace MarketplaceHub.Workflows;
public sealed class FakeSuzClient : ISuzClient
{
    public int CreateCalls, ReadCalls, ReceiveCalls, ListCalls, ListBlockCalls, RecoverCalls;
    public Func<PurchaseIntent, CancellationToken, Task<SuzOutcome<SuzOrderReceipt>>>? OnCreate;
    public Func<PurchaseIntent, CancellationToken, Task<SuzOutcome<SuzOrderStatus>>>? OnRead;
    public Func<PurchaseIntent, int, CancellationToken, Task<SuzOutcome<SuzBlock>>>? OnReceive;
    public Func<PurchaseIntent, string, CancellationToken, Task<SuzOutcome<SuzBlock>>>? OnRecover;
    public IReadOnlyList<SuzOrderCandidate> Candidates = Array.Empty<SuzOrderCandidate>();
    public IReadOnlyList<SuzBlock> Blocks = Array.Empty<SuzBlock>();
    public List<string?> ReadIds { get; } = new();
    public int TotalCalls => CreateCalls + ReadCalls + ReceiveCalls + ListCalls + ListBlockCalls + RecoverCalls;
    public Task<SuzOutcome<SuzOrderReceipt>> CreateOrderAsync(PurchaseIntent intent, CancellationToken ct) { CreateCalls++; return OnCreate?.Invoke(intent, ct) ?? Task.FromResult(new SuzOutcome<SuzOrderReceipt>(SuzOutcomeKind.Confirmed, new("SUZ-1"))); }
    public Task<SuzOutcome<SuzOrderStatus>> ReadOrderAsync(PurchaseIntent intent, CancellationToken ct) { ReadCalls++; ReadIds.Add(intent.RemoteOrderId); return OnRead?.Invoke(intent, ct) ?? Task.FromResult(new SuzOutcome<SuzOrderStatus>(SuzOutcomeKind.Confirmed, new(intent.RemoteOrderId!, "ACTIVE", 2, null))); }
    public Task<SuzOutcome<IReadOnlyList<SuzOrderCandidate>>> ListOrdersAsync(PurchaseIntent intent, CancellationToken ct) { ListCalls++; return Task.FromResult(new SuzOutcome<IReadOnlyList<SuzOrderCandidate>>(SuzOutcomeKind.Confirmed, Candidates)); }
    public Task<SuzOutcome<SuzBlock>> ReceiveCodesAsync(PurchaseIntent intent, int quantity, CancellationToken ct) { ReceiveCalls++; return OnReceive?.Invoke(intent, quantity, ct) ?? Task.FromResult(new SuzOutcome<SuzBlock>(SuzOutcomeKind.Confirmed, FullBlock(intent))); }
    public Task<SuzOutcome<IReadOnlyList<SuzBlock>>> ListBlocksAsync(PurchaseIntent intent, CancellationToken ct) { ListBlockCalls++; return Task.FromResult(new SuzOutcome<IReadOnlyList<SuzBlock>>(SuzOutcomeKind.Confirmed, Blocks)); }
    public Task<SuzOutcome<SuzBlock>> RecoverBlockAsync(PurchaseIntent intent, string id, CancellationToken ct) { RecoverCalls++; return OnRecover?.Invoke(intent, id, ct) ?? Task.FromResult(new SuzOutcome<SuzBlock>(SuzOutcomeKind.Confirmed, FullBlock(intent) with { BlockId = id })); }
    public static SuzBlock FullBlock(PurchaseIntent i) => new("BLOCK-1", i.RemoteOrderId!, i.Request.Gtin, new[] { WorkflowFixture.Raw, WorkflowFixture.Raw.Replace("SERIAL0000001", "SERIAL0000002") });
}
