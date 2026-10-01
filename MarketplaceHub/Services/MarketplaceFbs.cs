using MarketplaceHub.Core;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed record MarketplaceFbsItem(string Id,string Offer,string Name,int Quantity,bool RequiresKiz,JsonObject Raw);
public sealed record MarketplaceKizState(IReadOnlyDictionary<string,IReadOnlyList<string>> Codes,bool Verified);
public sealed record MarketplaceFbsSnapshot(long StoreId,Marketplace Marketplace,string OrderId,string Status,string Substatus,
    IReadOnlyList<MarketplaceFbsItem> Items,JsonObject Raw,bool CancelRequested,string BlockingRequirements)
{
    public bool CanPack => !CancelRequested && BlockingRequirements.Length==0 && (Marketplace==Marketplace.Ozon
        ? Status=="awaiting_packaging" : Status=="PROCESSING" && Substatus=="STARTED");
    public bool IsPacked => !CancelRequested && (Marketplace==Marketplace.Ozon
        ? new[]{"awaiting_deliver","delivering","delivered"}.Contains(Status)
        : Status=="PROCESSING" && Substatus=="READY_TO_SHIP" || new[]{"DELIVERY","PICKUP","DELIVERED"}.Contains(Status));
    public IReadOnlyList<FbsOrderRow> Rows => Items.Select(x=>new FbsOrderRow(StoreId,Marketplace,OrderId,x.Offer,x.Name,x.Quantity,
        Marketplace==Marketplace.Yandex?Status+"/"+Substatus:Status,x.RequiresKiz,Raw.ToJsonString())).ToArray();
    public string ItemFingerprint => string.Join("|",Items.OrderBy(x=>x.Id,StringComparer.Ordinal).Select(x=>$"{x.Id}:{x.Offer}:{x.Quantity}"));
}

public static class MarketplaceFbsPayloads
{
    public static JsonObject BuildOzonPackage(MarketplaceFbsSnapshot snapshot)
    {
        if(snapshot.Marketplace!=Marketplace.Ozon)throw new InvalidOperationException("Posting này không thuộc Ozon.");
        var products=new JsonArray();
        foreach(var item in snapshot.Items)
            products.Add(new JsonObject{["product_id"]=long.Parse(item.Id),["quantity"]=item.Quantity});
        return new JsonObject{["posting_number"]=snapshot.OrderId,["packages"]=new JsonArray(new JsonObject{["products"]=products}),
            ["with"]=new JsonObject{["additional_data"]=true}};
    }

    // Layout contains API request items (fullCount or partialCount), never inferred from box IDs alone.
    public static JsonObject BuildYandexBoxes(MarketplaceFbsSnapshot snapshot,IReadOnlyDictionary<string,IReadOnlyList<string>> codes,
        JsonArray? explicitLayout=null)
    {
        if(snapshot.Marketplace!=Marketplace.Yandex)throw new InvalidOperationException("Đơn này không thuộc Yandex.");
        var allCodes=codes.Values.SelectMany(x=>x).Select(NormalizeCode).ToArray();
        if(allCodes.Any(string.IsNullOrWhiteSpace) || allCodes.Distinct(StringComparer.Ordinal).Count()!=allCodes.Length)
            throw new InvalidOperationException("Mỗi đơn vị hàng cần một KIZ riêng. Phát hiện mã trống hoặc mã trùng.");
        var expected=snapshot.Items.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        foreach(var key in codes.Keys)if(!expected.ContainsKey(key))throw new InvalidOperationException("KIZ trỏ tới item không có trong đơn hiện tại.");
        foreach(var item in snapshot.Items)
            if(item.RequiresKiz && (!codes.TryGetValue(item.Id,out var unitCodes) || unitCodes.Count!=item.Quantity))
                throw new InvalidOperationException($"{snapshot.OrderId}/{item.Offer}: cần đúng {item.Quantity} KIZ.");
        JsonArray boxes;
        if(explicitLayout is not null)boxes=(JsonArray)explicitLayout.DeepClone();
        else
        {
            var existing=new JsonArray();
            foreach(var shipment in snapshot.Raw["delivery"]?["shipments"] as JsonArray ?? new JsonArray())
                foreach(var box in shipment?["boxes"] as JsonArray ?? new JsonArray())existing.Add(box?.DeepClone());
            if(existing.Count>0 && existing.Any(x=>x?["items"] is not JsonArray))
                throw new InvalidOperationException("Yandex đã có hộp nhưng API không trả phân bổ item. Hãy nhập layout đầy đủ trước khi tiếp tục; ứng dụng không gộp hoặc xóa hộp.");
            boxes=existing.Count>0?existing:new JsonArray(new JsonObject{["items"]=new JsonArray(snapshot.Items.Select(x=>(JsonNode)new JsonObject{
                ["id"]=long.Parse(x.Id),["fullCount"]=x.Quantity,["instances"]=x.Raw["instances"]?.DeepClone()}).ToArray())});
        }
        if(boxes.Count==0)throw new InvalidOperationException("Layout phải có ít nhất một hộp.");
        var totals=snapshot.Items.ToDictionary(x=>x.Id,_=>0,StringComparer.Ordinal);
        var offsets=snapshot.Items.ToDictionary(x=>x.Id,_=>0,StringComparer.Ordinal);
        var partials=new Dictionary<string,HashSet<int>>(StringComparer.Ordinal);
        var partialTotals=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var box in boxes)
        {
            if(box is not JsonObject b || b["items"] is not JsonArray boxItems || boxItems.Count==0)
                throw new InvalidOperationException("Mỗi hộp phải có phân bổ item đầy đủ.");
            // The API generates physical box IDs; do not send response-only fields back as a layout.
            foreach(var field in b.Select(x=>x.Key).Where(x=>x!="items").ToArray())b.Remove(field);
            var boxPartial=false;var boxWhole=false;
            foreach(var entry in boxItems)
            {
                if(entry is not JsonObject item || !expected.TryGetValue(item["id"]?.ToString()??"",out var wanted))
                    throw new InvalidOperationException("Hộp chứa item không có trong đơn hiện tại.");
                var id=wanted.Id;
                var quantity=0;
                if(item["partialCount"] is JsonObject part)
                {
                    boxPartial=true;
                    if(item["fullCount"] is not null || wanted.Quantity!=1 || !int.TryParse(part["current"]?.ToString(),out var current)
                        || !int.TryParse(part["total"]?.ToString(),out var total) || total<2 || current<1 || current>total)
                        throw new InvalidOperationException("Layout hàng chia nhiều hộp không đầy đủ hoặc không hỗ trợ nhiều đơn vị cùng item.");
                    if(!partials.TryGetValue(id,out var parts))partials[id]=parts=new();
                    if(!parts.Add(current) || partialTotals.TryGetValue(id,out var previous) && previous!=total)
                        throw new InvalidOperationException("Phân bổ phần hàng bị trùng hoặc khác tổng số hộp.");
                    partialTotals[id]=total;quantity=1;
                }
                else
                {
                    boxWhole=true;
                    if(!int.TryParse(item["fullCount"]?.ToString(),out quantity) || quantity<1)throw new InvalidOperationException("fullCount phải lớn hơn 0.");
                    totals[id]+=quantity;
                }
                if(codes.TryGetValue(id,out var selected))
                {
                    var old=item["instances"] as JsonArray ?? new JsonArray();
                    if(old.Count>quantity)throw new InvalidOperationException("Số instance cũ lớn hơn số đơn vị trong hộp.");
                    var instances=new JsonArray();
                    for(var index=0;index<quantity;index++)
                    {
                        var position=item["partialCount"] is null?offsets[id]++:0;
                        if(position>=selected.Count)throw new InvalidOperationException("Layout dùng nhiều đơn vị hơn số KIZ đã giữ.");
                        var metadata=old.ElementAtOrDefault(index)?.DeepClone() as JsonObject ?? new JsonObject();
                        var oldCode=metadata["cisFull"]?.ToString()??metadata["cis"]?.ToString();
                        var newCode=NormalizeCode(selected[position]);
                        if(!string.IsNullOrWhiteSpace(oldCode) && NormalizeCode(oldCode)!=newCode)
                            throw new InvalidOperationException("Instance hiện tại chứa KIZ khác. Đối soát trước khi thay đổi layout.");
                        metadata.Remove("cisFull");metadata["cis"]=newCode;instances.Add(metadata);
                    }
                    item["instances"]=instances;
                }
                foreach(var field in item.Select(x=>x.Key).Where(x=>!new[]{"id","fullCount","partialCount","instances"}.Contains(x)).ToArray())item.Remove(field);
            }
            if(boxPartial && boxItems.Count!=1)throw new InvalidOperationException("Hộp chứa phần sản phẩm chỉ được có một item.");
            if(boxPartial && boxWhole)throw new InvalidOperationException("Một hộp không được trộn hàng nguyên đơn vị và hàng chia phần.");
        }
        foreach(var item in snapshot.Items)
        {
            if(partials.TryGetValue(item.Id,out var parts))
            {
                if(totals[item.Id]!=0 || parts.Count!=partialTotals[item.Id])throw new InvalidOperationException("Layout thiếu hoặc trùng phần hàng.");
            }
            else if(totals[item.Id]!=item.Quantity)throw new InvalidOperationException($"Layout thiếu/thừa item {item.Offer}; yêu cầu giữ đủ {item.Quantity} đơn vị.");
        }
        return new JsonObject{["boxes"]=boxes,["allowRemove"]=false};
    }

    public static bool YandexCodesAccepted(JsonNode? response,IReadOnlyDictionary<string,IReadOnlyList<string>> expected)
    {
        if(response?["status"]?.ToString()!="OK" || response?["errors"] is JsonArray errors && errors.Count>0)return false;
        var items=response?["result"]?["items"] as JsonArray;
        if(items is null)return expected.Count==0;
        foreach(var item in expected)
        {
            var rows=items.Where(x=>x?["id"]?.ToString()==item.Key).ToArray();
            if(rows.Length!=1 || rows[0]?["cis"] is not JsonArray statuses)return false;
            foreach(var code in item.Value)
            {
                var matching=statuses.Where(x=>NormalizeCode(x?["value"]?.ToString()??"")==NormalizeCode(code)).ToArray();
                if(matching.Length!=1 || matching[0]?["status"]?.ToString()!="OK")return false;
            }
        }
        return true;
    }
    public static string NormalizeCode(string code)=>StripLeadingGs(code.Replace("\\u001d","\u001d",StringComparison.OrdinalIgnoreCase).Replace("<GS>","\u001d",StringComparison.OrdinalIgnoreCase).Trim());
    private static string StripLeadingGs(string code)=>code.StartsWith("\u001d",StringComparison.Ordinal)?code[1..]:code;
}

public sealed partial class MarketplaceGateway
{
    public async Task<MarketplaceKizState> ReadMarketplaceKizAsync(StoreProfile store,MarketplaceFbsSnapshot snapshot,CancellationToken ct=default)
    {
        var codes=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
        if(!snapshot.Items.Any(x=>x.RequiresKiz))return new(codes,true);
        if(store.Marketplace==Marketplace.Yandex) {
            using var request=Request(HttpMethod.Post,$"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{snapshot.OrderId}/identifiers/status",store,"{}");
            var root=await SendMarketplaceFbsJsonAsync(request,store.Marketplace,ct);
            foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz)) {
                var matching=(root["result"]?["items"] as JsonArray ?? new()).Where(x=>x?["id"]?.ToString()==item.Id).ToArray();
                if(matching.Length>1)throw new InvalidDataException("Yandex trả item ID trùng trong trạng thái KIZ.");
                var list=(matching.FirstOrDefault()?["cis"] as JsonArray ?? new()).Select(x=>MarketplaceFbsPayloads.NormalizeCode(x?["value"]?.ToString()??"")).Where(x=>x.Length>0).ToArray();
                codes[item.Id]=list;
            }
            var present=codes.Where(x=>x.Value.Count>0).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            return new(codes,present.Count==0 || MarketplaceFbsPayloads.YandexCodesAccepted(root,present));
        }
        using var detailRequest=Request(HttpMethod.Post,"https://api-seller.ozon.ru/v6/fbs/posting/product/exemplar/create-or-get",store,new JsonObject{["posting_number"]=snapshot.OrderId}.ToJsonString());
        var detail=await SendMarketplaceFbsJsonAsync(detailRequest,store.Marketplace,ct);
        var products=new JsonArray();
        void Collect(JsonNode? node)
        {
            if(node is JsonObject obj) {
                if(obj["product_id"] is not null && obj["exemplars"] is JsonArray exemplars) {
                    var id=obj["product_id"]!.ToString();var item=snapshot.Items.SingleOrDefault(x=>x.Id==id);
                    if(item is not null && item.RequiresKiz) {
                        if(codes.ContainsKey(id))throw new InvalidDataException("Ozon trả trùng product/exemplar.");
                        var list=new List<string>();var proof=new JsonArray();
                        foreach(var exemplar in exemplars) {
                            var marks=exemplar?["marks"] as JsonArray;
                            var values=(marks??new()).Where(x=>x?["mark_type"]?.ToString() is null or "mandatory_mark").Select(x=>MarketplaceFbsPayloads.NormalizeCode(x?["mark"]?.ToString()??"")).Where(x=>x.Length>0).ToArray();
                            if(values.Length>1)throw new InvalidDataException("Ozon trả nhiều KIZ cho một exemplar.");
                            if(values.Length==1) {list.Add(values[0]);proof.Add(new JsonObject{["exemplar_id"]=exemplar?["exemplar_id"]?.DeepClone()});}
                        }
                        codes[id]=list;products.Add(new JsonObject{["product_id"]=obj["product_id"]!.DeepClone(),["exemplars"]=proof});
                    }
                }
                foreach(var child in obj)Collect(child.Value);
            }else if(node is JsonArray arr)foreach(var child in arr)Collect(child);
        }
        Collect(detail);
        string Fingerprint()=>products.ToJsonString()+string.Join("|",codes.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+":"+string.Join(";",x.Value)));
        var before=Fingerprint();
        using var statusRequest=Request(HttpMethod.Post,"https://api-seller.ozon.ru/v5/fbs/posting/product/exemplar/status",store,new JsonObject{["posting_number"]=snapshot.OrderId}.ToJsonString());
        var status=await SendMarketplaceFbsJsonAsync(statusRequest,store.Marketplace,ct);
        using var againRequest=Request(HttpMethod.Post,"https://api-seller.ozon.ru/v6/fbs/posting/product/exemplar/create-or-get",store,new JsonObject{["posting_number"]=snapshot.OrderId}.ToJsonString());
        var again=await SendMarketplaceFbsJsonAsync(againRequest,store.Marketplace,ct);
        codes.Clear();products.Clear();Collect(again);var stable=before==Fingerprint();
        bool HasErrors(JsonNode? n)=>n is JsonObject o ? o.Any(x=>(x.Key=="error_codes" && (x.Value is JsonArray errors?errors.Count>0:x.Value is JsonValue v && v.ToString().Length>0)) || HasErrors(x.Value)) : n is JsonArray arr && arr.Any(HasErrors);
        var reported=CollectStringValues(status,"mark").Select(MarketplaceFbsPayloads.NormalizeCode).Where(x=>x.Length>0).ToArray();
        var allCodes=codes.Values.SelectMany(x=>x).ToHashSet(StringComparer.Ordinal);
        var valuesMatch=reported.All(allCodes.Contains);
        foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz))codes.TryAdd(item.Id,Array.Empty<string>());
        var available=status["ship_available"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true || status["status"]?.ToString()=="ship_available" || status["result"]?["status"]?.ToString()=="ship_available";
        var rejected=OzonHasRejectedExemplar(status) || HasErrors(status);
        return new(codes,stable && valuesMatch && available && !rejected && codes.Values.Any(x=>x.Count>0) && OzonExemplarAccepted(status,products));
    }

    public async Task<MarketplaceFbsSnapshot> ReadMarketplaceFbsAsync(StoreProfile store,string id,CancellationToken ct=default)
    {
        if(store.Marketplace==Marketplace.Wildberries)throw new InvalidOperationException("WB sử dụng shipment riêng.");
        if(string.IsNullOrWhiteSpace(id))throw new InvalidOperationException("Thiếu mã posting/order.");
        if(store.Marketplace==Marketplace.Yandex && (!long.TryParse(store.CampaignId,out var campaign) || campaign<=0 || !long.TryParse(id,out var orderId) || orderId<=0))
            throw new InvalidOperationException("Campaign ID/Order ID Yandex không hợp lệ.");
        using var request=store.Marketplace==Marketplace.Ozon
            ? Request(HttpMethod.Post,"https://api-seller.ozon.ru/v3/posting/fbs/get",store,new JsonObject{["posting_number"]=id,["with"]=new JsonObject{
                ["analytics_data"]=false,["financial_data"]=false,["product_exemplars"]=true}}.ToJsonString())
            : Request(HttpMethod.Get,$"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{id}",store);
        var root=await SendMarketplaceFbsJsonAsync(request,store.Marketplace,ct);
        var raw=root[store.Marketplace==Marketplace.Ozon?"result":"order"] as JsonObject
            ?? throw new InvalidOperationException("Sàn chưa trả chi tiết đơn đầy đủ. Chưa cấp hoặc gửi KIZ.");
        if(raw[store.Marketplace==Marketplace.Ozon?"posting_number":"id"]?.ToString()!=id)
            throw new InvalidOperationException("Sàn trả chi tiết của đơn khác. Dừng để đối soát.");
        var items=new List<MarketplaceFbsItem>();
        var block="";
        var required=new HashSet<string>(StringComparer.Ordinal);
        if(store.Marketplace==Marketplace.Ozon)
        {
            if(raw["requirements"] is not JsonObject requirements)throw new InvalidOperationException("Ozon chưa trả requirements. Chưa cấp hoặc gửi KIZ.");
            foreach(var name in new[]{"products_requiring_mandatory_mark","products_requiring_mark"})
            {
                if(requirements[name] is not null && requirements[name] is not JsonArray)throw new InvalidOperationException("Requirements Ozon không hợp lệ.");
                foreach(var req in requirements[name] as JsonArray ?? new JsonArray())if(req is not null)required.Add(req.ToString());
            }
            if(raw["is_multibox"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true || int.TryParse(raw["multi_box_qty"]?.ToString(),out var boxes) && boxes>1)
                block="Ozon có nhiều hộp/kiện. Phân bổ và đóng posting trên sàn trước; không tự gộp vào một gói.";
            var integration=raw["tpl_integration_type"]?.ToString();
            if(!string.IsNullOrEmpty(integration) && integration!="ozon")block="Posting dùng dịch vụ vận chuyển riêng; hoàn tất đúng luồng trên Ozon trước khi xuất nhãn.";
            if(raw["ship_available"] is not null && raw["ship_available"]!.ToString().Equals("false",StringComparison.OrdinalIgnoreCase))block="Ozon chưa cho phép đóng posting này.";
            foreach(var requirement in requirements.Where(x=>!new[]{"products_requiring_mandatory_mark","products_requiring_mark"}.Contains(x.Key)))
                if(requirement.Value is JsonArray outstanding && outstanding.Count>0)block="Ozon còn yêu cầu "+requirement.Key+". Hoàn tất trên sàn trước khi đóng hàng.";
        }
        var list=raw[store.Marketplace==Marketplace.Ozon?"products":"items"] as JsonArray;
        if(list is null || list.Count==0)throw new InvalidOperationException("Đơn không có danh sách hàng đầy đủ.");
        foreach(var node in list)
        {
            if(node is not JsonObject item)throw new InvalidOperationException("Item trên sàn không hợp lệ.");
            var itemId=store.Marketplace==Marketplace.Ozon?(item["sku"]??item["product_id"])?.ToString():item["id"]?.ToString();
            var offer=item[store.Marketplace==Marketplace.Ozon?"offer_id":"offerId"]?.ToString()??"";
            if(!long.TryParse(itemId,out var numeric) || numeric<=0 || offer.Length==0 || !int.TryParse(item[store.Marketplace==Marketplace.Ozon?"quantity":"count"]?.ToString(),out var quantity) || quantity<=0)
                throw new InvalidOperationException("Item thiếu ID/SKU hoặc số lượng hợp lệ. Không đóng một phần đơn.");
            var needs=store.Marketplace==Marketplace.Ozon?required.Contains(itemId!) || required.Contains(offer)
                : item["hasCis"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true
                    || (item["requiredInstanceTypes"] as JsonArray)?.Any(x=>x?.ToString() is "CIS" or "CIS_FULL")==true;
            if(store.Marketplace==Marketplace.Yandex)
                foreach(var type in item["requiredInstanceTypes"] as JsonArray ?? new JsonArray())
                    if(type?.ToString() is not ("CIS" or "CIS_FULL") && (item["instances"] as JsonArray)?.Count!=quantity)
                        block="Yandex yêu cầu "+type+" chưa có đủ instance. Hoàn tất thông tin trước khi đóng hàng.";
            items.Add(new(itemId!,offer,item[store.Marketplace==Marketplace.Ozon?"name":"offerName"]?.ToString()??offer,quantity,needs,(JsonObject)item.DeepClone()));
        }
        if(items.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=items.Count)throw new InvalidOperationException("Sàn trả item ID trùng. Chưa xử lý đơn.");
        if(store.Marketplace==Marketplace.Ozon && required.Any(x=>!items.Any(item=>item.Id==x || item.Offer==x)))
            throw new InvalidOperationException("Ozon yêu cầu KIZ cho sản phẩm không có trong chi tiết posting.");
        return new(store.Id,store.Marketplace,id,raw["status"]?.ToString()??"",raw["substatus"]?.ToString()??"",items,(JsonObject)raw.DeepClone(),
            raw["cancelRequested"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true,block);
    }

    private async Task<JsonObject> SendMarketplaceFbsJsonAsync(HttpRequestMessage request,Marketplace marketplace,CancellationToken ct)
    {
        using var response=await http.SendAsync(request,ct);
        var body=await response.Content.ReadAsStringAsync(ct);
        Ensure(response,body);
        var root=JsonNode.Parse(body) as JsonObject??throw new InvalidOperationException("Sàn trả JSON không hợp lệ.");
        if(root["errors"] is JsonArray errors && errors.Count>0 || marketplace==Marketplace.Yandex && root["status"]?.ToString()=="ERROR")
            throw new InvalidOperationException("Sàn từ chối thao tác FBS. Kiểm tra thông tin đơn và quyền API.");
        return root;
    }

    public async Task<PriceUpdateResult> PrepareMarketplaceFbsKizAsync(StoreProfile store,MarketplaceFbsSnapshot snapshot,
        IReadOnlyDictionary<string,IReadOnlyList<string>> codes,JsonArray? layout=null,CancellationToken ct=default)
    {
        if(!snapshot.CanPack)return new(false,"Trạng thái/requirements của đơn không cho phép đóng hàng.");
        if(store.Marketplace==Marketplace.Ozon)
        {
            if(snapshot.Items.Select(x=>x.Offer).Distinct(StringComparer.Ordinal).Count()!=snapshot.Items.Count)
                return new(false,"Ozon có nhiều product ID cùng offer_id. Dừng để đối soát ánh xạ KIZ.");
            var byOffer=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach(var item in snapshot.Items.Where(x=>x.RequiresKiz))
            {
                if(!codes.TryGetValue(item.Id,out var unitCodes) || unitCodes.Count!=item.Quantity)return new(false,"Số KIZ không khớp từng đơn vị trong posting.");
                byOffer[item.Offer]=unitCodes;
            }
            return await PrepareOzonKizAsync(store,snapshot.OrderId,byOffer,ct);
        }
        var body=MarketplaceFbsPayloads.BuildYandexBoxes(snapshot,codes,layout);
        using var request=Request(HttpMethod.Put,$"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{snapshot.OrderId}/boxes",store,body.ToJsonString());
        var response=await SendMarketplaceFbsJsonAsync(request,store.Marketplace,ct);
        if(response["status"]?.ToString()!="OK")return new(false,"Yandex chưa xác nhận layout hộp.");
        if(codes.Count>0)
        {
            for(var attempt=0;attempt<12;attempt++)
            {
                using var statusRequest=Request(HttpMethod.Post,$"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{snapshot.OrderId}/identifiers/status",store,"{}");
                var status=await SendMarketplaceFbsJsonAsync(statusRequest,store.Marketplace,ct);
                if(MarketplaceFbsPayloads.YandexCodesAccepted(status,codes))return new(true,"Yandex đã xác minh đúng từng KIZ của đơn.",snapshot.OrderId);
                if(CollectStringValues(status["result"],"status").Any(x=>x is "INVALID" or "FAILED"))return new(false,"Yandex từ chối KIZ. Giữ nguyên mã đã dành cho đơn để đối soát.");
                await wbLabelDelay(TimeSpan.FromSeconds(1),ct);
            }
            return new(false,"Yandex chưa xác minh đủ từng KIZ. Chưa chuyển READY_TO_SHIP.");
        }
        return new(true,"Yandex đã nhận layout toàn bộ đơn.",snapshot.OrderId);
    }

    public async Task<PriceUpdateResult> ConfirmMarketplaceFbsAsync(StoreProfile store,MarketplaceFbsSnapshot snapshot,CancellationToken ct=default)
    {
        if(snapshot.StoreId!=store.Id || snapshot.Marketplace!=store.Marketplace)return new(false,"Đơn không thuộc cửa hàng hiện tại.");
        if(snapshot.IsPacked)return new(true,"Sàn đã xác nhận đóng hàng; không gửi lại lệnh.",snapshot.OrderId);
        if(!snapshot.CanPack)return new(false,"Trạng thái hiện tại không cho phép đóng hàng.",snapshot.OrderId);
        try
        {
            using var request=store.Marketplace==Marketplace.Ozon
                ? Request(HttpMethod.Post,"https://api-seller.ozon.ru/v4/posting/fbs/ship",store,MarketplaceFbsPayloads.BuildOzonPackage(snapshot).ToJsonString())
                : Request(HttpMethod.Put,$"https://api.partner.market.yandex.ru/v2/campaigns/{store.CampaignId}/orders/{snapshot.OrderId}/status",store,
                    new JsonObject{["order"]=new JsonObject{["status"]="PROCESSING",["substatus"]="READY_TO_SHIP"}}.ToJsonString());
            await SendMarketplaceFbsJsonAsync(request,store.Marketplace,ct);
            for(var attempt=0;attempt<4;attempt++)
            {
                var readback=await ReadMarketplaceFbsAsync(store,snapshot.OrderId,ct);
                if(readback.IsPacked && readback.ItemFingerprint==snapshot.ItemFingerprint)return new(true,"Sàn đã xác nhận toàn bộ đơn đóng hàng.",snapshot.OrderId);
                if(!readback.CanPack)return new(false,"Trạng thái đã thay đổi; hãy đối soát trước khi tiếp tục.",snapshot.OrderId);
                await wbLabelDelay(TimeSpan.FromSeconds(1),ct);
            }
            return new(false,"Lệnh đã gửi nhưng sàn chưa xác nhận đóng hàng. Không gửi lại tự động; hãy đối soát trạng thái.",snapshot.OrderId);
        }
        catch(Exception)
        {
            // A lost mutation response may still have succeeded. Only a fresh platform state can release labels.
            try{var readback=await ReadMarketplaceFbsAsync(store,snapshot.OrderId,ct);if(readback.IsPacked && readback.ItemFingerprint==snapshot.ItemFingerprint)return new(true,"Đã đối soát: sàn xác nhận đóng hàng.",snapshot.OrderId);}catch{}
            return new(false,"Kết quả lệnh đóng hàng chưa được xác nhận. Hãy đối soát; ứng dụng không tự gửi lại.",snapshot.OrderId);
        }
    }
}
