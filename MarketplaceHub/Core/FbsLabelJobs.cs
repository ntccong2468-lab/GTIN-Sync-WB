using System.Security.Cryptography;
using System.Text.Json;
namespace MarketplaceHub.Core;
public enum LabelTargetKind { WbSupply, MarketplaceBatch }
public enum FbsLabelJobStage { Queued, Validating, AwaitingPurchase, Purchasing, AwaitingCodes, Recovering, AwaitingLegalState, AwaitingPhysicalMark, Attaching, Verifying, LabelsReady, Partial, Paused, NeedsReconciliation, SnapshotChanged, Failed, Cancelled }
public enum WorkflowRunMode { UserRequested, RecoveryOnly }
public sealed record LabelTarget(long StoreId, Marketplace Marketplace, LabelTargetKind Kind, string TargetId);
public sealed record FbsUnitKey(long StoreId, Marketplace Marketplace, string OrderId, string ItemId, int UnitIndex);
public sealed record FbsUnitDemand(FbsUnitKey Unit, string Sku, string Gtin, long MappingVersion, bool RequiresKiz);
public sealed record LabelJobSnapshot(LabelTarget Target, int StoreGeneration, IReadOnlyList<FbsUnitDemand> Units, string ItemFingerprint, string RequirementFingerprint);
public sealed record FbsLabelJob(string Id, int Revision, LabelJobSnapshot Snapshot, string SnapshotHash, FbsLabelJobStage Stage, bool Active, long Version, DateTimeOffset UpdatedAt);
public sealed record UnitWorkflowResult(FbsUnitKey Unit, string Stage, string? CodeHash, string? ErrorCode);
public sealed record LabelJobResult(string JobId, int Revision, FbsLabelJobStage Stage, int VerifiedUnits, int TotalUnits, IReadOnlyList<UnitWorkflowResult> Units, IReadOnlyList<LabelArtifact> Artifacts, string? ErrorCode);
public sealed record LabelArtifact(string Id, string JobId, int Revision, string Kind, string FilePath, string Sha256, IReadOnlyList<FbsUnitKey> Units, bool OfficialMarketplaceLabel);
public sealed record FbsWorkflowContext(string JobId, int Revision, LabelJobSnapshot Snapshot, SuzProfile? Profile);
public static class WorkflowIdentity
{
    public static string Unit(FbsUnitKey unit) => JsonSerializer.Serialize(unit);
    public static string Snapshot(LabelJobSnapshot snapshot)
    {
        var ordered = snapshot with { Units = snapshot.Units.OrderBy(x => x.Unit.StoreId).ThenBy(x => x.Unit.Marketplace)
            .ThenBy(x => x.Unit.OrderId, StringComparer.Ordinal).ThenBy(x => x.Unit.ItemId, StringComparer.Ordinal).ThenBy(x => x.Unit.UnitIndex).ToArray() };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ordered)));
    }
}
