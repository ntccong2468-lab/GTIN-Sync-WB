using MarketplaceHub.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    private readonly SemaphoreSlim marketplaceFbsOperations=new(1,1);

    public async Task<PriceUpdateResult> ReceiveMarketplaceFbsAsync(StoreProfile store,IEnumerable<string> orderIds,
        CancellationToken ct=default,IProgress<string>? progress=null,string? batchId=null)
    {
        if(store.Marketplace is not (Marketplace.Ozon or Marketplace.Yandex))return new(false,"Luồng nhận đơn này chỉ dành cho Ozon/Yandex.");
        var ids=orderIds.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if(ids.Length==0)return new(false,"Chưa chọn đơn mới.");
        var accepted=new List<string>();var rejected=new List<string>();var index=0;
        foreach(var id in ids) {
            ct.ThrowIfCancellationRequested();index++;progress?.Report($"{store.Marketplace} · kiểm tra đơn {index}/{ids.Length}");
            try {
                var fresh=await ReadFreshMarketplaceFbsAsync(store,id,ct).ConfigureAwait(false);
                if(!fresh.CanPack||fresh.IsPacked){rejected.Add(id+": trạng thái hiện tại không thể nhận vào lượt đóng gói");continue;}
                Db.UpsertOrders(store.Id,store.Marketplace,fresh.Rows);accepted.Add(id);
            } catch(Exception ex){rejected.Add(id+": "+ex.Message);}
        }
        if(accepted.Count==0)return new(false,string.Join(Environment.NewLine,rejected.Take(8)));
        var batch=Db.CreateMarketplaceFbsBatch(store,accepted,batchId);
        var message=$"Đã nhận {accepted.Count}/{ids.Length} đơn vào {batch.Name}. Chuyển sang Đang đóng gói để mở shipment và xuất nhãn.";
        if(rejected.Count>0)message+="\nChưa nhận: "+string.Join("; ",rejected.Take(5));
        return new(rejected.Count==0,message,batch.Id);
    }

    public async Task<PriceUpdateResult> PackMarketplaceFbsAsync(StoreProfile store,IEnumerable<string> orderIds,
        Func<MarketplaceFbsItem,string> resolveGtin,bool useKiz=true,CancellationToken ct=default,IProgress<string>? progress=null,
        string? batchId=null,IReadOnlyDictionary<string,JsonArray>? layouts=null,FbsWorkflowContext? workflowContext=null)
    {
        if(!await marketplaceFbsOperations.WaitAsync(0,ct).ConfigureAwait(false))return new(false,"Đang có một lượt đóng hàng Ozon/Yandex. Hãy chờ hoàn tất.");
        string? activeBatch=batchId;string? activeOrder=null;var done=0;
        try {
            if(!License.CanRunFbsWorkflow().Allowed)return new(false,"License đã ký còn hiệu lực là bắt buộc để đóng hàng.");
            if(store.Marketplace is not (Marketplace.Ozon or Marketplace.Yandex))return new(false,"Luồng này chỉ dành cho Ozon/Yandex.");
            if(string.IsNullOrWhiteSpace(batchId))return new(false,"workflow_context_required: chọn batch đã nhận trước khi đóng hàng.");
            if(orderIds.Any())return new(false,"Không thêm đơn mới trong lượt xuất nhãn. Hãy nhận đơn trước.");
            if(workflowContext is null)workflowContext=await PrepareWorkflowContextAsync(new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,batchId),new Fbs.FbsLabelAdapter(this,store.Marketplace,marketGtin:resolveGtin),ct);
            RequireWorkflowAuthorization(workflowContext);
            if(workflowContext.Snapshot.Target!=new LabelTarget(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,batchId))return new(false,"workflow_scope_mismatch");
            var batch=Db.CreateMarketplaceFbsBatch(store,Array.Empty<string>(),batchId);activeBatch=batch.Id;
            var members=Db.MarketplaceFbsBatchOrders(store,batch.Id);
            if(members.Count==0)throw new InvalidOperationException("Lượt FBS chưa có đơn.");
            foreach(var saved in members) {
                ct.ThrowIfCancellationRequested();activeOrder=saved.Id;
                progress?.Report($"{store.Marketplace} · đọc toàn bộ đơn {saved.Id}…");
                var snapshot=await ReadFreshMarketplaceFbsAsync(store,saved.Id,ct).ConfigureAwait(false);
                RequireWorkflowAuthorization(workflowContext);
                var demands=workflowContext.Snapshot.Units.Where(x=>x.Unit.OrderId==saved.Id).ToArray();
                if(snapshot.Items.Sum(x=>x.Quantity)!=demands.Length||snapshot.Items.Any(i=>demands.Count(x=>x.Unit.ItemId==i.Id)!=i.Quantity||demands.Any(x=>x.Unit.ItemId==i.Id&&(x.Sku!=i.Offer||x.RequiresKiz!=i.RequiresKiz||i.RequiresKiz&&x.Gtin!=resolveGtin(i)||x.MappingVersion!=Db.WorkflowMappingVersion(store,i.Offer)))))throw new InvalidOperationException("snapshot_changed");
                var scoped=Db.BoundScopedKiz(workflowContext);var legal=await WorkflowEligibility.VerifyAsync(workflowContext,scoped,ct);RequireWorkflowAuthorization(workflowContext);
                if(demands.Any(x=>!legal.Any(p=>p.Unit==x.Unit&&p.Stage=="Ready")))continue; // Only whole ready postings may mutate in a partial batch.
                if(!snapshot.CanPack && !snapshot.IsPacked)throw new InvalidOperationException("Đơn không sẵn sàng hoặc đã hủy. Chưa cấp KIZ/đóng hàng.");
                if(!snapshot.IsPacked && Db.MarketplaceShipAlreadySubmitted(store,saved.Id))throw new InvalidOperationException("Lệnh đóng hàng đã gửi trước đó cho đơn này. Đối soát trạng thái trên sàn; tạo lượt mới không gửi lại lệnh.");
                var reservations=Db.MarketplaceKizReservations(store,saved.Id);
                if(reservations.Any(r=>!snapshot.Items.Any(i=>i.Id==r.ItemId && r.Unit<i.Quantity)))
                    throw new InvalidOperationException("Đơn đã đổi item/số lượng sau khi giữ mã. Đối soát KIZ trước khi tiếp tục.");
                var codes=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
                var gtins=new Dictionary<string,string>(StringComparer.Ordinal);
                var remote=await Api.ReadMarketplaceKizAsync(store,snapshot,ct).ConfigureAwait(false);
                JsonArray? layout=null;
                if(store.Marketplace==Marketplace.Yandex && snapshot.CanPack) {
                    if(layouts?.TryGetValue(saved.Id,out var supplied)==true)layout=(JsonArray)supplied.DeepClone();
                    else if(saved.Layout.Length>0) {
                        var persisted=JsonNode.Parse(saved.Layout)!;
                        if(persisted["fingerprint"]?.ToString()!=snapshot.ItemFingerprint)throw new InvalidOperationException("Phân bổ hộp đã lưu không còn khớp toàn bộ đơn hiện tại.");
                        layout=(JsonArray)persisted["boxes"]!.DeepClone();
                    }
                    var planned=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
                    foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz)) {
                        var held=reservations.Where(r=>r.ItemId==item.Id).OrderBy(r=>r.Unit).ToArray();
                        var old=remote.Codes.TryGetValue(item.Id,out var current)?current:Array.Empty<string>();
                        // identifiers/status may return CIS in a different order. The saved unit reservation determines layout positions.
                        if(old.Count==item.Quantity && held.Length==item.Quantity && old.Select(MarketplaceFbsPayloads.NormalizeCode).ToHashSet(StringComparer.Ordinal).SetEquals(held.Select(r=>MarketplaceFbsPayloads.NormalizeCode(r.Code))))old=held.Select(r=>r.Code).ToArray();
                        planned[item.Id]=old.Count==item.Quantity?old:Enumerable.Range(0,item.Quantity).Select(u=>held.FirstOrDefault(r=>r.Unit==u)?.Code??$"plan:{item.Id}:{u}").ToArray();
                    }
                    var proposed=MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,planned,layout);
                    layout=(JsonArray)proposed["boxes"]!.DeepClone();
                    foreach(var box in layout)foreach(var item in box?["items"] as JsonArray??new())foreach(var instance in item?["instances"] as JsonArray??new())
                        if(instance?["cis"]?.ToString().StartsWith("plan:",StringComparison.Ordinal)==true)((JsonObject)instance).Remove("cis");
                    Db.SaveMarketplaceFbsOrder(store,batch.Id,saved.Id,saved.Status,layout:new JsonObject{["fingerprint"]=snapshot.ItemFingerprint,["boxes"]=layout.DeepClone()}.ToJsonString());
                }
                foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz)) {
                    if(!useKiz)throw new InvalidOperationException("Đơn bắt buộc KIZ. Bật tự động KIZ trước khi đóng hàng.");
                    var gtin=resolveGtin(item);
                    if(gtin.Length!=14 || ProductCatalog.NormalizeGtin(gtin)!=gtin)throw new InvalidOperationException(item.Offer+": chưa có GTIN đúng SKU/biến thể. Đồng bộ sản phẩm và kiểm tra barcode.");
                    gtins[item.Id]=gtin;
                    var current=remote.Codes.TryGetValue(item.Id,out var old)?old:Array.Empty<string>();
                    if(current.Count>0 && current.Count!=item.Quantity)throw new InvalidOperationException("Sàn trả KIZ thiếu/thừa đơn vị. Đối soát trước khi thay mã.");
                    if(current.Count==0 && snapshot.IsPacked)throw new InvalidOperationException("Đơn đã đóng nhưng không đọc được KIZ hiện tại. Không cấp mã thay thế.");
                    var reserved=reservations.Where(r=>r.ItemId==item.Id).OrderBy(r=>r.Unit).ToArray();
                    if(current.Count>0) {
                        if(reserved.Length==item.Quantity && current.Select(MarketplaceFbsPayloads.NormalizeCode).ToHashSet(StringComparer.Ordinal).SetEquals(reserved.Select(r=>MarketplaceFbsPayloads.NormalizeCode(r.Code))))current=reserved.Select(r=>r.Code).ToArray();
                        else if(reserved.Length==0)current=current.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                    }
                    var selected=new List<string>();
                    for(var unit=0;unit<item.Quantity;unit++) {
                        var remoteCode=current.Count>0?current[unit]:null;
                        if(remoteCode is not null)CheckMarketplaceGtin(remoteCode,gtin,item.Offer);
                        var key=new FbsUnitKey(store.Id,store.Marketplace,saved.Id,item.Id,unit);if(!scoped.TryGetValue(key,out var held))throw new InvalidOperationException("scoped_binding_required");if(remoteCode is not null&&Db.CodeProtector.Identity(remoteCode)!=Db.CodeProtector.Identity(held))throw new InvalidOperationException("remote_binding_conflict");
                        var code=Db.ReserveMarketplaceKiz(store,saved.Id,item.Id,unit,gtin,held)??throw new InvalidOperationException("Kho KIZ không còn đủ mã khả dụng.");
                        CheckMarketplaceGtin(code,gtin,item.Offer);selected.Add(code);
                    }
                    codes[item.Id]=selected;
                }
                if(codes.Values.SelectMany(x=>x).Select(MarketplaceFbsPayloads.NormalizeCode).Distinct(StringComparer.Ordinal).Count()!=codes.Values.Sum(x=>x.Count))throw new InvalidOperationException("KIZ bị trùng giữa các đơn vị hàng.");
                var matching=MarketplaceCodesMatch(snapshot,codes,remote);
                if(snapshot.CanPack && (!matching || store.Marketplace==Marketplace.Yandex)) {
                    progress?.Report($"{saved.Id} · gửi KIZ và phân bổ đủ hàng…");
                    PriceUpdateResult submitted;
                    RequireWorkflowAuthorization(workflowContext);
                    try {submitted=store.Marketplace==Marketplace.Ozon
                        ?await PrepareDurableOzonKizAsync(store,snapshot,codes,ct).ConfigureAwait(false)
                        :await Api.PrepareMarketplaceFbsKizAsync(store,snapshot,codes,layout,ct).ConfigureAwait(false);}
                    catch(Exception ex){submitted=new(false,ex.Message);}
                    remote=await Api.ReadMarketplaceKizAsync(store,snapshot,ct).ConfigureAwait(false);
                    RequireWorkflowAuthorization(workflowContext);
                    matching=MarketplaceCodesMatch(snapshot,codes,remote);
                    if(!matching || !submitted.Success && (codes.Count==0 || store.Marketplace==Marketplace.Yandex))throw new InvalidOperationException(submitted.Message+" Mã đã giữ được bảo toàn; chưa đóng hoặc xuất nhãn.");
                }
                if(!matching)throw new InvalidOperationException("Sàn chưa xác nhận đúng từng KIZ đã giữ. Không đóng hàng hoặc xuất nhãn.");
                foreach(var item in codes)for(var unit=0;unit<item.Value.Count;unit++)Db.ConfirmMarketplaceKiz(store,saved.Id,item.Key,unit,gtins[item.Key],item.Value[unit]);
                var fresh=await ReadFreshMarketplaceFbsAsync(store,saved.Id,ct).ConfigureAwait(false);
                if(fresh.ItemFingerprint!=snapshot.ItemFingerprint)throw new InvalidOperationException("Danh sách hàng đã đổi trên sàn; dừng trước khi đóng đơn.");
                var freshProof=await Api.ReadMarketplaceKizAsync(store,fresh,ct).ConfigureAwait(false);
                RequireWorkflowAuthorization(workflowContext);
                if(!MarketplaceCodesMatch(fresh,codes,freshProof) || fresh.Items.Where(x=>x.RequiresKiz).Any(x=>!gtins.TryGetValue(x.Id,out var oldGtin) || resolveGtin(x)!=oldGtin))
                    throw new InvalidOperationException("Requirements/GTIN/KIZ đã đổi trên sàn. Chưa gửi lệnh đóng hàng.");
                if(!fresh.IsPacked) {
                    if(!fresh.CanPack)throw new InvalidOperationException("Trạng thái hoặc requirements mới không cho phép đóng hàng.");
                    if(saved.Status is "SUBMITTING" or "AMBIGUOUS")throw new InvalidOperationException("Lệnh đóng hàng trước chưa rõ kết quả. Đối soát trên sàn; ứng dụng không tự gửi lại.");
                    if(!Db.TryBeginMarketplaceShip(store,saved.Id))throw new InvalidOperationException("Đơn này đã gửi lệnh đóng hàng. Chưa tự gửi lại.");
                    Db.SaveMarketplaceFbsOrder(store,batch.Id,saved.Id,"SUBMITTING");
                    RequireWorkflowAuthorization(workflowContext);
                    var packed=await Api.ConfirmMarketplaceFbsAsync(store,fresh,ct).ConfigureAwait(false);
                    RequireWorkflowAuthorization(workflowContext);
                    if(!packed.Success){Db.SaveMarketplaceFbsOrder(store,batch.Id,saved.Id,"AMBIGUOUS",packed.Message);return new(false,packed.Message,batch.Id);}
                    fresh=await ReadFreshMarketplaceFbsAsync(store,saved.Id,ct).ConfigureAwait(false);
                }
                if(!fresh.IsPacked || fresh.ItemFingerprint!=snapshot.ItemFingerprint)throw new InvalidOperationException("Chưa xác nhận toàn bộ đơn đã đóng trên sàn.");
                Db.UpsertOrders(store.Id,store.Marketplace,fresh.Rows);Db.SaveMarketplaceFbsOrder(store,batch.Id,saved.Id,saved.Status=="LABELS_READY"?"LABELS_READY":"PACKED");done++;
            }
            return new(true,$"{store.Marketplace} xác nhận {done} đơn đã đóng, đủ hàng và KIZ; sẵn sàng xuất nhãn.",activeBatch);
        }catch(Exception ex) {
            var message=ex is OperationCanceledException?"Đã tạm dừng; lượt đóng hàng và KIZ đã giữ vẫn còn.":ex.Message;
            if(activeBatch is not null && activeOrder is not null) {
                var state=Db.MarketplaceFbsBatchOrders(store,activeBatch).FirstOrDefault(x=>x.Id==activeOrder).Status;
                Db.SaveMarketplaceFbsOrder(store,activeBatch,activeOrder,state is "SUBMITTING" or "AMBIGUOUS"?"AMBIGUOUS":"ERROR",message);
            }
            return new(false,message,activeBatch);
        }finally {marketplaceFbsOperations.Release();}
    }

    public async Task<MarketplaceFbsSnapshot> ReadFreshMarketplaceFbsAsync(StoreProfile store,string orderId,CancellationToken ct=default)
    {
        var generation=Db.StoreGeneration(store.Id);
        var snapshot=await Api.ReadMarketplaceFbsAsync(store,orderId,ct).ConfigureAwait(false);
        if(!Db.StoreGenerationMatches(store.Id,generation))throw new InvalidOperationException("store_deleted");
        Db.MarkOrderRemoteState(store.Id,store.Marketplace,orderId,snapshot.Rows[0].Status,snapshot.CancelRequested?"cancel_requested":"");
        var reserved=Db.MarketplaceKizReservations(store,orderId).Select(x=>x.ItemId).ToHashSet(StringComparer.Ordinal);
        return snapshot with{Items=snapshot.Items.Select(x=>x with{RequiresKiz=x.RequiresKiz || reserved.Contains(x.Id)}).ToArray()};
    }
    public static bool MarketplaceCodesMatch(MarketplaceFbsSnapshot snapshot,IReadOnlyDictionary<string,IReadOnlyList<string>> expected,MarketplaceKizState remote)
    {
        return (expected.Count==0 || remote.Verified) && snapshot.Items.Where(x=>x.RequiresKiz).All(item=>expected.TryGetValue(item.Id,out var codes)
            && codes.Count==item.Quantity && remote.Codes.TryGetValue(item.Id,out var actual) && actual.Count==codes.Count
            && actual.Distinct(StringComparer.Ordinal).Count()==actual.Count && actual.Select(MarketplaceFbsPayloads.NormalizeCode).ToHashSet(StringComparer.Ordinal).SetEquals(codes.Select(MarketplaceFbsPayloads.NormalizeCode)));
    }
    private static void CheckMarketplaceGtin(string code,string gtin,string offer)
    {var parsed=ParseKiz(code);if(!parsed.Ok || parsed.Gtin!=gtin)throw new InvalidOperationException(offer+": GTIN trong KIZ khác biến thể sản phẩm.");}

    public async Task<LabelResult> ExportVerifiedMarketplaceLabelAsync(StoreProfile store,string orderId,Func<MarketplaceFbsItem,string> resolveGtin,CancellationToken ct=default,FbsWorkflowContext? workflowContext=null,bool artifactRecovery=false)
    {
        try {
            if(!License.CanRunFbsWorkflow().Allowed)return new(false,"License đã ký còn hiệu lực là bắt buộc để xuất nhãn.");
            if(workflowContext is null){var candidates=Db.TodayMarketplaceFbsBatches(store).Where(b=>Db.MarketplaceFbsBatchOrders(store,b.Id).Any(x=>x.Id==orderId)).ToArray();if(candidates.Length!=1)return new(false,"workflow_context_required: mở đúng batch đã nhận.");workflowContext=await PrepareWorkflowContextAsync(new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,candidates[0].Id),new Fbs.FbsLabelAdapter(this,store.Marketplace,marketGtin:resolveGtin),ct);}
            RequireWorkflowAuthorization(workflowContext,requireActive:!artifactRecovery);
            if(workflowContext.Snapshot.Target.StoreId!=store.Id||!workflowContext.Snapshot.Units.Any(x=>x.Unit.OrderId==orderId))return new(false,"workflow_scope_mismatch");
            var current=await ReadFreshMarketplaceFbsAsync(store,orderId,ct).ConfigureAwait(false);
            RequireWorkflowAuthorization(workflowContext,requireActive:!artifactRecovery);
            if(!current.IsPacked)return new(false,"Đơn chưa được sàn xác nhận đã đóng hoặc đang yêu cầu hủy.");
            var remote=await Api.ReadMarketplaceKizAsync(store,current,ct).ConfigureAwait(false);
            var expected=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach(var item in current.Items.Where(x=>x.RequiresKiz)) {
                var gtin=resolveGtin(item);var reserved=Db.MarketplaceKizReservations(store,orderId).Where(x=>x.ItemId==item.Id).ToArray();
                if(reserved.Length!=item.Quantity || reserved.Any(x=>x.Gtin!=gtin || x.Status!="ASSIGNED"))return new(false,"KIZ chưa được đối soát đủ từng đơn vị. Bấm Nhận/đóng gói để kiểm tra lại.");
                expected[item.Id]=reserved.OrderBy(x=>x.Unit).Select(x=>x.Code).ToArray();foreach(var code in expected[item.Id])CheckMarketplaceGtin(code,gtin,item.Offer);
            }
            if(!MarketplaceCodesMatch(current,expected,remote))return new(false,"Mã hiện tại trên sàn khác mã đã xác nhận. Chưa xuất nhãn.");
            var scoped=Db.BoundScopedKiz(workflowContext);var legal=await WorkflowEligibility.VerifyAsync(workflowContext,scoped,ct);RequireWorkflowAuthorization(workflowContext,requireActive:!artifactRecovery);
            if(workflowContext.Snapshot.Units.Where(x=>x.Unit.OrderId==orderId).Any(x=>!legal.Any(v=>v.Unit==x.Unit&&v.Stage=="Ready")))return new(false,"KIZ cần legal proof và xác nhận đã dán mã.");
            if(store.Marketplace==Marketplace.Ozon){if(artifactRecovery&&string.IsNullOrWhiteSpace(Db.GetOrCreateOzonLabelJob(store,orderId).TaskId))return new(false,"label_task_reconciliation_required");return await DownloadDurableOzonLabelAsync(store,orderId,ct).ConfigureAwait(false);}
            return await Api.DownloadLabelAsync(store,orderId,ct).ConfigureAwait(false);
        }catch(Exception ex){return new(false,ex.Message);}
    }

    private async Task<LabelResult> DownloadDurableOzonLabelAsync(StoreProfile store,string postingNumber,CancellationToken ct)
    {
        var job=Db.GetOrCreateOzonLabelJob(store,postingNumber);
        if((job.Status is "CREATE_PENDING" or "RECONCILE_REQUIRED") && string.IsNullOrWhiteSpace(job.TaskId))
            return new(false,"Kết quả tạo job nhãn Ozon trước đó chưa rõ. Mở bảng kiểm tra API/đối soát trên Ozon; ứng dụng không tự tạo job thứ hai.");
        var taskId=job.TaskId;
        if(string.IsNullOrWhiteSpace(taskId))
        {
            if(!Db.TryBeginOzonLabelCreate(store,postingNumber))
                return new(false,"Job nhãn Ozon đã được một luồng khác bắt đầu hoặc cần đối soát. Ứng dụng không tạo job thứ hai.");
            try
            {
                taskId=await Api.CreateOzonLabelTaskAsync(store,new[]{postingNumber},ct).ConfigureAwait(false);
                Db.SaveOzonLabelJob(store,postingNumber,taskId,"POLLING");
            }
            catch(OperationCanceledException){Db.SaveOzonLabelJob(store,postingNumber,"","RECONCILE_REQUIRED","cancelled_during_create");throw;}
            catch(Exception){Db.SaveOzonLabelJob(store,postingNumber,"","RECONCILE_REQUIRED","create_outcome_unknown");return new(false,"Chưa xác nhận được kết quả tạo job nhãn Ozon. Không tự gửi lại; hãy dùng bảng kiểm tra API để đối soát.");}
        }
        var label=await Api.DownloadOzonLabelTaskAsync(store,postingNumber,taskId,ct).ConfigureAwait(false);
        Db.SaveOzonLabelJob(store,postingNumber,taskId,label.Success?"READY":label.Message.Contains("đang tạo",StringComparison.OrdinalIgnoreCase)?"POLLING":"FAILED",label.Success?"":"label_not_ready");
        return label;
    }

    private async Task<PriceUpdateResult> PrepareDurableOzonKizAsync(StoreProfile store,MarketplaceFbsSnapshot snapshot,
        IReadOnlyDictionary<string,IReadOnlyList<string>> codes,CancellationToken ct)
    {
        var canonical=string.Join("\n",codes.OrderBy(x=>x.Key,StringComparer.Ordinal).SelectMany(x=>x.Value.Select((code,index)=>
            x.Key+":"+index+":"+MarketplaceFbsPayloads.NormalizeCode(code))));
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.ItemFingerprint+"\n"+canonical))).ToLowerInvariant();
        var existing=Db.OzonExemplarMutation(store,snapshot.OrderId);
        if(existing is not null)
        {
            if(existing.Fingerprint!=fingerprint)return new(false,"Danh sách hàng/KIZ khác checkpoint exemplar đã gửi. Dừng để đối soát trên Ozon.");
            if(existing.State=="VERIFIED")return new(true,"Ozon đã xác thực KIZ/exemplar ở lần trước.",snapshot.OrderId);
            return new(false,"Kết quả gửi KIZ/exemplar Ozon trước đó chưa rõ. Không tự gửi lại; hãy đối soát trạng thái exemplar.");
        }
        var required=snapshot.Items.Where(x=>x.RequiresKiz).ToArray();
        if(required.Select(x=>x.Offer).Distinct(StringComparer.Ordinal).Count()!=required.Length)
            return new(false,"Ozon có nhiều product ID cùng offer_id. Dừng để đối soát ánh xạ KIZ; chưa gửi mutation.");
        var claimed=false;
        try
        {
            var byOffer=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz))
            {
                if(!codes.TryGetValue(item.Id,out var unitCodes))throw new InvalidOperationException("Thiếu KIZ cho item Ozon.");
                byOffer[item.Offer]=unitCodes;
            }
            var result=await Api.PrepareOzonKizAsync(store,snapshot.OrderId,byOffer,ct,()=>
            {
                claimed=Db.TryBeginOzonExemplarMutation(store,snapshot.OrderId,fingerprint);return claimed;
            }).ConfigureAwait(false);
            if(claimed)Db.SaveOzonExemplarMutation(store,snapshot.OrderId,fingerprint,result.Success?"VERIFIED":"RECONCILE_REQUIRED",result.Success?"":"submit_not_verified");
            return result.Success?result:claimed
                ?new(false,result.Message+" Checkpoint được giữ; ứng dụng không tự gửi mutation lần hai.")
                :result;
        }
        catch(OperationCanceledException)
        {
            if(claimed)Db.SaveOzonExemplarMutation(store,snapshot.OrderId,fingerprint,"RECONCILE_REQUIRED","cancelled_during_submit");throw;
        }
        catch(Exception)
        {
            if(claimed)Db.SaveOzonExemplarMutation(store,snapshot.OrderId,fingerprint,"RECONCILE_REQUIRED","submit_outcome_unknown");
            return new(false,"Kết quả gửi KIZ/exemplar Ozon chưa rõ. Không tự gửi lại; hãy đối soát trạng thái exemplar.");
        }
    }
}
