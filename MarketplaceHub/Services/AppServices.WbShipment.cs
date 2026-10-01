using MarketplaceHub.Core;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    private readonly SemaphoreSlim wbKizOperations=new(1,1);

    public async Task<WbReceiveResult> ReceiveWbOrdersAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> orders,
        WbShipmentChoice choice,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        if(store.Marketplace!=Marketplace.Wildberries)return new(false,null,false,0,Array.Empty<WbReceiveOrderResult>(),"Chỉ nhận shipment WB cho cửa hàng Wildberries.");
        var unique=orders.GroupBy(x=>x.ExternalOrderId,StringComparer.Ordinal).Select(x=>x.First()).ToArray();
        if(unique.Length==0)return new(false,null,false,0,Array.Empty<WbReceiveOrderResult>(),"Chưa chọn đơn mới.");
        var result=await Api.ReceiveWbShipmentAsync(store,unique,choice.SupplyId,choice.Name,ct,progress).ConfigureAwait(false);
        var observed=DateTimeOffset.UtcNow;
        foreach(var order in result.Orders) {
            if(order.Disposition==WbReceiveDisposition.Cancelled)Db.MarkOrderRemoteState(store.Id,store.Marketplace,order.OrderId,"new","cancelled",true,observed);
            else if(order.Verified)Db.MarkOrderRemoteState(store.Id,store.Marketplace,order.OrderId,"confirm","waiting",true,observed);
            else if(order.Disposition==WbReceiveDisposition.Rejected)Db.MarkOrderRemoteState(store.Id,store.Marketplace,order.OrderId,"","",false,observed);
        }
        if(!string.IsNullOrWhiteSpace(result.SupplyId)) {
            try {
                var supply=await Api.GetWbSupplyAsync(store,result.SupplyId,ct).ConfigureAwait(false);
                var members=await Api.GetWbSupplyOrderIdsAsync(store,supply.Id,ct).ConfigureAwait(false);
                Db.UpsertWbSupply(store.Id,supply,members.Count);Db.StoreWbSupplyMembership(store.Id,supply.Id,members);
            } catch(Exception ex) {return result with{Success=false,Message=result.Message+" Đã tạo/giữ shipment nhưng chưa đọc đủ membership: "+ex.Message};}
        }
        return result;
    }

    public async Task RefreshWbSuppliesAsync(StoreProfile store,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        var rows=await Api.GetWbSuppliesAsync(store,ct).ConfigureAwait(false);var index=0;
        foreach(var supply in rows) {
            ct.ThrowIfCancellationRequested();index++;progress?.Report($"WB · shipment {index}/{rows.Count}");int? count=null;
            try {var ids=await Api.GetWbSupplyOrderIdsAsync(store,supply.Id,ct).ConfigureAwait(false);count=ids.Count;Db.StoreWbSupplyMembership(store.Id,supply.Id,ids);}
            catch(OperationCanceledException){throw;}catch(Exception ex){Db.Audit("FBS WB","Chưa đọc đủ shipment "+supply.Id,ex.Message);}
            Db.UpsertWbSupply(store.Id,supply,count);
        }
    }

    public async Task<IReadOnlyList<FbsOrderRow>> ReadWbSupplyOrdersAsync(StoreProfile store,string supplyId,CancellationToken ct=default)
    {
        var ids=await Api.GetWbSupplyOrderIdsAsync(store,supplyId,ct).ConfigureAwait(false);
        Db.StoreWbSupplyMembership(store.Id,supplyId,ids);
        var local=Db.Orders(store.Id).Where(x=>x.Marketplace==Marketplace.Wildberries).ToDictionary(x=>x.ExternalOrderId,StringComparer.Ordinal);
        if(ids.Any(id=>!local.ContainsKey(id))) {
            var synced=await SyncOrdersAsync(store,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(!synced.Ok)Db.Audit("FBS WB","Chưa tải đủ chi tiết shipment",synced.Message);
            local=Db.Orders(store.Id).Where(x=>x.Marketplace==Marketplace.Wildberries).ToDictionary(x=>x.ExternalOrderId,StringComparer.Ordinal);
        }
        var missing=ids.Where(id=>!local.ContainsKey(id)).ToArray();
        foreach(var id in missing)local[id]=new(store.Id,Marketplace.Wildberries,id,"","Chưa tải được chi tiết sản phẩm · đồng bộ lại đơn",0,"unknown",true,"{\"unresolved\":true}");
        var statuses=await Api.GetWbOrderStatusesAsync(store,ids,ct).ConfigureAwait(false);
        var rows=ids.Select(id=>{
            var order=local[id];var raw=JsonNode.Parse(order.RawJson)??new JsonObject();raw["supplyId"]=supplyId;raw["wbStatus"]=statuses[id].WbStatus;
            return order with{Status=statuses[id].SupplierStatus,RawJson=raw.ToJsonString()};
        }).ToArray();
        Db.UpsertOrders(store.Id,Marketplace.Wildberries,rows.Where(x=>!missing.Contains(x.ExternalOrderId)).ToArray());return rows;
    }

    public async Task<PriceUpdateResult> EnsureWbSupplyKizAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> orders,
        IReadOnlyDictionary<string,string> gtinByOrder,bool useKiz,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        await wbKizOperations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(store.Marketplace!=Marketplace.Wildberries || orders.Any(x=>x.StoreId!=store.Id || x.Marketplace!=Marketplace.Wildberries))
                return new(false,"Các đơn KIZ phải thuộc cùng cửa hàng WB.");
            if(orders.Any(x=>string.IsNullOrWhiteSpace(Db.FindWbSupplyForOrder(store.Id,x.ExternalOrderId))))
                return new(false,"Hãy thêm mọi đơn vào shipment trước khi gắn KIZ.");
            var ids=orders.Select(x=>x.ExternalOrderId).Distinct().ToArray();
            var statuses=await Api.GetWbOrderStatusesAsync(store,ids,ct).ConfigureAwait(false);
            if(ids.Any(id=>statuses[id].SupplierStatus is not ("confirm" or "complete") || statuses[id].WbStatus.Contains("cancel",StringComparison.OrdinalIgnoreCase)))
                return new(false,"WB chưa xác nhận mọi đơn đang đóng gói. Chưa gắn KIZ.");
            var metadata=await Api.GetWbPrintKizAsync(store,ids,ct,progress).ConfigureAwait(false);
            var fresh=orders.Select(x=>x with{Status=statuses[x.ExternalOrderId].SupplierStatus}).ToArray();
            var validation=ValidateWbSupplyKiz(store,fresh,gtinByOrder,metadata,true);
            if(!validation.Success)return validation;
            var pending=new List<(FbsOrderRow Order,string Gtin,string? Reserved)>();
            var expected=new Dictionary<string,string>(StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var order in orders)
            {
                var meta=metadata[order.ExternalOrderId];
                if(!order.NeedsKiz && !meta.Required)continue;
                if(!useKiz)return new(false,$"Đơn {order.ExternalOrderId} bắt buộc KIZ. Hãy bật tự động KIZ.");
                if(!gtinByOrder.TryGetValue(order.ExternalOrderId,out var gtin) || gtin.Length!=14 || !gtin.All(char.IsDigit))
                    return new(false,$"{order.ExternalOrderId}: chưa có GTIN đúng size/SKU. Hãy đồng bộ sản phẩm trước khi gắn KIZ.");
                if(order.Quantity!=1)return new(false,$"{order.ExternalOrderId}: nhiệm vụ WB cần một sản phẩm/một mã KIZ; hãy đồng bộ lại số lượng.");
                if(meta.Codes.Count>0) {
                    if(meta.Codes.Count!=1 || !seen.Add(meta.Codes[0]))return new(false,"WB trả thiếu/trùng KIZ giữa các đơn. Chưa xuất nhãn.");
                    CheckWbKizGtin(meta.Codes[0],gtin,order.ExternalOrderId);
                    Db.ConfirmWbKiz(store.Id,order.ExternalOrderId,gtin,meta.Codes[0]);
                    expected[order.ExternalOrderId]=meta.Codes[0];continue;
                }
                if(statuses[order.ExternalOrderId].SupplierStatus=="complete")return new(false,$"{order.ExternalOrderId}: đơn đã giao nhưng thiếu KIZ xác nhận. Không thay mã của shipment đã đóng.");
                pending.Add((order,gtin,Db.WbReservedKizForOrder(store.Id,order.ExternalOrderId,gtin)));
            }
            foreach(var group in pending.Where(x=>x.Reserved is null).GroupBy(x=>x.Gtin))
            {
                progress?.Report($"Chuẩn bị {group.Count()} KIZ cho GTIN {group.Key}…");
                var ensured=await EnsureKizQuantityAsync(store.Id,group.First().Order.Sku,group.Key,group.Count(),ct).ConfigureAwait(false);
                if(!ensured.Ok)return new(false,ensured.Message);
            }
            foreach(var batch in pending.Chunk(100))
            {
                foreach(var item in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var code=Db.ReserveWbKiz(store.Id,item.Order.ExternalOrderId,item.Gtin)
                        ??throw new InvalidOperationException($"{item.Order.ExternalOrderId}: không còn KIZ sẵn sàng.");
                    CheckWbKizGtin(code,item.Gtin,item.Order.ExternalOrderId);
                    if(!seen.Add(code))throw new InvalidOperationException("KIZ bị trùng giữa các đơn.");
                    progress?.Report($"Gắn KIZ: {expected.Count}/{pending.Count} · đơn {item.Order.ExternalOrderId}…");
                    var attached=await Api.AttachWbSgtinAsync(store,item.Order.ExternalOrderId,code,ct).ConfigureAwait(false);
                    if(!attached.Success)return new(false,$"{item.Order.ExternalOrderId}: {attached.Message} Mã đã được giữ cho đơn này; thử lại sẽ kiểm tra WB trước, không lấy mã mới.");
                    expected[item.Order.ExternalOrderId]=code;
                }
                var readback=await Api.GetWbPrintKizAsync(store,batch.Select(x=>x.Order.ExternalOrderId),ct,progress).ConfigureAwait(false);
                foreach(var item in batch)
                {
                    var remote=readback[item.Order.ExternalOrderId].Codes;
                    if(remote.Count!=1 || remote[0]!=expected[item.Order.ExternalOrderId])
                        return new(false,$"{item.Order.ExternalOrderId}: WB chưa xác nhận đúng KIZ đã gửi. Mã vẫn được giữ; chưa xuất nhãn.");
                    Db.ConfirmWbKiz(store.Id,item.Order.ExternalOrderId,item.Gtin,remote[0]);
                }
            }
            return new(true,$"WB xác nhận KIZ cho {expected.Count} đơn; sẵn sàng xuất bộ nhãn.");
        }
        catch(OperationCanceledException){return new(false,"Đã dừng gắn KIZ. Shipment và mã đã giữ vẫn còn để tiếp tục.");}
        catch(Exception ex){return new(false,ex.Message);}
        finally {wbKizOperations.Release();}
    }

    private static void CheckWbKizGtin(string code,string gtin,string orderId)
    {
        var parsed=ParseKiz(code);
        if(!parsed.Ok || parsed.Gtin!=gtin)throw new InvalidOperationException($"{orderId}: GTIN trong KIZ không khớp biến thể sản phẩm. Chưa gửi/in mã.");
    }

    public IReadOnlyDictionary<string,string> WbSupplyKizErrors(StoreProfile store,IReadOnlyList<FbsOrderRow> orders,
        IReadOnlyDictionary<string,string> gtinByOrder,IReadOnlyDictionary<string,WbPrintKizMetadata> metadata,bool allowMissing=false)
    {
        var errors=new Dictionary<string,string>(StringComparer.Ordinal);
        var duplicated=metadata.Values.SelectMany(x=>x.Codes).GroupBy(x=>x,StringComparer.Ordinal).Where(x=>x.Count()>1).Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
        foreach(var order in orders)try {
            if(store.Marketplace!=Marketplace.Wildberries || order.StoreId!=store.Id || order.Marketplace!=Marketplace.Wildberries)
                throw new InvalidOperationException("Đơn không thuộc cửa hàng WB đang chọn.");
            if(order.Status is not ("confirm" or "complete") || JsonNode.Parse(order.RawJson)?["wbStatus"]?.ToString().Contains("cancel",StringComparison.OrdinalIgnoreCase)==true)
                throw new InvalidOperationException("Đơn chưa sẵn sàng hoặc đã hủy trên WB.");
            if(!metadata.TryGetValue(order.ExternalOrderId,out var marking))throw new InvalidOperationException("Chưa đọc được KIZ hiện tại trên WB.");
            if(!order.NeedsKiz && !marking.Required && marking.Codes.Count==0)continue;
            if(!gtinByOrder.TryGetValue(order.ExternalOrderId,out var gtin) || gtin.Length!=14 || !gtin.All(char.IsDigit))
                throw new InvalidOperationException("Chưa có GTIN đúng size/SKU.");
            if(order.Quantity!=1)throw new InvalidOperationException("Nhiệm vụ WB phải có đúng một sản phẩm/một mã.");
            if(marking.Codes.Count==0 && allowMissing)continue;
            if(marking.Codes.Count!=1)throw new InvalidOperationException("Chưa có đúng một KIZ được WB xác nhận.");
            var code=marking.Codes[0];
            if(duplicated.Contains(code))throw new InvalidOperationException("KIZ bị trùng giữa các đơn.");
            CheckWbKizGtin(code,gtin,order.ExternalOrderId);
            Db.ValidateWbKizOwnership(store.Id,order.ExternalOrderId,gtin,code);
        } catch(Exception ex){errors[order.ExternalOrderId]=ex.Message;}
        return errors;
    }

    public PriceUpdateResult ValidateWbSupplyKiz(StoreProfile store,IReadOnlyList<FbsOrderRow> orders,
        IReadOnlyDictionary<string,string> gtinByOrder,IReadOnlyDictionary<string,WbPrintKizMetadata> metadata,bool allowMissing=false)
    {
        var errors=WbSupplyKizErrors(store,orders,gtinByOrder,metadata,allowMissing);
        return errors.Count==0?new(true,"KIZ đúng GTIN, không trùng và đúng chủ sở hữu."):
            new(false,string.Join("\n",errors.Take(8).Select(x=>x.Key+": "+x.Value)));
    }

    public async Task<PriceUpdateResult> DeliverVerifiedWbSupplyAsync(StoreProfile store,string supplyId,
        IReadOnlyDictionary<string,string> gtinByOrder,CancellationToken ct=default)
    {
        try {
            var supply=await Api.GetWbSupplyAsync(store,supplyId,ct).ConfigureAwait(false);
            if(supply.Done)return new(false,"Shipment đã chuyển sang giao hàng; không giao lại.");
            var orders=await ReadWbSupplyOrdersAsync(store,supplyId,ct).ConfigureAwait(false);
            if(orders.Count==0 || orders.Any(x=>x.Status!="confirm"))return new(false,"Mọi đơn phải đang đóng gói trên WB trước khi giao shipment.");
            var metadata=await Api.GetWbPrintKizAsync(store,orders.Select(x=>x.ExternalOrderId),ct).ConfigureAwait(false);
            var verified=ValidateWbSupplyKiz(store,orders,gtinByOrder,metadata);
            if(!verified.Success)return verified;
            return await Api.DeliverSupplyAsync(store,supplyId,ct).ConfigureAwait(false);
        }catch(Exception ex){return new(false,ex.Message);}
    }
}
