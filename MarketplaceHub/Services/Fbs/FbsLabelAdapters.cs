using MarketplaceHub.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MarketplaceHub.Services.Fbs;
public sealed class FbsLabelAdapter(AppServices app,Marketplace marketplace,Func<FbsOrderRow,string>? wbGtin=null,Func<MarketplaceFbsItem,string>? marketGtin=null) : IFbsLabelAdapter
{
    private StoreProfile Store(LabelTarget target)=>app.Db.Stores().SingleOrDefault(x=>x.Id==target.StoreId&&x.Marketplace==marketplace)??throw new InvalidOperationException("store_deleted");
    public async Task<LabelJobSnapshot> ReadSnapshotAsync(LabelTarget target,CancellationToken ct)
    {
        var store=Store(target);var units=new List<FbsUnitDemand>();var fingerprints=new List<string>();
        if(marketplace==Marketplace.Wildberries){
            if(target.Kind!=LabelTargetKind.WbSupply)throw new InvalidOperationException("wrong_target_kind");
            var received=app.Db.WbReceivedOrderIds(store.Id).Any(id=>app.Db.FindWbSupplyForOrder(store.Id,id)==target.TargetId);if(!received)throw new InvalidOperationException("received_supply_required");
            var supply=await app.Api.GetWbSupplyAsync(store,target.TargetId,ct);if(supply.Id!=target.TargetId)throw new InvalidOperationException("supply_identity_mismatch");
            var rows=await app.ReadWbSupplyOrdersAsync(store,target.TargetId,ct);if(rows.Count==0||rows.Any(x=>x.Quantity!=1||x.Status is not("confirm" or "complete")))throw new InvalidOperationException("supply_units_unresolved");
            var metadata=await app.Api.GetWbPrintKizAsync(store,rows.Select(x=>x.ExternalOrderId),ct);
            foreach(var row in rows){if(!metadata.TryGetValue(row.ExternalOrderId,out var meta))throw new InvalidOperationException("wb_requirements_unresolved");var required=row.NeedsKiz||meta.Required||meta.Codes.Count>0;var gtin=wbGtin?.Invoke(row)??app.ResolveWorkflowWbGtin(store,row);if(required&&GtinCode.Normalize(gtin)!=gtin)throw new InvalidOperationException("gtin_variant_required");
                var item=JsonNode.Parse(row.RawJson)?["chrtId"]?.ToString()??row.Sku;units.Add(new(new(store.Id,marketplace,row.ExternalOrderId,item,0),row.Sku,gtin,app.Db.WorkflowMappingVersion(store,row.Sku),required));fingerprints.Add(row.ExternalOrderId+":"+required);
            }
        }else{
            if(target.Kind!=LabelTargetKind.MarketplaceBatch)throw new InvalidOperationException("wrong_target_kind");var members=app.Db.MarketplaceFbsBatchOrders(store,target.TargetId);if(members.Count==0)throw new InvalidOperationException("received_batch_required");
            foreach(var member in members){var fresh=await app.ReadFreshMarketplaceFbsAsync(store,member.Id,ct);if(!fresh.CanPack&&!fresh.IsPacked)throw new InvalidOperationException("order_not_packable");
                fingerprints.Add(member.Id+":"+fresh.ItemFingerprint);foreach(var item in fresh.Items){var gtin=marketGtin?.Invoke(item)??app.ResolveWorkflowMarketplaceGtin(store,item);if(item.RequiresKiz&&GtinCode.Normalize(gtin)!=gtin)throw new InvalidOperationException("gtin_variant_required");if(item.Quantity<=0)throw new InvalidOperationException("quantity_unresolved");for(var u=0;u<item.Quantity;u++)units.Add(new(new(store.Id,marketplace,member.Id,item.Id,u),item.Offer,gtin,app.Db.WorkflowMappingVersion(store,item.Offer),item.RequiresKiz));}
            }
        }
        if(!app.Db.Stores().Any(x=>x.Id==store.Id))throw new InvalidOperationException("store_deleted");
        var itemHash=Hash(JsonSerializer.Serialize(units.OrderBy(x=>WorkflowIdentity.Unit(x.Unit),StringComparer.Ordinal).Select(x=>new{x.Unit,x.Sku,x.Gtin,x.MappingVersion})));
        var requiredHash=Hash(JsonSerializer.Serialize(units.OrderBy(x=>WorkflowIdentity.Unit(x.Unit),StringComparer.Ordinal).Select(x=>new{x.Unit,x.RequiresKiz}))+string.Join("|",fingerprints.OrderBy(x=>x,StringComparer.Ordinal)));
        return new(target,app.Db.StoreGeneration(store.Id),units,itemHash,requiredHash);
    }
    public async Task<IReadOnlyDictionary<FbsUnitKey,string>> ReadAssignedCodesAsync(FbsWorkflowContext context,CancellationToken ct)
    {
        var store=Store(context.Snapshot.Target);var result=new Dictionary<FbsUnitKey,string>();
        if(marketplace==Marketplace.Wildberries){var metadata=await app.Api.GetWbPrintKizAsync(store,context.Snapshot.Units.Select(x=>x.Unit.OrderId).Distinct(),ct);foreach(var demand in context.Snapshot.Units.Where(x=>x.RequiresKiz)){if(metadata.TryGetValue(demand.Unit.OrderId,out var row)&&row.Codes.Count==1)result[demand.Unit]=row.Codes[0];}}
        else foreach(var order in context.Snapshot.Units.GroupBy(x=>x.Unit.OrderId)){
            var fresh=await app.ReadFreshMarketplaceFbsAsync(store,order.Key,ct);var remote=await app.Api.ReadMarketplaceKizAsync(store,fresh,ct);if(!remote.Verified&&order.Any(x=>x.RequiresKiz))continue;
            foreach(var item in order.Where(x=>x.RequiresKiz).GroupBy(x=>x.Unit.ItemId)){if(!remote.Codes.TryGetValue(item.Key,out var codes)||codes.Count!=item.Count())continue;var reserved=app.Db.MarketplaceKizReservations(store,order.Key).Where(x=>x.ItemId==item.Key).OrderBy(x=>x.Unit).ToArray();var ordered=codes.OrderBy(x=>x,StringComparer.Ordinal).ToArray();if(reserved.Length==codes.Count&&reserved.Select(x=>app.Db.CodeProtector.Identity(x.Code)).ToHashSet().SetEquals(codes.Select(app.Db.CodeProtector.Identity)))ordered=reserved.Select(x=>x.Code).ToArray();foreach(var demand in item)result[demand.Unit]=ordered[demand.Unit.UnitIndex];}
        }return result;
    }
    public async Task<IReadOnlyList<UnitWorkflowResult>> EnsureMarkedAndPackedAsync(FbsWorkflowContext context,IReadOnlyDictionary<FbsUnitKey,string> codes,CancellationToken ct)
    {
        app.RequireWorkflowAuthorization(context);var fresh=await ReadSnapshotAsync(context.Snapshot.Target,ct);if(WorkflowIdentity.Snapshot(fresh)!=WorkflowIdentity.Snapshot(context.Snapshot))throw new InvalidOperationException("snapshot_changed");
        var store=Store(context.Snapshot.Target);var eligible=context.Snapshot.Units.GroupBy(x=>x.Unit.OrderId).Where(g=>g.All(x=>!x.RequiresKiz||codes.ContainsKey(x.Unit))).Select(g=>g.Key).ToHashSet();
        if(marketplace==Marketplace.Wildberries){var rows=await app.ReadWbSupplyOrdersAsync(store,context.Snapshot.Target.TargetId,ct);var selected=rows.Where(x=>eligible.Contains(x.ExternalOrderId)).ToArray();var gtins=context.Snapshot.Units.Where(x=>eligible.Contains(x.Unit.OrderId)).ToDictionary(x=>x.Unit.OrderId,x=>x.Gtin);
            var result=await app.EnsureWbSupplyKizAsync(store,selected,gtins,true,ct,workflowContext:context);if(!result.Success)throw new InvalidOperationException("wb_attach_unverified");
        }else{var result=await app.PackMarketplaceFbsAsync(store,Array.Empty<string>(),item=>context.Snapshot.Units.First(x=>x.Unit.ItemId==item.Id&&x.Sku==item.Offer).Gtin,true,ct,batchId:context.Snapshot.Target.TargetId,workflowContext:context);if(!result.Success)throw new InvalidOperationException("marketplace_pack_unverified");}
        return context.Snapshot.Units.Where(x=>eligible.Contains(x.Unit.OrderId)).Select(x=>new UnitWorkflowResult(x.Unit,"Verified",codes.TryGetValue(x.Unit,out var raw)?app.Db.CodeProtector.Identity(raw):null,null)).ToArray();
    }
    public async Task<IReadOnlyList<VerifiedLabel>> DownloadLabelsAsync(FbsWorkflowContext context,IReadOnlyList<FbsUnitKey> eligibleUnits,CancellationToken ct)
    {
        app.RequireWorkflowAuthorization(context);if(WorkflowIdentity.Snapshot(await ReadSnapshotAsync(context.Snapshot.Target,ct))!=WorkflowIdentity.Snapshot(context.Snapshot))throw new InvalidOperationException("snapshot_changed");
        var assigned=await ReadAssignedCodesAsync(context,ct);var bound=app.Db.BoundScopedKiz(context);var legal=await app.WorkflowEligibility.VerifyAsync(context,bound,ct);var store=Store(context.Snapshot.Target);var result=new List<VerifiedLabel>();
        foreach(var group in context.Snapshot.Units.GroupBy(x=>x.Unit.OrderId)){
            if(group.Any(x=>!eligibleUnits.Contains(x.Unit)||!legal.Any(v=>v.Unit==x.Unit&&v.Stage=="Ready")||x.RequiresKiz&&(!assigned.TryGetValue(x.Unit,out var raw)||!bound.TryGetValue(x.Unit,out var held)||app.Db.CodeProtector.Identity(raw)!=app.Db.CodeProtector.Identity(held))))continue;
            app.RequireWorkflowAuthorization(context);LabelResult label;
            if(marketplace==Marketplace.Wildberries)label=await app.Api.DownloadLabelAsync(store,group.Key,ct);
            else label=await app.ExportVerifiedMarketplaceLabelAsync(store,group.Key,item=>context.Snapshot.Units.Single(x=>x.Unit.OrderId==group.Key&&x.Unit.ItemId==item.Id&&x.Unit.UnitIndex==0).Gtin,ct,context);
            app.RequireWorkflowAuthorization(context);if(!label.Success||string.IsNullOrWhiteSpace(label.FilePath))continue;
            var remoteTask=marketplace==Marketplace.Ozon?app.Db.GetOrCreateOzonLabelJob(store,group.Key).TaskId:group.Key;
            result.Add(new(group.Key,label,group.Select(x=>x.Unit).ToArray(),marketplace+":official-label",Hash(remoteTask+"|"+WorkflowIdentity.Snapshot(context.Snapshot))));
        }return result;
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
