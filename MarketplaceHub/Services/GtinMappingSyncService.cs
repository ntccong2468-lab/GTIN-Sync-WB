using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Services;

public sealed class GtinMappingSyncService(AppDatabase db,MarketplaceGateway api,HttpClient nationalHttp,
    Func<TimeSpan,CancellationToken,Task>? delay=null)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Locks=new();
    private SemaphoreSlim Gate(StoreProfile store)=>Locks.GetOrAdd(db.DbPath+"#"+store.Id+"#"+store.Marketplace,_=>new(1,1));
    private readonly Func<TimeSpan,CancellationToken,Task> pause=delay??Task.Delay;
    private static GtinSyncTarget Target(GtinMappingRow x)=>new(x.Sku,x.VariantId,x.ExternalId,x.Size,x.Gtin);
    private IReadOnlyList<GtinMappingRow> Rows(StoreProfile store)
    {
        var rows=new List<GtinMappingRow>();for(var offset=0;;offset+=50){var page=db.GetGtinMappingPage(store,offset);rows.AddRange(page.Rows);if(offset+50>=page.Total)return rows;}
    }

    public void QueueWbGtinWriteback(StoreProfile store)
    {
        if(store.Marketplace!=Marketplace.Wildberries)throw new InvalidOperationException("Cập nhật GTIN lên card chỉ dành cho WB.");
        var gate=Gate(store);if(!gate.Wait(0))throw new InvalidOperationException("Đồng bộ đang chạy; chờ hoàn tất trước khi đưa lượt mới vào hàng đợi.");
        try{
            var old=db.GetGtinSyncJob(store,"WB_WRITEBACK");if(old is not null&&old.State is not ("COMPLETE" or "STALE_MAPPING"))return;
            var targets=Rows(store).Where(x=>x.Confirmed&&x.WbStage!="VERIFIED").Select(Target).ToArray();
            if(targets.Any(x=>GtinCode.Normalize(x.Gtin)!=x.Gtin||!long.TryParse(x.ExternalId,out var nm)||nm<=0||!long.TryParse(x.VariantId,out var chrt)||chrt<=0))
                throw new InvalidOperationException("Mapping cần GTIN checksum hợp lệ cùng nmID/chrtID chính xác trước khi đưa lên WB.");
            db.SaveGtinSyncJob(store,"WB_WRITEBACK",new(ProductCatalog.Scope(store),JsonSerializer.Serialize(targets),0,targets.Length==0?"COMPLETE":"QUEUED",null,""));
        }finally{gate.Release();}
    }

    public void QueueWbGtinWritebackForVariant(StoreProfile store,string sku,string variantId)
    {
        if(store.Marketplace!=Marketplace.Wildberries)throw new InvalidOperationException("Thao tác này chỉ dành cho cửa hàng WB đã chọn.");
        var gate=Gate(store);if(!gate.Wait(0))throw new InvalidOperationException("Đang có tác vụ đồng bộ; chưa tạo lượt thử mới.");
        try{
            var row=Rows(store).SingleOrDefault(x=>x.Sku==sku&&x.VariantId==variantId);
            if(row is null||!row.Confirmed||GtinCode.Normalize(row.Gtin)!=row.Gtin||row.Gtin.Length==0)
                throw new InvalidOperationException("Mục tiêu thử phải là đúng một biến thể với GTIN đã xác nhận.");
            var target=Target(row);var old=db.GetGtinSyncJob(store,"WB_WRITEBACK");
            if(old is not null&&old.State!="COMPLETE"){
                var pending=JsonSerializer.Deserialize<GtinSyncTarget[]>(old.SnapshotJson)??Array.Empty<GtinSyncTarget>();
                if(old.Scope!=ProductCatalog.Scope(store)||pending.Length!=1||pending[0]!=target)
                    throw new InvalidOperationException("Có lượt WB khác chưa đối soát. Không dùng thao tác thử một biến thể để tiếp tục lượt đó.");
                return;
            }
            db.SaveGtinSyncJob(store,"WB_WRITEBACK",new(ProductCatalog.Scope(store),JsonSerializer.Serialize(new[]{target}),0,"QUEUED",null,""));
        }finally{gate.Release();}
    }

    public async Task<PriceUpdateResult> ResumeWbGtinWritebackAsync(StoreProfile store,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        if(store.Marketplace!=Marketplace.Wildberries)return new(false,"Cập nhật GTIN lên card chỉ dành cho WB.");
        var gate=Gate(store);await gate.WaitAsync(ct).ConfigureAwait(false);
        try{
            var job=db.GetGtinSyncJob(store,"WB_WRITEBACK");if(job is null)return new(false,"Chưa có lượt GTIN WB trong hàng đợi.");
            if(job.Scope!=ProductCatalog.Scope(store))return new(false,"Thông tin tài khoản WB thay đổi. Xác minh cửa hàng trước khi tiếp tục checkpoint cũ.");
            if(job.State=="COMPLETE")return new(true,"Lượt GTIN WB đã được xác minh.");
            if(job.RetryAt is { } until&&until>DateTimeOffset.UtcNow)return new(false,$"WB yêu cầu chờ đến {until.ToLocalTime():HH:mm:ss}. Checkpoint đã giữ; chưa gọi tiếp.");
            var targets=JsonSerializer.Deserialize<GtinSyncTarget[]>(job.SnapshotJson)??throw new InvalidDataException("Checkpoint GTIN bị thiếu.");
            var groups=targets.GroupBy(x=>x.ExternalId).OrderBy(x=>long.Parse(x.Key)).Select(x=>x.ToArray()).ToArray();
            var mappings=db.ConfirmedGtinMappings(store);
            if(targets.Any(x=>!mappings.TryGetValue((x.Sku,x.VariantId),out var gtin)||gtin!=x.Gtin)){
                db.SaveGtinSyncJob(store,"WB_WRITEBACK",job with{State="STALE_MAPPING",LastError="Mapping thay đổi. Kiểm tra biến thể rồi tạo lượt GTIN mới."});return new(false,"Mapping thay đổi; chưa ghi lên WB. Hãy kiểm tra và tạo lượt mới.");
            }
            GtinSyncTarget[] current=Array.Empty<GtinSyncTarget>();
            try{
                while(job.Cursor<groups.Length){
                    ct.ThrowIfCancellationRequested();var batch=groups.Skip(job.Cursor).Take(50).ToArray();current=batch.SelectMany(x=>x).ToArray();var payloads=new List<JsonObject>();
                    // Read before any re-submit, including an ambiguous response or restart.
                    foreach(var group in batch){
                        progress?.Report($"WB · đối soát card {job.Cursor+payloads.Count+1}/{groups.Length}");
                        var card=await api.ReadWbGtinCardAsync(store,group[0].ExternalId,ct).ConfigureAwait(false);
                        if(card["vendorCode"]?.ToString()!=group[0].Sku)throw new InvalidDataException("nmID hiện tại không còn thuộc đúng SKU. Chưa ghi card.");
                        if(group.All(x=>WbGtinPayloads.Matches(card,x)))continue;
                        payloads.Add(WbGtinPayloads.BuildWbGtinCard(card.ToJsonString(),group.ToDictionary(x=>x.VariantId,x=>x.Gtin,StringComparer.Ordinal)));
                    }
                    if(payloads.Count>0){
                        job=job with{State="WRITE_UNKNOWN",RetryAt=null,LastError=""};db.SaveGtinSyncJob(store,"WB_WRITEBACK",job);
                        await api.WriteWbGtinCardsAsync(store,payloads,ct).ConfigureAwait(false);
                    }
                    job=job with{State="VERIFY_PENDING"};db.SaveGtinSyncJob(store,"WB_WRITEBACK",job);
                    var unmatched=0;
                    foreach(var group in batch){
                        var card=await api.ReadWbGtinCardAsync(store,group[0].ExternalId,ct).ConfigureAwait(false);
                        foreach(var target in group){var verified=WbGtinPayloads.Matches(card,target);db.MarkWbGtinState(store,target,verified?"VERIFIED":"VERIFY_PENDING",verified?"":"WB chưa trả đúng GTIN của size đã gửi.");if(!verified)unmatched++;}
                    }
                    if(unmatched>0){db.SaveGtinSyncJob(store,"WB_WRITEBACK",job with{LastError=$"{unmatched} biến thể chưa khớp readback; giữ nguyên batch để đối soát."});return new(false,$"WB còn {unmatched} biến thể chưa khớp readback. Batch trước đã xác minh được giữ nguyên.");}
                    job=job with{Cursor=job.Cursor+batch.Length,State=job.Cursor+batch.Length==groups.Length?"COMPLETE":"QUEUED",RetryAt=null,LastError=""};db.SaveGtinSyncJob(store,"WB_WRITEBACK",job);
                }
                return new(true,$"WB đã xác minh GTIN cho {targets.Length} biến thể / {groups.Length} card.");
            }catch(GtinQuotaException ex){job=job with{State="RATE_LIMIT",RetryAt=DateTimeOffset.UtcNow+Nonnegative(ex.Delay),LastError=ex.Message};db.SaveGtinSyncJob(store,"WB_WRITEBACK",job);return new(false,ex.Message);}
            catch(OperationCanceledException){db.SaveGtinSyncJob(store,"WB_WRITEBACK",job with{LastError="Đã dừng; tiếp tục sẽ đọc lại WB trước khi ghi."});return new(false,"Đã dừng. Checkpoint và mapping đã xác minh vẫn được giữ.");}
            catch(Exception){db.SaveGtinSyncJob(store,"WB_WRITEBACK",job with{LastError="WB chưa xác nhận đầy đủ. Kiểm tra quyền token/kết nối và tiếp tục để đối soát batch hiện tại."});return new(false,"WB chưa xác nhận đầy đủ. Checkpoint đã giữ; tiếp tục sẽ đọc lại trước khi gửi.");}
        }finally{gate.Release();}
    }

    public async Task<PriceUpdateResult> SyncZnackGtinAsync(StoreProfile store,NationalCatalogAccess access,CancellationToken ct=default,IProgress<string>? progress=null)
    {
        if(string.IsNullOrWhiteSpace(access.ApiKey)&&string.IsNullOrWhiteSpace(access.Token))return new(false,"Nhập API key National Catalog hoặc xác thực chứng thư Znack trước khi đồng bộ.");
        var gate=Gate(store);await gate.WaitAsync(ct).ConfigureAwait(false);
        try{
            var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProductCatalog.Scope(store)+"\n"+access.Sandbox+"\n"+(access.ApiKey.Length>0?access.ApiKey:access.Token))));
            var job=db.GetGtinSyncJob(store,"ZNACK_PRODUCT");
            if(job?.RetryAt is { } until&&until>DateTimeOffset.UtcNow)return new(false,$"National Catalog yêu cầu chờ đến {until.ToLocalTime():HH:mm:ss}. Checkpoint đã giữ.");
            if(job is not null&&job.State!="COMPLETE"&&job.Scope!=scope)return new(false,"Tài khoản National Catalog đã thay đổi. Đối soát checkpoint cũ trước khi dùng credential mới.");
            if(job is null||job.State=="COMPLETE"){
                var targets=Rows(store).Select(Target).ToArray();
                foreach(var target in targets.Where(x=>GtinCode.Normalize(x.Gtin).Length==0))db.ObserveZnackGtin(store,target,"","GTIN_REQUIRED",false,"{}","Thiếu GTIN hợp lệ hoặc barcode mơ hồ. Seller cần chọn đúng biến thể.");
                job=new(scope,JsonSerializer.Serialize(targets.Where(x=>GtinCode.Normalize(x.Gtin)==x.Gtin&&x.Gtin.Length>0)),0,"QUEUED",null,"");db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job);
            }
            var groups=(JsonSerializer.Deserialize<GtinSyncTarget[]>(job.SnapshotJson)??Array.Empty<GtinSyncTarget>()).GroupBy(x=>x.Gtin).OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.ToArray()).ToArray();
            var failed=0;
            try{
                while(job.Cursor<groups.Length){
                    ct.ThrowIfCancellationRequested();var batch=groups.Skip(job.Cursor).Take(25).ToArray();progress?.Report($"National Catalog · GTIN {job.Cursor+1}–{job.Cursor+batch.Length}/{groups.Length}");
                    await pause(TimeSpan.FromSeconds(3),ct).ConfigureAwait(false);
                    var baseUrl=access.Sandbox?"https://api.nk.sandbox.crptech.ru":"https://апи.национальный-каталог.рф";
                    var query="/v3/product?gtins="+Uri.EscapeDataString(string.Join(";",batch.Select(x=>x[0].Gtin)))+"&format=json";
                    if(access.ApiKey.Length>0)query+="&apikey="+Uri.EscapeDataString(access.ApiKey);
                    using var request=new HttpRequestMessage(HttpMethod.Get,baseUrl+query);
                    if(access.ApiKey.Length==0)request.Headers.Authorization=new("Bearer",access.Token);
                    using var response=await nationalHttp.SendAsync(request,ct).ConfigureAwait(false);
                    if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new GtinQuotaException("/v3/product",response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date is { } at?at-DateTimeOffset.UtcNow:TimeSpan.FromMinutes(5)));
                    if(!response.IsSuccessStatusCode)throw new InvalidDataException("National Catalog chưa trả dữ liệu hợp lệ.");
                    var root=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    var cards=root?["result"] as JsonArray??root?["result"]?["goods"] as JsonArray??throw new InvalidDataException("National Catalog thiếu result cards.");
                    foreach(var group in batch){
                        var matches=cards.Where(x=>CardGtins(x).Contains(group[0].Gtin)).ToArray();
                        foreach(var target in group){
                            var card=matches.Length==1?matches[0]:null;var status=card?["good_status"]?.ToString()??"";var goodId=card?["good_id"]?.ToString()??"";
                            var size=(card?["good_attrs"] as JsonArray)?.FirstOrDefault(x=>x?["attr_id"]?.ToString()=="35")?["attr_value"]?.ToString()??"";
                            var mismatch=size.Length>0&&target.Size.Length>0&&!size.Trim().Equals(target.Size.Trim(),StringComparison.OrdinalIgnoreCase);
                            var published=card is not null&&goodId.Length>0&&status=="published"&&card["is_tech_gtin"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)!=true&&!mismatch;
                            var stage=published?"PUBLISHED":mismatch?"VARIANT_MISMATCH":card is null?"NOT_FOUND":status.Length>0?status.ToUpperInvariant():"UNKNOWN";
                            var error=published?"":mismatch?"Size National Catalog không khớp biến thể. Kiểm tra mapping.":"Chưa có đúng một card published với GTIN này.";
                            if(!published)failed++;
                            var metadata=JsonSerializer.Serialize(new{Gtin=target.Gtin,GoodId=goodId,Status=status,Size=Safe(size,store,access),Name=Safe(card?["good_name"]?.ToString()??"",store,access)});
                            db.ObserveZnackGtin(store,target,goodId,stage,published,metadata,error);
                        }
                    }
                    job=job with{Cursor=job.Cursor+batch.Length,State=job.Cursor+batch.Length==groups.Length?"COMPLETE":"QUEUED",RetryAt=null,LastError=""};
                    if(Exhausted(response,"API-Usage-Limit")||Exhausted(response,"API-Method-Usage-Limit"))job=job with{RetryAt=DateTimeOffset.UtcNow+TimeSpan.FromMinutes(5),State=job.State=="COMPLETE"?"COMPLETE":"RATE_LIMIT"};
                    db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job);if(job.State=="RATE_LIMIT")return new(false,"National Catalog đã chạm quota. Giữ checkpoint và tiếp tục sau 5 phút.");
                }
                if(groups.Length==0){db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job with{State="COMPLETE"});return new(false,"Chưa có GTIN hợp lệ để đồng bộ. Seller cần xác nhận mapping theo size.");}
                return new(failed==0,failed==0?$"Đã xác minh {groups.Length} GTIN với National Catalog.":$"Đã đọc National Catalog; {failed} biến thể cần kiểm tra trạng thái/size. Mapping đã xác nhận được giữ nguyên.");
            }catch(GtinQuotaException ex){db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job with{State="RATE_LIMIT",RetryAt=DateTimeOffset.UtcNow+Nonnegative(ex.Delay),LastError=ex.Message});return new(false,ex.Message);}
            catch(OperationCanceledException){db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job with{LastError="Đã dừng; tiếp tục từ checkpoint hiện tại."});return new(false,"Đã dừng đồng bộ National Catalog. Mapping đã xác nhận được giữ.");}
            catch(Exception){db.SaveGtinSyncJob(store,"ZNACK_PRODUCT",job with{LastError="National Catalog chưa trả đầy đủ dữ liệu. Kiểm tra credential/kết nối và tiếp tục checkpoint hiện tại."});return new(false,"National Catalog chưa trả đầy đủ dữ liệu. Mapping đã xác nhận và checkpoint được giữ.");}
        }finally{gate.Release();}
    }

    private static TimeSpan Nonnegative(TimeSpan delay)=>delay<TimeSpan.Zero?TimeSpan.Zero:delay;
    private static IEnumerable<string> CardGtins(JsonNode? card)
    {
        var values=(card?["identified_by"] as JsonArray)?.Where(x=>x?["type"]?.ToString()=="gtin").Select(x=>GtinCode.Normalize(x?["value"]?.ToString()))??Enumerable.Empty<string>();
        return values.Append(GtinCode.Normalize(card?["gtin"]?.ToString())).Where(x=>x.Length>0).Distinct(StringComparer.Ordinal);
    }
    private static bool Exhausted(HttpResponseMessage response,string header)
    {
        if(!response.Headers.TryGetValues(header,out var values))return false;var parts=values.FirstOrDefault()?.Split('/');
        return parts?.Length==2&&int.TryParse(parts[0],out var used)&&int.TryParse(parts[1],out var limit)&&limit>0&&used>=limit;
    }
    private static string Safe(string value,StoreProfile store,NationalCatalogAccess access)
    {
        foreach(var secret in new[]{store.ApiKey,store.Token,access.ApiKey,access.Token}.Where(x=>x.Length>0))value=value.Replace(secret,"[đã che]",StringComparison.Ordinal);
        value=value.Replace('\r',' ').Replace('\n',' ');return value[..Math.Min(value.Length,500)];
    }
}

public sealed partial class AppServices
{
    public void QueueWbGtinWritebackForVariant(StoreProfile store,string sku,string variantId)=>new GtinMappingSyncService(Db,Api,znakHttp).QueueWbGtinWritebackForVariant(store,sku,variantId);
    public void QueueWbGtinWriteback(StoreProfile store)=>new GtinMappingSyncService(Db,Api,znakHttp).QueueWbGtinWriteback(store);
    public Task<PriceUpdateResult> ResumeWbGtinWritebackAsync(StoreProfile store,CancellationToken ct=default)=>new GtinMappingSyncService(Db,Api,znakHttp).ResumeWbGtinWritebackAsync(store,ct);
    public Task<PriceUpdateResult> SyncZnackGtinAsync(StoreProfile store,NationalCatalogAccess access,CancellationToken ct=default)=>new GtinMappingSyncService(Db,Api,znakHttp).SyncZnackGtinAsync(store,access,ct);
}
