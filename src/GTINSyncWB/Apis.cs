using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace GTINSyncWB;
public sealed class ApiFailure(string message,int status=0,bool unknownOutcome=false) : Exception(message)
{
    public int Status {get;}=status;
    public bool UnknownOutcome {get;}=unknownOutcome;
}
public sealed class RequestGate(TimeSpan interval)
{
    private readonly SemaphoreSlim mutex=new(1,1);
    private DateTimeOffset next;
    public async Task Enter(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var delay=next-DateTimeOffset.UtcNow;
            if(delay>TimeSpan.Zero)await Task.Delay(delay,ct);
            next=DateTimeOffset.UtcNow+interval;
        }
        finally{mutex.Release();}
    }
}
public sealed class ApiTransport
{
    private readonly HttpClient http;
    public ApiTransport(HttpMessageHandler? handler=null)
    {
        http=handler==null?new HttpClient():new HttpClient(handler);
        http.Timeout=TimeSpan.FromSeconds(35);
    }
    public async Task<JsonNode> Send(HttpRequestMessage request,CancellationToken ct,bool isWrite=false)
    {
        for(var attempt=0;attempt<5;attempt++)
        {
            using var message=Clone(request);
            try
            {
                using var response=await http.SendAsync(message,ct);
                if(response.StatusCode==HttpStatusCode.TooManyRequests || (!isWrite && response.StatusCode==HttpStatusCode.ServiceUnavailable))
                {
                    if(attempt==4)throw new ApiFailure("API tạm giới hạn số yêu cầu; có thể thử lại",(int)response.StatusCode);
                    var retry=response.Headers.RetryAfter;
                    var seconds=Math.Clamp(retry?.Delta?.TotalSeconds ?? (retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds ?? Math.Pow(2,attempt+1),1,300);
                    await Task.Delay(TimeSpan.FromSeconds(seconds),ct);continue;
                }
                if(!response.IsSuccessStatusCode)
                {
                    var code=(int)response.StatusCode;
                    throw new ApiFailure(code switch {401=>"Khóa API không hợp lệ hoặc đã hết quyền",403=>"Không có quyền truy cập thẻ hoặc cửa hàng",413=>"Giới hạn số bản ghi/kích thước yêu cầu",_=>$"API trả lỗi HTTP {code}"},code,isWrite && code>=500);
                }
                var data=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new ApiFailure("Phản hồi API trống");
                if(data is JsonObject o && o["error"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true)throw new ApiFailure("API báo lỗi xử lý; mở tài khoản seller để xem chi tiết an toàn");
                return data;
            }
            catch(TaskCanceledException) when(!ct.IsCancellationRequested){throw new ApiFailure("Kết nối API quá thời gian",0,isWrite);}
            catch(HttpRequestException){throw new ApiFailure("Mất kết nối API",0,isWrite);}
        }
        throw new ApiFailure("API chưa thể phục vụ");
    }
    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var next=new HttpRequestMessage(original.Method,original.RequestUri);
        foreach(var h in original.Headers)next.Headers.TryAddWithoutValidation(h.Key,h.Value);
        if(original.Content!=null)next.Content=new StringContent(original.Content.ReadAsStringAsync().GetAwaiter().GetResult(),System.Text.Encoding.UTF8,"application/json");
        return next;
    }
}
public sealed class CatalogApi(ApiTransport transport)
{
    private const string Base="https://апи.национальный-каталог.рф";
    private readonly RequestGate gate=new(TimeSpan.FromSeconds(1));
    private async Task<JsonNode> Get(string path,string key,CancellationToken ct)
    {
        // NK requires apikey in the query. No request URI or response body is ever logged.
        using var req=new HttpRequestMessage(HttpMethod.Get,Base+path+(path.Contains('?')?'&':'?')+"apikey="+Uri.EscapeDataString(key)+"&format=json");
        await gate.Enter(ct);
        return await transport.Send(req,ct);
    }
    public Task<JsonNode> Check(string key,CancellationToken ct)=>Get("/v4/product-list?limit=1&offset=0&good_status=published&from_date=2020-01-01%2000%3A00%3A00&to_date=2020-01-02%2000%3A00%3A00",key,ct);
    public async Task<List<CatalogItem>> Read(string key,DateTime from,DateTime to,IProgress<string>? progress,CancellationToken ct)
    {
        var summaries=new Dictionary<string,JsonNode>();
        for(var start=from.Date;start<to;start=start.AddDays(30))
        {
            var end=start.AddDays(30)<to?start.AddDays(30):to;
            await Window(start,end,0,summaries,key,progress,ct);
        }
        var goods=new List<CatalogItem>();var keys=summaries.Keys.ToList();
        for(var i=0;i<keys.Count;i+=25)
        {
            ct.ThrowIfCancellationRequested();
            var slice=keys.Skip(i).Take(25).ToArray();
            try
            {
                var data=await Get("/v3/feed-product?gtins="+Uri.EscapeDataString(string.Join(';',slice)),key,ct);
                foreach(var item in data["result"] as JsonArray ?? new JsonArray())AddRequested(goods,item!,slice);
                foreach(var missing in slice.Except(goods.Select(x=>x.Gtin)))goods.Add(new(missing,"",Json.S(summaries[missing],"good_name"),"","","","unavailable",DateTimeOffset.UtcNow,"NK: "+missing,false));
            }
            catch(ApiFailure e) when(e.Status==403)
            {
                // Isolate individual inaccessible cards so other cards remain usable.
                foreach(var gtin in slice)
                {
                    try{var data=await Get("/v3/feed-product?gtin="+Uri.EscapeDataString(gtin),key,ct);foreach(var item in data["result"] as JsonArray ?? new JsonArray())AddRequested(goods,item!,[gtin]);}
                    catch(ApiFailure individual) when(individual.Status is 403 or 404){goods.Add(new(gtin,"",Json.S(summaries[gtin],"good_name"),"","","","restricted",DateTimeOffset.UtcNow,"NK: "+gtin,false));}
                }
            }
            progress?.Report($"Đang đọc dữ liệu: {Math.Min(i+25,keys.Count)}/{keys.Count} thẻ");
        }
        return goods;
    }
    private async Task Window(DateTime start,DateTime end,int depth,Dictionary<string,JsonNode> target,string key,IProgress<string>? progress,CancellationToken ct)
    {
        var collected=new List<JsonNode>();
        try
        {
            for(var offset=0;;offset+=1000)
            {
                var q=$"/v4/product-list?from_date={Uri.EscapeDataString(start.ToString("yyyy-MM-dd HH:mm:ss"))}&to_date={Uri.EscapeDataString(end.ToString("yyyy-MM-dd HH:mm:ss"))}&good_status=published&limit=1000&offset={offset}";
                var result=(await Get(q,key,ct))["result"] ?? throw new ApiFailure("Danh sách NK thiếu result");
                var total=int.TryParse(Json.S(result,"total"),out var t)?t:0;
                if(total>=10000)throw new ApiFailure("Cửa sổ dữ liệu vượt giới hạn 10000",413);
                var batch=Json.A(result,"goods");foreach(var item in batch)if(item!=null)collected.Add(item.DeepClone());
                if(batch.Count==0 || offset+batch.Count>=total)break;
                if(offset+1000>=10000)throw new ApiFailure("Cửa sổ dữ liệu vượt giới hạn 10000",413);
            }
        }
        catch(ApiFailure e) when(e.Status==413 && depth<16 && (end-start)>TimeSpan.FromMinutes(2))
        {
            var mid=start+(end-start)/2;await Window(start,mid,depth+1,target,key,progress,ct);await Window(mid,end,depth+1,target,key,progress,ct);return;
        }
        foreach(var item in collected){var gtin=Json.S(item,"gtin");if(gtin!="")target[gtin]=item;}
        progress?.Report($"Đang đọc dữ liệu: {target.Count} GTIN");
    }
    private static void AddRequested(List<CatalogItem> goods,JsonNode item,IReadOnlyCollection<string> requested)
    {
        var identifiers=Json.A(item,"identified_by").Where(x=>Json.S(x,"type").Equals("gtin",StringComparison.OrdinalIgnoreCase) && requested.Contains(Json.S(x,"value"))).Select(x=>Json.S(x,"value")).Distinct().ToList();
        if(identifiers.Count==0 && requested.Contains(Json.S(item,"gtin")))identifiers.Add(Json.S(item,"gtin"));
        foreach(var id in identifiers)if(!goods.Any(g=>g.Gtin==id))goods.Add(Parse(item,DateTimeOffset.UtcNow,id));
    }
    public static CatalogItem Parse(JsonNode item,DateTimeOffset at,string? requestedGtin=null)
    {
        var attrs=Json.A(item,"good_attrs");
        string Attr(params string[] names)=>attrs.FirstOrDefault(a=>names.Any(n=>Json.S(a,"attr_name").Equals(n,StringComparison.OrdinalIgnoreCase)))?["attr_value"]?.ToString()??"";
        var identifier=(requestedGtin==null?null:Json.A(item,"identified_by").FirstOrDefault(x=>Json.S(x,"type").Equals("gtin",StringComparison.OrdinalIgnoreCase) && Json.S(x,"value")==requestedGtin))
            ??Json.A(item,"identified_by").FirstOrDefault(x=>Json.S(x,"type").Equals("gtin",StringComparison.OrdinalIgnoreCase) && Json.S(x,"level")=="trade-unit")
            ??Json.A(item,"identified_by").FirstOrDefault(x=>Json.S(x,"type").Equals("gtin",StringComparison.OrdinalIgnoreCase));
        var id=identifier?["value"]?.ToString()??Json.S(item,"gtin");
        return new(id,Attr("Артикул","Артикул производителя","Модель","Код модели","Код товара"),Json.S(item,"good_name"),Json.S(item,"brand_name"),Attr("Цвет","Основной цвет"),Attr("Размер","Размер изделия","Размер товара"),Json.S(item,"good_status"),at,"НК: "+id,true,Json.S(identifier,"level")=="trade-unit");
    }
}
public sealed class WbApi(ApiTransport transport):IWriteGateway
{
    private const string Base="https://content-api.wildberries.ru";
    private readonly RequestGate readGate=new(TimeSpan.FromMilliseconds(650));
    private readonly RequestGate writeGate=new(TimeSpan.FromSeconds(6));
    private async Task<JsonNode> Post(string path,string token,JsonNode data,CancellationToken ct,bool write=false)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,Base+path);
        req.Headers.TryAddWithoutValidation("Authorization",token);
        req.Content=JsonContent.Create(data);
        await (write?writeGate:readGate).Enter(ct);
        return await transport.Send(req,ct,write);
    }
    public Task<JsonNode> Check(string token,CancellationToken ct)=>Post("/content/v2/get/cards/list",token,new JsonObject{["settings"]=new JsonObject{["cursor"]=new JsonObject{["limit"]=1}}},ct);
    public async Task<List<Listing>> Read(Shop shop,string token,CancellationToken ct,IProgress<int>? progress=null)
    {
        var cards=new List<Listing>();JsonNode? cursor=null;
        for(var page=0;page<10000;page++)
        {
            var c=new JsonObject{["limit"]=100};
            if(cursor!=null){c["updatedAt"]=cursor["updatedAt"]?.DeepClone();c["nmID"]=cursor["nmID"]?.DeepClone();}
            var data=await Post("/content/v2/get/cards/list",token,new JsonObject{["settings"]=new JsonObject{["cursor"]=c}},ct);
            var inner=data["cards"]!=null?data:data["data"];
            var batch=Json.A(inner,"cards");
            foreach(var card in batch.OfType<JsonObject>())cards.Add(Parse(shop,card));
            progress?.Report(cards.Count);
            if(batch.Count<100)break;
            var next=inner?["cursor"];
            if(next==null || Json.L(next,"nmID")==Json.L(cursor,"nmID"))throw new ApiFailure("Phân trang WB thiếu cursor tiếp theo");
            cursor=next.DeepClone();
            if(page==9999)throw new ApiFailure("Chưa đọc hết phân trang WB; không dùng danh sách chưa hoàn tất");
        }
        return cards;
    }
    public async Task<Listing> ReadOne(Shop shop,string token,long nmId,CancellationToken ct)
    {
        var data=await Post("/content/v2/get/cards/list",token,new JsonObject{["settings"]=new JsonObject{["filter"]=new JsonObject{["textSearch"]=nmId.ToString()},["cursor"]=new JsonObject{["limit"]=100}}},ct);
        var inner=data["cards"]!=null?data:data["data"];
        var card=Json.A(inner,"cards").OfType<JsonObject>().SingleOrDefault(x=>Json.L(x,"nmID")==nmId);
        return card==null?throw new ApiFailure("Không đọc lại được đúng nmID; dừng ghi"):Parse(shop,card);
    }
    public async Task Update(string token,JsonArray payload,CancellationToken ct)
    {
        if(payload.Count!=1 || System.Text.Encoding.UTF8.GetByteCount(payload.ToJsonString())>10_000_000)throw new ApiFailure("Lô cập nhật WB vượt giới hạn an toàn");
        await Post("/content/v2/cards/update",token,payload,ct,true);
    }
    public async Task<string> Errors(string token,long nmId,CancellationToken ct)
    {
        var data=await Post("/content/v2/cards/error/list",token,new JsonObject{["cursor"]=new JsonObject{["limit"]=100},["order"]=new JsonObject{["ascending"]=false}},ct);
        var errors=Json.A(data["data"]??data,"items");
        foreach(var item in errors)if(item?.ToJsonString().Contains(nmId.ToString(),StringComparison.Ordinal)==true)return "WB ghi nhận lỗi xử lý thẻ; mở danh sách lỗi trong tài khoản WB để xem chi tiết";
        return "";
    }
    private static Listing Parse(Shop shop,JsonObject card)
    {
        var photo=Json.A(card,"photos").FirstOrDefault()?["big"]?.ToString()??"";
        return new(shop.Id,shop.Name,Json.L(card,"nmID"),Json.S(card,"vendorCode"),Json.S(card,"title"),"",photo,(JsonObject)card.DeepClone(),DateTimeOffset.UtcNow);
    }
}
