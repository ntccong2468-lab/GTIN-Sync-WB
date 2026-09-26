using System.Net;
using System.IO.Compression;
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
var direct=goods[0] with {Size="XL"};
Check(Matching.Build([direct],[listing],settings)[0].Status==MatchStatus.Exact,"Registered article, color and size match automatically without a mapping rule");
Check(Matching.Build([direct],[listing with {VendorCode="PANTS-SIMILAR",Title="Pants"}],settings)[0].Status!=MatchStatus.Exact,"Nearly identical product name cannot override different article");
Check(Matching.Build([direct with {Color=""}],[listing],settings)[0].Status!=MatchStatus.Exact,"Missing catalog color blocks match");
Check(Matching.Build(goods,[listing],settings)[0].Status==MatchStatus.NeedsConfirmation,"No implicit XL to 48 conversion");
var secondCard=Card();secondCard["nmID"]=102;secondCard["vendorCode"]="SHIRT";
var second=new Listing(shop.Id,shop.Name,102,"SHIRT","Shirt","đen","",secondCard,DateTimeOffset.UtcNow);
Check(Matching.Build(goods,[second],settings)[0].Status!=MatchStatus.Exact,"Identical size in a different model cannot match");
var missingModel=listing with {VendorCode="",Raw=Card()};
Check(Matching.Build(goods,[missingModel],settings)[0].Status!=MatchStatus.Exact,"Missing model identifier blocks selection");
settings.SizeRules[shop.Id+":PANTS:XL"]="48";
var rows=Matching.Build(goods,[listing],settings);Check(rows[0].Status==MatchStatus.Exact,"Product scoped size rule");
Check(rows[1].Status==MatchStatus.NeedsConfirmation,"Other size stays unmatched");
Check(Matching.Build([goods[0] with { Color="navy" }],[listing],settings)[0].Status==MatchStatus.Conflict,"Color isolation");
var blueCard=Card();blueCard["nmID"]=103;blueCard["characteristics"]![0]!["value"]=new JsonArray("navy");
var blueListing=listing with {NmId=103,Color="navy",Raw=blueCard};
Check(Matching.Build(goods,[blueListing],settings)[0].Status!=MatchStatus.Exact,"Same size but wrong color blocked");
Check(Matching.Build([goods[0] with { Model="PANTS1",Size="XL" }],[listing with { VendorCode="PANTS-1" }],new Settings())[0].Status==MatchStatus.Missing,"Model punctuation is an identifier, not discarded");
var plus=Card();Json.A(plus,"sizes")[0]!["techSize"]="XL+";
Check(Matching.Build([goods[0] with { Size="XL" }],[listing with { Raw=plus }],new Settings())[0].Status==MatchStatus.NeedsConfirmation,"Size XL+ never silently matches XL");
settings.ColorRules[shop.Id+":PANTS:ĐEN"]="navy";
Check(Matching.Build([goods[0] with { Color="navy" }],[listing],settings)[0].Status==MatchStatus.Exact,"Explicit shop and product scoped color rule");
settings.ColorRules.Clear();
var mappedOther=Matching.Build(goods,[second],settings)[0];
Check(mappedOther.Status!=MatchStatus.Exact,"XL conversion remains local to PANTS");
Check(Matching.Build([goods[0],goods[0] with { Gtin="00000000000024" }],[listing],settings)[0].Status==MatchStatus.Multiple,"Ambiguous codes blocked");
settings.GtinRules[shop.Id+":101:11"]=gtin;
Check(Matching.Build([goods[0],goods[0] with { Gtin="00000000000024" }],[listing],settings)[0].Status==MatchStatus.Exact,"Seller-confirmed GTIN pin resolves only the exact chrtID");
settings.GtinRules.Clear();
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
var selection=Matching.Build(goods,[listing,second],settings);
Check(MatchSelection.SelectExact(selection,shop.Id,MatchStatus.Exact)==1 && selection.Count(x=>x.Selected)==1,"Select all exact in current shop/filter across underlying collection");
Check(MatchSelection.SelectExact(selection,shop.Id,MatchStatus.Missing)==0,"Nonexact filter cannot select blocked rows");
Check(MatchSelection.Clear(selection)==0 && selection.All(x=>!x.Selected),"Clear all selection");
var drop=Matching.Build(goods,[listing],settings)[0];drop.Selected=true;
Check(MatchSelection.Revalidate([drop],[goods[0] with {Color="navy"}],[listing],settings).Count==0 && drop.Status==MatchStatus.Review,"Revalidate drops changed candidate before preview");
Check(Gtin.IsValid(gtin) && !Gtin.IsValid("00000000000018"),"GTIN check digit and leading zero");
var mixed=JsonNode.Parse("""{"identified_by":[{"type":"gtin","value":"00000000000017","level":"trade-unit"},{"type":"gtin","value":"00000000000024","level":"box"}],"good_status":"published"}""")!;
Check(CatalogApi.Parse(mixed,DateTimeOffset.UtcNow,gtin).TradeUnit && !CatalogApi.Parse(mixed,DateTimeOffset.UtcNow,"00000000000024").TradeUnit,"NK packaging level remains bound to the requested GTIN");
var mock=new MockHandler();var api=new CatalogApi(new ApiTransport(mock));
var read=await api.Read("test-key",new DateTime(2026,1,1),new DateTime(2026,1,2),null,CancellationToken.None);
Check(read.Count==2 && read.All(x=>x.Model=="PANTS"),"NK pagination and detailed attributes");
Check(mock.Calls.Count(x=>x.Contains("product-list"))==2,"NK follows total/offset");
var wbPages=new WbPagesHandler();
var wbRead=await new WbApi(new ApiTransport(wbPages)).Read(shop,"dummy",CancellationToken.None);
Check(wbRead.Count==101 && wbRead.Last().NmId==101,"WB cursor pagination reads the final page");
Check(wbPages.Requests.Count==2 && wbPages.Requests[1].Contains("\"updatedAt\"") && wbPages.Requests[1].Contains("\"nmID\":100"),"WB sends updatedAt and nmID cursor");
var retry=new ApiTransport(new RetryHandler());
using var req=new HttpRequestMessage(HttpMethod.Get,"https://example.org/data");
Check((await retry.Send(req,CancellationToken.None))["ok"]?.ToString()=="true","429 retry");
var denied=new ApiTransport(new DeniedHandler());
try{using var r=new HttpRequestMessage(HttpMethod.Get,"https://example.org/data");await denied.Send(r,CancellationToken.None);throw new Exception("403 incorrectly passed");}
catch(ApiFailure e){Check(e.Status==403,"403 surfaced without secret");}
var uncertainHandler=new WriteTimeoutHandler();
var uncertainTransport=new ApiTransport(uncertainHandler);
try{using var r=new HttpRequestMessage(HttpMethod.Post,"https://example.org/update"){Content=new StringContent("[]")};await uncertainTransport.Send(r,CancellationToken.None,true);throw new Exception("Write timeout incorrectly accepted");}
catch(ApiFailure e){Check(e.UnknownOutcome && uncertainHandler.Calls==1,"Timed-out POST is unknown and never retried blindly");}
var workFolder=Path.Combine(Path.GetTempPath(),"gtin-queue-test-"+Guid.NewGuid().ToString("N"));
try
{
    var disk=new Storage(workFolder);
    disk.SaveSnapshot(new ReadSnapshot{CompletedAt=DateTimeOffset.UtcNow,Goods=goods,Cards=[listing]});
    Check(disk.LoadSnapshot()?.Goods.Single().Gtin==gtin && disk.LoadSnapshot()?.Cards.Single().Raw["sizes"] is JsonArray,"Read preview survives restart with GTIN string and sizes");
    var job=WriteJob.FromRows(listing,[rows[0]]);
    disk.SaveJobs([job]);
    var restored=new Storage(workFolder).LoadJobs().Single();
    Check(restored.Lines.Single().Gtin==gtin && restored.ExpectedCard==job.ExpectedCard,"Confirmed queue survives restart with leading zero");
    var fake=new FakeWriteGateway(listing);
    fake.OnUpdate=()=>throw new ApiFailure("Request timed out",0,true);
    var processor=new WriteProcessor(disk,fake,()=>DateTimeOffset.UtcNow);
    await processor.Process(restored,CancellationToken.None);
    Check(fake.Writes==1 && disk.LoadJobs().Single().Status==WriteState.Unknown,"Write timeout stays unknown and persisted");
    await processor.Process(disk.LoadJobs().Single(),CancellationToken.None);
    Check(fake.Writes==1,"Unknown result is not immediately resent");
    var already=Card();Json.A(Json.A(already,"sizes")[0],"skus").Add(gtin);
    fake.Current=listing with {Raw=already};
    await processor.Process(disk.LoadJobs().Single(),CancellationToken.None);
    Check(disk.LoadJobs().Single().Status==WriteState.Success && fake.Writes==1,"Readback resolves uncertain result at exact chrtID without retry");
    var wrong=Card();Json.A(Json.A(wrong,"sizes")[1],"skus").Add(gtin);
    fake.Current=listing with {Raw=wrong};
    var other=WriteJob.FromRows(listing,[rows[0]]);disk.SaveJobs([other]);
    await processor.Process(other,CancellationToken.None);
    Check(other.Status==WriteState.Review && fake.Writes==1,"Wrong chrtID blocks write");
    fake.Current=listing;
    var clean=WriteJob.FromRows(listing,[rows[0]]);disk.SaveJobs([clean]);
    fake.OnUpdate=()=>{var updated=Card();Json.A(Json.A(updated,"sizes")[0],"skus").Add(gtin);fake.Current=listing with {Raw=updated};return Task.CompletedTask;};
    await processor.Process(clean,CancellationToken.None);
    Check(clean.Status==WriteState.Success && fake.Writes==2,"Acknowledgment requires readback");
    fake.Current=listing with {Raw=Card()};
    var changedCard=Card();changedCard["description"]="changed on WB";fake.Current=listing with {Raw=changedCard};
    var drift=WriteJob.FromRows(listing,[rows[0]]);disk.SaveJobs([drift]);
    await processor.Process(drift,CancellationToken.None);
    Check(drift.Status==WriteState.Review && fake.Writes==2,"WB drift before update stops write");
    fake.Current=listing;fake.ReportError="WB processing rejected this card";fake.OnUpdate=()=>Task.CompletedTask;
    var rejected=WriteJob.FromRows(listing,[rows[0]]);disk.SaveJobs([rejected]);
    await processor.Process(rejected,CancellationToken.None);
    Check(rejected.Status==WriteState.Failed && fake.Writes==3,"HTTP 200 with asynchronous card processing error is not success");
    fake.ReportError="";
}
finally{if(Directory.Exists(workFolder))Directory.Delete(workFolder,true);}
var report=Path.Combine(Path.GetTempPath(),"gtin-report-"+Guid.NewGuid().ToString("N")+".xlsx");
try
{
    ReportWriter.Xlsx(report,["GTIN","Kết quả"],[[gtin,"Thành công"]]);
    using var archive=ZipFile.OpenRead(report);
    Check(archive.GetEntry("xl/workbook.xml")!=null && archive.GetEntry("xl/worksheets/sheet1.xml")!=null,"XLSX has workbook and worksheet");
    using var reader=new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
    Check((await reader.ReadToEndAsync()).Contains(gtin),"XLSX keeps leading zero GTIN as text");
}
finally{if(File.Exists(report))File.Delete(report);}
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
sealed class WriteTimeoutHandler:HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;throw new TaskCanceledException();}
}
sealed class WbPagesHandler:HttpMessageHandler
{
    public List<string> Requests=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(ct));
        var cards=Requests.Count==1?string.Join(',',Enumerable.Range(1,100).Select(i=>$"{{\"nmID\":{i},\"vendorCode\":\"SKU-{i}\",\"sizes\":[]}}")):"{\"nmID\":101,\"vendorCode\":\"SKU-101\",\"sizes\":[]}";
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"cards\":["+cards+"],\"cursor\":{\"updatedAt\":\"2026-09-26T00:00:00Z\",\"nmID\":100}}",Encoding.UTF8,"application/json")};
    }
}
sealed class FakeWriteGateway(Listing listing):IWriteGateway
{
    public Listing Current=listing;
    public int Writes;
    public Func<Task>? OnUpdate;
    public string ReportError="";
    public Task<Listing> ReadOne(Shop shop,string token,long nmId,CancellationToken ct)=>Task.FromResult(Current);
    public async Task Update(string token,JsonArray payload,CancellationToken ct){Writes++;if(OnUpdate!=null)await OnUpdate();}
    public Task<string> Errors(string token,long nmId,CancellationToken ct)=>Task.FromResult(ReportError);
}
