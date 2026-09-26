using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GTINSyncWB;

var passed=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;}
var shop=new Shop{Name="Shop"};
JsonObject Card()=>JsonNode.Parse("""{"nmID":101,"vendorCode":"PANTS","title":"Pants","brand":"B","description":"keep","dimensions":{"length":10},"characteristics":[{"id":7,"name":"Цвет","value":["đen"]}],"photos":[{"big":"photo"}],"sizes":[{"chrtID":11,"techSize":"XL","skus":["1234567890128"]},{"chrtID":12,"techSize":"L","skus":["2222222222222"]}]}""")!.AsObject();
var listing=new Listing(shop.Id,shop.Name,101,"PANTS","Pants","đen","photo",Card(),DateTimeOffset.UtcNow);
var gtin="00000000000017";
var goods=new List<CatalogItem>{new(gtin,"PANTS","Pants","B","đen","48","published",DateTimeOffset.UtcNow,"NK")};
var settings=new Settings();
Check(Matching.Build(goods,[listing],settings).All(x=>x.Status==MatchStatus.Missing),"No implicit XL to 48 conversion");
settings.SizeRules["PANTS:XL"]="48";
var rows=Matching.Build(goods,[listing],settings);Check(rows[0].Status==MatchStatus.Exact,"Product scoped size rule");
Check(rows[1].Status==MatchStatus.Missing,"Other size stays unmatched");
Check(Matching.Build([goods[0] with { Color="navy" }],[listing],settings)[0].Status==MatchStatus.Missing,"Color isolation");
Check(Matching.Build([goods[0],goods[0] with { Gtin="00000000000024" }],[listing],settings)[0].Status==MatchStatus.Multiple,"Ambiguous codes blocked");
Check(Matching.Build([goods[0] with { Status="draft" }],[listing],settings)[0].Status==MatchStatus.Unpublished,"Draft blocked");
rows[0].Selected=true;
var payload=CardPayload.Add(Card(),rows);var changed=payload[0]!.AsObject();
Check(Json.S(changed,"description")=="keep" && changed["dimensions"]!.ToJsonString()==Card()["dimensions"]!.ToJsonString(),"Card fields preserved");
Check(Json.A(changed,"sizes").Count==2 && Json.A(Json.A(changed,"sizes")[1],"skus").Count==1,"Other size preserved");
Check(Json.A(Json.A(changed,"sizes")[0],"skus").Select(x=>x!.ToString()).SequenceEqual(new[]{"1234567890128",gtin}),"Append only");
Check(changed["photos"]==null,"Media excluded from update API payload");
Check(Matching.Build(goods,[listing with { Raw=changed }],settings)[0].Status==MatchStatus.Existing,"Duplicate is detected");
var conflict=Card();Json.A(Json.A(conflict,"sizes")[1],"skus").Add(gtin);
Check(Matching.Build(goods,[listing with { Raw=conflict }],settings)[0].Status==MatchStatus.Conflict,"Barcode in another size blocked");
Check(Gtin.IsValid(gtin) && !Gtin.IsValid("00000000000018"),"GTIN check digit and leading zero");
var mock=new MockHandler();var api=new CatalogApi(new ApiTransport(mock));
var read=await api.Read("test-key",new DateTime(2026,1,1),new DateTime(2026,1,2),null,CancellationToken.None);
Check(read.Count==2 && read.All(x=>x.Model=="PANTS"),"NK pagination and detailed attributes");
Check(mock.Calls.Count(x=>x.Contains("product-list"))==2,"NK follows total/offset");
var retry=new ApiTransport(new RetryHandler());
using var req=new HttpRequestMessage(HttpMethod.Get,"https://example.org/data");
Check((await retry.Send(req,CancellationToken.None))["ok"]?.ToString()=="true","429 retry");
var denied=new ApiTransport(new DeniedHandler());
try{using var r=new HttpRequestMessage(HttpMethod.Get,"https://example.org/data");await denied.Send(r,CancellationToken.None);throw new Exception("403 incorrectly passed");}
catch(ApiFailure e){Check(e.Status==403,"403 surfaced without secret");}
Console.WriteLine($"PASS {passed} assertions");

sealed class MockHandler:HttpMessageHandler
{
    public List<string> Calls=[];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var url=request.RequestUri!.ToString();Calls.Add(url);
        string text;
        if(url.Contains("product-list"))
        {
            var second=url.Contains("offset=1000");
            // Force a full first page to exercise offset pagination.
            var goods=second?"{\"gtin\":\"00000000000024\"}":string.Join(',',Enumerable.Range(0,1000).Select(_=>"{\"gtin\":\"00000000000017\"}"));
            text="{\"result\":{\"total\":1001,\"goods\":["+goods+"]}}";
        }
        else text="{\"result\":[{\"identified_by\":[{\"value\":\"00000000000017\",\"type\":\"gtin\"}],\"good_status\":\"published\",\"good_attrs\":[{\"attr_name\":\"Артикул\",\"attr_value\":\"PANTS\"}]},{\"identified_by\":[{\"value\":\"00000000000024\",\"type\":\"gtin\"}],\"good_status\":\"published\",\"good_attrs\":[{\"attr_name\":\"Артикул\",\"attr_value\":\"PANTS\"}]}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(text,Encoding.UTF8,"application/json")});
    }
}
sealed class RetryHandler:HttpMessageHandler
{
    private int calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var response=++calls==1?new HttpResponseMessage(HttpStatusCode.TooManyRequests):new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true}")};
        if(calls==1)response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));return Task.FromResult(response);
    }
}
sealed class DeniedHandler:HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
}
