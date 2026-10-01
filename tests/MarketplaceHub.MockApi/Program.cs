using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

var app=new AppServices();
var mock=new OzonYandexMockApi();
typeof(MarketplaceGateway).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app.Api,new HttpClient(mock));
var suffix=Guid.NewGuid().ToString("N");
var ozon=app.Db.SaveStore(new StoreProfile(0,Marketplace.Ozon,"MOCK-OZON-"+suffix,"10001","mock-secret","","","",true));
var yandex=app.Db.SaveStore(new StoreProfile(0,Marketplace.Yandex,"MOCK-YANDEX-"+suffix,"","mock-secret","20001","30001","",true));
const string gtin="04601234567893";
string Kiz(string serial)=>"01"+gtin+"21"+serial+"\u001d91ABCD\u001d92MOCKPROOF";
var failures=new List<string>();var checks=0;
void Expect(bool ok,string message){checks++;if(!ok){failures.Add(message);Console.WriteLine("FAIL "+message);}else Console.WriteLine("PASS "+message);}
try
{
    foreach(var store in new[]{ozon,yandex})
    {
        var sku=store.Marketplace==Marketplace.Ozon?"OZ-SKU":"YA-SKU";
        app.Db.ReplaceProducts(store.Id,store.Marketplace,new[]{new ProductRow(store.Id,store.Marketplace,sku,sku,"Mock marked product",1000,"","{}")});
        app.Db.UpsertKiz(Kiz(store.Marketplace==Marketplace.Ozon?"OZON0001":"YANDEX01"),gtin,"AVAILABLE");
        var rows=await app.Api.SyncFbsAsync(store);app.Db.UpsertOrders(store.Id,store.Marketplace,rows);
        Expect(rows.Count==1,store.Marketplace+" mock queue returns one complete posting");
        var orderId=rows.Single().ExternalOrderId;
        var received=await app.ReceiveMarketplaceFbsAsync(store,new[]{orderId});
        Expect(received.Success&&received.ExternalTaskId is not null,store.Marketplace+" receives posting into a durable shipment");
        Expect(app.Db.MarketplaceReceivedOrderIds(store).Contains(orderId),store.Marketplace+" received posting leaves New queue");
        var packed=await app.PackMarketplaceFbsAsync(store,Array.Empty<string>(),_=>gtin,true,batchId:received.ExternalTaskId);
        Expect(packed.Success,store.Marketplace+" automatically reserves, submits and verifies KIZ before packing: "+packed.Message);
        var reservations=app.Db.MarketplaceKizReservations(store,orderId);
        Expect(reservations.Count==1&&reservations[0].Status=="ASSIGNED",store.Marketplace+" owns exactly one accepted KIZ for one physical unit");
        var label=await app.ExportVerifiedMarketplaceLabelAsync(store,orderId,_=>gtin);
        Expect(label.Success&&label.FilePath is not null&&File.Exists(label.FilePath),store.Marketplace+" returns a verified official PDF label: "+label.Message);
        if(label.FilePath is not null&&File.Exists(label.FilePath))File.Delete(label.FilePath);
    }
    Expect(mock.OzonLabelCreates==1,"Ozon creates one label task and never uses the obsolete direct PDF endpoint");
    Expect(mock.OzonShips==1&&mock.YandexReadyMutations==1,"Both mock platforms receive exactly one final packing mutation");
    Expect(mock.UnexpectedMutations.Count==0,"Mock API observed no duplicate or out-of-order mutation");
}
finally
{
    app.Db.DeleteStore(ozon.Id);app.Db.DeleteStore(yandex.Id);
}
Console.WriteLine($"{checks-failures.Count}/{checks} Ozon/Yandex mock API end-to-end checks passed");
return failures.Count==0?0:1;

sealed class OzonYandexMockApi:HttpMessageHandler
{
    private string? ozonCode;private string? yandexCode;private bool ozonPacked;private bool yandexPacked;
    public int OzonLabelCreates{get;private set;}public int OzonShips{get;private set;}public int YandexReadyMutations{get;private set;}
    public List<string> UnexpectedMutations{get;}=new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var path=request.RequestUri!.AbsolutePath;var host=request.RequestUri.Host;var body=request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult()??"";
        if(host.EndsWith("ozon.ru",StringComparison.OrdinalIgnoreCase))return Task.FromResult(Ozon(request,path,body));
        if(host.EndsWith("yandex.ru",StringComparison.OrdinalIgnoreCase))return Task.FromResult(Yandex(request,path,body));
        return Task.FromResult(Json("{}",HttpStatusCode.NotFound));
    }
    private HttpResponseMessage Ozon(HttpRequestMessage request,string path,string body)
    {
        if(path=="/v4/posting/fbs/unfulfilled/list")return Json("{\"result\":{\"postings\":["+OzonPosting()+"],\"has_next\":false,\"cursor\":\"\"}}");
        if(path=="/v3/posting/fbs/get")return Json("{\"result\":"+OzonPosting()+"}");
        if(path=="/v6/fbs/posting/product/exemplar/create-or-get")return Json(Exemplars());
        if(path=="/v5/fbs/posting/product/exemplar/validate")return Json("{\"products\":[{\"product_id\":100,\"exemplars\":[{\"status\":\"valid\"}]}]}");
        if(path=="/v6/fbs/posting/product/exemplar/set")
        {
            var root=JsonNode.Parse(body)!;ozonCode=root["products"]![0]!["exemplars"]![0]!["marks"]![0]!["mark"]!.ToString();return Json("{}");
        }
        if(path=="/v5/fbs/posting/product/exemplar/status")return Json(ozonCode is null?"{\"status\":\"validation_in_process\"}":"{\"status\":\"ship_available\",\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":5001,\"marks\":[{\"mark\":"+JsonValue.Create(ozonCode).ToJsonString()+",\"check_status\":\"passed\"}]}]}]}");
        if(path=="/v4/posting/fbs/ship")
        {
            if(ozonPacked)UnexpectedMutations.Add("duplicate Ozon ship");OzonShips++;ozonPacked=true;return Json("{}");
        }
        if(path=="/v2/posting/fbs/package-label/create")
        {
            if(!ozonPacked)UnexpectedMutations.Add("label before Ozon ship");OzonLabelCreates++;return Json("{\"result\":{\"task_id\":9001}}");
        }
        if(path=="/v1/posting/fbs/package-label/get")return Json("{\"result\":{\"status\":\"completed\",\"file_url\":\"https://cdn.ozon.ru/mock/9001.pdf\"}}");
        if(path=="/mock/9001.pdf")return Pdf();
        return Json("{}",HttpStatusCode.NotFound);
    }
    private string OzonPosting()=>"{\"posting_number\":\"OZ-1\",\"status\":\""+(ozonPacked?"awaiting_deliver":"awaiting_packaging")+"\",\"requirements\":{\"products_requiring_mandatory_mark\":[100]},\"products\":[{\"sku\":100,\"product_id\":100,\"offer_id\":\"OZ-SKU\",\"name\":\"Mock Ozon product\",\"quantity\":1}]}";
    private string Exemplars()=>"{\"products\":[{\"product_id\":100,\"exemplars\":[{\"exemplar_id\":5001"+(ozonCode is null?"":",\"marks\":[{\"mark_type\":\"mandatory_mark\",\"mark\":"+JsonValue.Create(ozonCode).ToJsonString()+"}]")+"}]}]}";
    private HttpResponseMessage Yandex(HttpRequestMessage request,string path,string body)
    {
        if(path=="/v2/campaigns/30001/orders"&&request.Method==HttpMethod.Get)return Json("{\"orders\":["+YandexOrder()+"],\"paging\":{}}");
        if(path=="/v2/campaigns/30001/orders/7001"&&request.Method==HttpMethod.Get)return Json("{\"order\":"+YandexOrder()+"}");
        if(path.EndsWith("/identifiers/status"))return Json(yandexCode is null?"{\"status\":\"OK\",\"result\":{\"items\":[]}}":"{\"status\":\"OK\",\"result\":{\"items\":[{\"id\":11,\"cis\":[{\"value\":"+JsonValue.Create(yandexCode).ToJsonString()+",\"status\":\"OK\"}]}]}}");
        if(path.EndsWith("/boxes")&&request.Method==HttpMethod.Put)
        {
            var root=JsonNode.Parse(body)!;yandexCode=root["boxes"]![0]!["items"]![0]!["instances"]![0]!["cis"]!.ToString();return Json("{\"status\":\"OK\"}");
        }
        if(path=="/v2/campaigns/30001/orders/7001/status"&&request.Method==HttpMethod.Put)
        {
            if(yandexPacked)UnexpectedMutations.Add("duplicate Yandex READY_TO_SHIP");YandexReadyMutations++;yandexPacked=true;return Json("{\"status\":\"OK\"}");
        }
        if(path.EndsWith("/delivery/labels"))return Pdf();
        return Json("{}",HttpStatusCode.NotFound);
    }
    private string YandexOrder()=>"{\"id\":7001,\"status\":\"PROCESSING\",\"substatus\":\""+(yandexPacked?"READY_TO_SHIP":"STARTED")+"\",\"items\":[{\"id\":11,\"offerId\":\"YA-SKU\",\"offerName\":\"Mock Yandex product\",\"count\":1,\"requiredInstanceTypes\":[\"CIS\"]}]}";
    private static HttpResponseMessage Json(string body,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Pdf()=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\nmock official marketplace label"))};
}
