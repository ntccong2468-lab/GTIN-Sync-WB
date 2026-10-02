using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.UI;
using System.Net;
using System.Reflection;
using System.Text;

internal static class LabelJobUiTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static IEnumerable<Control> All(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(All(x)));
    static void Expect(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Pump(Task task){var end=DateTime.UtcNow.AddSeconds(12);while(!task.IsCompleted&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(1);}Expect(task.IsCompleted,"Workflow UI callback timed out");task.GetAwaiter().GetResult();}
    static Form Dialog(AppServices app,LabelTarget target){var type=typeof(MainForm).Assembly.GetType("MarketplaceHub.UI.FbsLabelJobDialog");Expect(type is not null,"FBS label job dialog missing");return (Form)Activator.CreateInstance(type!,app,target)!;}
    static void Apply(Form d,LabelJobResult result)=>d.GetType().GetMethod("Apply")!.Invoke(d,new object[]{result});
    public static void Run(Action<string,Action> check,AppServices app,MainForm form,StoreProfile store)
    {
        var target=new LabelTarget(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,"fixture-label-job");
        check("readiness_is_not_reported_at_post_success",()=>{
            using var d=Dialog(app,target);var unit=new FbsUnitKey(store.Id,store.Marketplace,"1","2",0);
            Apply(d,new("accepted",1,FbsLabelJobStage.Purchasing,0,2,new[]{new UnitWorkflowResult(unit,"AwaitingCodes",null,null)},Array.Empty<LabelArtifact>(),null));
            var stage=All(d).Single(c=>c.Name=="fbsJobStage");Expect(!stage.Text.Contains("100%")&&stage.ForeColor!=Color.FromArgb(34,197,94),"Accepted POST shown as completed");
            foreach(var name in new[]{"fbsJobUnits","fbsJobResume","fbsJobPause","fbsJobReprint","kizPrintDataMatrix","kizPhysicalConfirmed","kizWorkflowProfile"})Expect(All(d).Any(c=>c.Name==name),"Missing workflow action "+name);
            Expect(!All(d).Single(c=>c.Name=="fbsJobReprint").Enabled,"No artifact yet but reprint is enabled");
        });
        check("workflow_profile_does_not_assume_producer_or_enable_purchase",()=>{
            var type=typeof(MainForm).Assembly.GetType("MarketplaceHub.UI.KizWorkflowProfileDialog");Expect(type is not null,"Workflow profile dialog missing");using var d=(Form)Activator.CreateInstance(type!,app,store)!;
            var business=All(d).OfType<ComboBox>().Single(c=>c.Name=="kizProfileBusiness");var buy=All(d).OfType<CheckBox>().Single(c=>c.Name=="kizProfileAutoPurchase");
            Expect(business.SelectedIndex<=0&&!buy.Checked,"New seller silently became a producer or enabled purchases");
            Expect(All(d).Single(c=>c.Name=="kizProfileCapability").Text.Length>0,"Capability gate not shown");
        });
        check("workflow_actions_fit_both_viewports_and_themes",()=>{
            foreach(var dark in new[]{true,false})foreach(var width in new[]{1044,1366}){
                using var d=Dialog(app,target);var appearance=d.GetType().GetMethod("SetAppearance");Expect(appearance is not null,"Dialog theme selection missing");appearance!.Invoke(d,new object[]{dark});d.Size=new(width,768);d.Show(form);d.MinimumSize=new(width,768);d.Size=new(width,768);Application.DoEvents();Expect(d.Width==width,"Runner did not render requested viewport width");
                foreach(var stage in new[]{FbsLabelJobStage.AwaitingLegalState,FbsLabelJobStage.AwaitingPhysicalMark,FbsLabelJobStage.Partial,FbsLabelJobStage.LabelsReady}){
                    Apply(d,new("layout",1,stage,stage==FbsLabelJobStage.LabelsReady?2:1,2,new[]{new UnitWorkflowResult(new(store.Id,store.Marketplace,"101","A",0),stage==FbsLabelJobStage.LabelsReady?"LabelsReady":"AwaitingLegalState",null,"legal_check_required"),new UnitWorkflowResult(new(store.Id,store.Marketplace,"102","B",0),stage==FbsLabelJobStage.LabelsReady?"LabelsReady":"AwaitingPhysicalMark",null,"physical_confirmation_required")},Array.Empty<LabelArtifact>(),"fixture_waiting"));Application.DoEvents();
                    foreach(var button in All(d).OfType<Button>()){var p=d.PointToClient(button.PointToScreen(Point.Empty));Expect(p.X>=0&&p.Y>=0&&p.X+button.Width<=d.ClientSize.Width&&p.Y+button.Height<=d.ClientSize.Height,"Workflow action clipped: "+button.Name);}
                    var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);using var image=new Bitmap(d.Width,d.Height);d.DrawToBitmap(image,new Rectangle(Point.Empty,d.Size));image.Save(Path.Combine(dir,$"FbsJob-{width}-{(dark?"dark":"light")}-{stage}.png"));
                }d.Close();
            }
        });
        check("switching_page_unsubscribes_without_killing_job",()=>{
            var s=app.Db.SaveStore(new(0,Marketplace.Yandex,"job navigation","","fixture-api","456","789","fixture",true));var batch=app.Db.CreateMarketplaceFbsBatch(s,new[]{"11"});var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
            var http=typeof(MarketplaceGateway).GetField("http",Hidden)!;var previous=http.GetValue(app.Api);http.SetValue(app.Api,new HttpClient(new FixtureHttp(async()=>{if(Interlocked.Increment(ref count)==1){entered.SetResult();await release.Task;}return new(HttpStatusCode.OK){Content=new StringContent("{\"order\":{\"id\":11,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":1}]}}",Encoding.UTF8,"application/json")};})));
            try{
                var show=typeof(MainForm).GetMethod("ShowFbsLabelJobAsync",Hidden);Expect(show is not null,"MainForm does not observe a persistent label job");
                var pageCts=(CancellationTokenSource)typeof(MainForm).GetField("pageCts",Hidden)!.GetValue(form)!;
                Pump((Task)show!.Invoke(form,new object[]{new LabelTarget(s.Id,s.Marketplace,LabelTargetKind.MarketplaceBatch,batch.Id),pageCts.Token})!);Pump(entered.Task);
                typeof(MainForm).GetMethod("ShowReport",Hidden)!.Invoke(form,null);release.SetResult();
                Pump(app.ExportFbsLabelsAsync(new(s.Id,s.Marketplace,LabelTargetKind.MarketplaceBatch,batch.Id)));
                Expect(All(form).Any(c=>c.Text=="Báo cáo")&&app.Db.ActiveLabelJobs(s.Id).Count==1,"Background job disappeared or replaced new page");
                Expect(!Application.OpenForms.Cast<Form>().Any(x=>x.GetType().Name=="FbsLabelJobDialog"),"Page observer did not unsubscribe");
            }finally{release.TrySetResult();http.SetValue(app.Api,previous);app.Db.DeleteStore(s.Id);}
        });
    }
    private sealed class FixtureHttp(Func<Task<HttpResponseMessage>> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>response();}
}
