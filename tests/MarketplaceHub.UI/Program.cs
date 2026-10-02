using MarketplaceHub.TestSupport;
using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.UI;
using System.Reflection;
using System.Net;
using System.Text;

internal static class Program
{
    [STAThread]
    static int Main()
    {
        ApplicationConfiguration.Initialize();
        var failures = new List<string>();
        var checks=0;
        void Check(string name, Action test)
        {
            checks++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
        }
        void Expect(bool ok, string message) { if (!ok) throw new Exception(message); }
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        void Page(MainForm form, string method) => typeof(MainForm).GetMethod(method, instance)!.Invoke(form, null);
        bool State(string method, string state) => (bool)typeof(MainForm).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { state })!;
        void Pump(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
            Expect(task.IsCompleted, "Async callback did not finish within 8 seconds.");
            task.GetAwaiter().GetResult();
        }
        HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
        var app = WorkflowTestSupport.App(new MarketplaceHub.Infrastructure.AppDatabase(Path.Combine(Path.GetTempPath(),"MarketplaceHub-ui-"+Guid.NewGuid().ToString("N"),"test.db")),new MarketplaceGateway());
        var marker = "UI-FIXTURE-" + Guid.NewGuid().ToString("N");
        var store = app.Db.SaveStore(new StoreProfile(0, Marketplace.Yandex, marker, "123", "fixture", "456", "789", "fixture", true));
        app.Db.ReplaceProducts(store.Id, Marketplace.Yandex,
            new[] { new ProductRow(store.Id, Marketplace.Yandex, "A", "A", "Quần nam • A", 1250, "", "{}") });
        app.Db.UpsertOrders(store.Id, Marketplace.Yandex,
            new[] {
                new FbsOrderRow(store.Id, Marketplace.Yandex, "1", "A", "Quần nam • A", 2, "PROCESSING/STARTED", false, "{\"creationDate\":\"2026-09-30\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2}]}"),
                new FbsOrderRow(store.Id, Marketplace.Yandex, "1", "B", "Quần nam • B", 1, "PROCESSING/STARTED", false, "{\"creationDate\":\"2026-09-30\",\"items\":[{\"id\":3,\"offerId\":\"B\",\"count\":1}]}")
            });
        app.Db.MarkOrderRemoteState(store.Id,store.Marketplace,"1","PROCESSING/STARTED","");
        try
        {
            using var form = new MainForm(app);
            var picker = (ComboBox)typeof(MainForm).GetField("storePicker", instance)!.GetValue(form)!;
            for (var i = 0; i < picker.Items.Count; i++)
                if (picker.Items[i] is StoreProfile s && s.Id == store.Id) picker.SelectedIndex = i;
            form.Show();
            Application.DoEvents();
            Check("WB mixed receive dialog shows retained supply and one outcome per order",()=>{
                var type=typeof(MainForm).Assembly.GetType("MarketplaceHub.UI.WbReceiveResultDialog");
                Expect(type is not null,"WB per-order outcome dialog is missing.");
                var result=new WbReceiveResult(true,"SUPPLY-UI",true,1,new[]{
                    new WbReceiveOrderResult("301",WbReceiveDisposition.EligibleNew,true,"Đã thêm"),
                    new WbReceiveOrderResult("302",WbReceiveDisposition.Cancelled,false,"Khách đã hủy")},"Đã xử lý");
                using var dialog=(Form)Activator.CreateInstance(type!,result)!;
                var grid=All(dialog).OfType<DataGridView>().Single(x=>x.Name=="wbReceiveOutcomes");
                Expect(All(dialog).Any(x=>x.Name=="wbReceiveSupplyId"&&x.Text.Contains("SUPPLY-UI")),"Retained Supply ID is hidden.");
                Expect(grid.Rows.Count==2&&grid.Rows.Cast<DataGridViewRow>().Any(x=>x.Cells[1].Value?.ToString()=="Khách đã hủy"),"Mixed outcomes were collapsed into one batch result.");
            });
            Check("Ozon diagnostics keeps the real API key masked and declares read-only mode", () =>
            {
                var ozon = new StoreProfile(999, Marketplace.Ozon, "fixture", "client", "top-secret", "", "", "", true);
                using var dialog = new OzonDiagnosticsDialog(app.Api, ozon);
                var key = All(dialog).OfType<TextBox>().Single(x => x.Name == "ozonDiagnosticApiKey");
                var warning = All(dialog).Single(x => x.Name == "ozonReadOnlyWarning");
                Expect(key.UseSystemPasswordChar && key.Text == "top-secret", "Ozon key is not masked in the live-test panel.");
                Expect(warning.Text.Contains("chỉ đọc") && warning.Text.Contains("không tạo job nhãn"), "Mutation boundary is not visible to the seller.");
                Expect(All(dialog).OfType<Button>().Any(x => x.Name == "copyOzonRedactedReport"), "Redacted diagnostic export is missing.");
            });
            Check("Live validation center starts read-only with masked credentials and disabled mutation",()=>{
                var type=typeof(MainForm).Assembly.GetType("MarketplaceHub.UI.IntegrationTestCenterDialog");Expect(type is not null,"Local live validation center is missing.");
                var wb=store with{Marketplace=Marketplace.Wildberries,Token="private-wb-token"};
                using var dialog=(Form)Activator.CreateInstance(type!,app,wb)!;
                var token=All(dialog).OfType<TextBox>().Single(x=>x.Name=="liveWbToken");var nk=All(dialog).OfType<TextBox>().Single(x=>x.Name=="liveNationalCatalogKey");
                Expect(token.UseSystemPasswordChar&&nk.UseSystemPasswordChar,"Live credentials are visible unmasked.");
                var consent=All(dialog).OfType<CheckBox>().Single(x=>x.Name=="liveMutationConfirmed");var mutation=All(dialog).OfType<Button>().Single(x=>x.Name=="runLiveMutation");
                Expect(!consent.Checked&&!mutation.Enabled&&All(dialog).Any(x=>x.Name=="readOnlyIntegrationWarning"&&x.Text.Contains("chỉ đọc")),"Live panel enables remote writes by default.");
                consent.Checked=true;Expect(!mutation.Enabled,"Consent without a selected verified target enabled mutation.");
                var http=typeof(MarketplaceGateway).GetField("http",instance)!;var old=http.GetValue(app.Api);
                try{
                    http.SetValue(app.Api,new HttpClient(new AsyncFixtureHttp((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("{\"echo\":\"private-wb-token\"}")}))));
                    Pump((Task)type!.GetMethod("RunReadOnlyAsync",instance)!.Invoke(dialog,null)!);
                    var output=All(dialog).OfType<TextBox>().Single(x=>x.Name=="liveReadOnlyReport");
                    Expect(!output.Text.Contains("private-wb-token")&&output.Text.Contains("[đã che]"),"An echoed credential escaped redaction into the live report.");
                    Expect(!mutation.Enabled,"Failed read-only validation enabled a write.");
                }finally{http.SetValue(app.Api,old);}
                var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);
                dialog.Show(form);Application.DoEvents();using var image=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));image.Save(Path.Combine(dir,"IntegrationTestCenter.png"));dialog.Close();
            });
            Check("Packed Ozon and Yandex orders remain available for label printing", () =>
            {
                foreach (var state in new[] { "awaiting_deliver", "PROCESSING/READY_TO_SHIP" })
                    Expect(!State("IsNew", state) && State("IsPacking", state) && !State("IsShipping", state), "Packed order is absent from packing or shown in multiple tabs.");
            });
            Check("FBO grid offers selection and quantities per exact variant",()=>{
                var product=app.Db.Products(store.Id).Single();var scope=ProductCatalog.Scope(store);app.Db.BeginProductCatalog(store,scope);
                var variants=new[]{new ProductVariantRow(store.Id,store.Marketplace,"A","A","48","48",new[]{"4601234567893"},"04601234567893","","{\"color\":\"Đen\"}"),
                    new ProductVariantRow(store.Id,store.Marketplace,"A","A","50","50",new[]{"4601234567893"},"04601234567893","","{\"color\":\"Trắng\"}")};
                app.Db.ApplyProductCatalogPage(store,"",scope,new(new[]{new ProductCatalogEntry(product,variants)},"",true));
                Page(form,"ShowFboPacking");Application.DoEvents();
                var grid=All(form).OfType<DataGridView>().SingleOrDefault(x=>x.Name=="fboPreparation");
                Expect(grid is not null&&grid.Columns.Contains("quantity")&&grid.Columns.Contains("size")&&grid.Columns[0] is DataGridViewCheckBoxColumn,"FBO still uses a product-only cache table.");
                Expect(All(form).OfType<Button>().Any(x=>x.Name=="exportFboPreparation"),"Selected FBO quantities cannot be exported.");
                Expect(grid!.Rows.Count==2&&grid.Rows.Cast<DataGridViewRow>().Select(x=>x.Cells["size"].Value?.ToString()).SequenceEqual(new[]{"48","50"}),"Two sizes under the same SKU were merged.");
                Expect(grid.Rows.Cast<DataGridViewRow>().Select(x=>x.Cells["color"].Value?.ToString()).Distinct().Count()==2,"Variant color identity was lost.");
                var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);
                using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(dir,"FboPreparation.png"));
            });
            Check("FBO mandatory marking cannot be disabled before export",()=>{
                var product=app.Db.Products(store.Id).Single();app.Db.ReplaceProducts(store.Id,store.Marketplace,new[]{product with{RawJson="{\"needsKiz\":true}"}});
                Page(form,"ShowFboPacking");Application.DoEvents();var grid=All(form).OfType<DataGridView>().Single(x=>x.Name=="fboPreparation");
                Expect(grid.Rows.Count>0&&grid.Rows.Cast<DataGridViewRow>().All(x=>x.Cells["marked"].Value is true&&x.Cells["marked"].ReadOnly),"Seller can disable a known mandatory KIZ requirement.");
                var row=grid.Rows[0];row.Cells["marked"].Value=false;
                var resolve=typeof(MainForm).GetMethod("ResolveFboSelection",BindingFlags.Static|BindingFlags.NonPublic);Expect(resolve is not null,"FBO export lacks mandatory marking boundary.");
                var selected=(FboPreparationRow)resolve!.Invoke(null,new object[]{row})!;Expect(selected.NeedsKiz,"Unchecked UI state bypassed mandatory KIZ quantity/reservation checks.");
                app.Db.ReplaceProducts(store.Id,store.Marketplace,new[]{product});
            });
            Check("Report and FBS projection count one external order instead of product lines", () =>
            {
                var truth=(OrderTruthSnapshot)typeof(MainForm).GetMethod("BuildOrderTruth",instance)!.Invoke(form,new object[]{store})!;
                Expect(truth.NewCount==1&&truth.Orders.Count==1&&truth.Orders[0].Lines.Count==2,"UI projection counted SKU lines as separate orders.");
                Page(form,"ShowReport");Application.DoEvents();
                var label=All(form).OfType<Label>().Single(x=>x.Text=="Đơn mới");
                Expect(label.Parent!.Controls.OfType<Label>().Any(x=>x.Text=="1"),"Report did not render the canonical distinct count.");
                var chart=All(form).Single(x=>x.GetType().Name=="ReportBarChart");var values=(double[])chart.GetType().GetProperty("Values")!.GetValue(chart)!;
                Expect(values.Sum()==1,"Report chart counted product lines instead of distinct external orders.");
            });
            Check("KIZ Mapping exposes page controls and per-state inventory",()=>{
                Page(form,"ShowKizMapping");Application.DoEvents();
                var grid=All(form).OfType<DataGridView>().SingleOrDefault(x=>x.Name=="gtinMappingGrid");
                Expect(grid is not null&&grid.Columns.Contains("variant")&&grid.Columns.Contains("available")&&grid.Columns.Contains("reserved")&&grid.Columns.Contains("assigned"),"Mapping still merges first product barcodes or hides inventory states.");
                Expect(All(form).Any(x=>x.Name=="gtinMappingPage")&&All(form).OfType<Button>().Any(x=>x.Name=="nextGtinMappingPage"),"Mapping does not expose 50-row paging.");
                var actions=All(form).OfType<Button>().Where(x=>x.Text is "← Trước" or "Sau →" or "Đồng bộ catalog" or "Kiểm tra / đồng bộ Znack").ToArray();
                Expect(actions.Length==4&&actions.All(x=>x.Left>=0&&x.Right<=x.Parent!.ClientSize.Width),"KIZ Mapping actions are cut off in the current Windows viewport.");
                var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(dir,"KizMapping.png"));
            });
            Check("Delivered orders are absent from active shipping tab", () =>
                Expect(!State("IsShipping", "delivered"), "Delivered order is still active."));
            Check("Old page controls are disposed on navigation", () =>
            {
                Page(form, "ShowFbs");
                var work = (Panel)typeof(MainForm).GetField("work", instance)!.GetValue(form)!;
                var old = work.Controls.Cast<Control>().ToArray();
                Page(form, "ShowReport");
                Expect(old.All(c => c.IsDisposed), "Removed controls retained handles and old handlers.");
            });
            Check("FBS toolbar has no overlap at 1280 width", () =>
            {
                form.Size = new Size(1280, 780);
                Page(form, "ShowFbs"); Application.DoEvents();
                var controls = All(form).Where(c => c.Visible && (c.Text == "Đóng đơn FBS" || c.Text == "In nhãn đã chọn" || c.Text == "Tự động KIZ + in KIZ" || c.Text == "Tất cả danh mục")).ToArray();
                foreach (var a in controls)
                    foreach (var b in controls.Where(b => !ReferenceEquals(a, b)))
                        Expect(!new Rectangle(a.PointToScreen(Point.Empty), a.Size).IntersectsWith(new Rectangle(b.PointToScreen(Point.Empty), b.Size)), $"{a.Text} overlaps {b.Text}");
            });
            Check("Report date range changes the plotted days", () =>
            {
                Page(form, "ShowReport");
                var dates = All(form).OfType<DateTimePicker>().OrderBy(c => c.Left).ToArray();
                dates[0].Value = DateTime.Today.AddDays(-29);
                dates[1].Value = DateTime.Today;
                var chart = All(form).Single(c => c.GetType().Name == "ReportBarChart");
                var labels = (string[])chart.GetType().GetProperty("Labels")!.GetValue(chart)!;
                Expect(labels.Length == 30, "30-day filter still plots 7 days.");
            });
            Check("60 page transitions remain responsive and render", () =>
            {
                form.Size = new Size(1600, 900);
                for (var i = 0; i < 30; i++) { Page(form, "ShowFbs"); Page(form, "ShowReport"); Application.DoEvents(); }
                var dir = Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "MarketplaceHub-screenshots");
                Directory.CreateDirectory(dir);
                foreach (var page in new[] { "ShowReport", "ShowFbs", "ShowSyncCenter" })
                {
                    Page(form, page); Application.DoEvents();
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(dir, page + ".png"));
                }
            });
            Check("Delayed FBS detail completion never replaces a new page", () =>
            {
                var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                var handler = new AsyncFixtureHttp((r, ct) =>
                {
                    Interlocked.Increment(ref requests);
                    return response.Task;
                });
                typeof(MarketplaceGateway).GetField("http", instance)!.SetValue(app.Api, new HttpClient(handler));
                var order = app.Db.Orders(store.Id).First();
                typeof(MainForm).GetMethod("ShowFbsSupplyDetail", instance)!.Invoke(form, new object?[] { order, null });
                using var accept = new System.Windows.Forms.Timer { Interval = 10 };
                accept.Tick += (_, _) => { var chooser = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().Name == "MarketplaceBatchDialog"); if (chooser is not null) { accept.Stop(); chooser.DialogResult = DialogResult.OK; chooser.Close(); } };
                accept.Start();
                All(form).OfType<Button>().Single(b => b.Text.Contains("Tạo shipment")).PerformClick();
                Expect(requests > 0, "Shipment request never started.");
                Page(form, "ShowReport");
                response.SetResult(Json("{\"order\":{\"id\":1,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2}]}}"));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
                Expect(All(form).Any(c => c.Text == "Báo cáo"), "Old completion navigated back to FBS detail.");
            });
            Check("Delayed images never attach to a replacement row", () =>
            {
                Page(form, "ShowReport");
                var work = (Panel)typeof(MainForm).GetField("work", instance)!.GetValue(form)!;
                var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(MainForm).GetField("imageHttp", instance)!.SetValue(form,
                    new HttpClient(new AsyncFixtureHttp((_, _) => response.Task)));
                var grid = new DataGridView { Width = 300, Height = 200, AllowUserToAddRows = false };
                grid.Columns.Add(new DataGridViewImageColumn());
                grid.Rows.Add(); work.Controls.Add(grid); grid.BringToFront(); Application.DoEvents();
                var load = (Task)typeof(MainForm).GetMethod("LoadImageAsync", instance)!.Invoke(form, new object[] { grid, 0, 0, "https://fixture.invalid/image.png" })!;
                grid.Rows.Clear(); grid.Rows.Add();
                using var bitmap = new Bitmap(1600, 2400); using var data = new MemoryStream();
                bitmap.Save(data, System.Drawing.Imaging.ImageFormat.Png);
                response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) });
                Pump(load);
                Expect(grid.Rows[0].Cells[0].Value is not Image, "Old product photo was attached to a replacement row.");
                var next = (Task)typeof(MainForm).GetMethod("LoadImageAsync", instance)!.Invoke(form, new object[] { grid, 0, 0, "https://fixture.invalid/image.png" })!;
                Pump(next);
                var thumbnail = grid.Rows[0].Cells[0].Value as Image;
                Expect(thumbnail is not null && thumbnail.Width <= 160 && thumbnail.Height <= 160, "Grid kept full-resolution bitmaps, allowing large orders lists to exhaust memory.");
            });
            Check("Selecting one line checks mandatory KIZ for the whole posting", () =>
            {
                var count = 0;
                typeof(MarketplaceGateway).GetField("http", instance)!.SetValue(app.Api,
                    new HttpClient(new AsyncFixtureHttp((_, _) => { count++; return Task.FromResult(Json("{\"order\":{\"id\":1,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2},{\"id\":3,\"offerId\":\"B\",\"count\":2,\"hasCis\":true}]}}")); })));
                var a = app.Db.Orders(store.Id).First();
                app.Db.ReplaceProducts(store.Id, Marketplace.Yandex, new[] {
                    new ProductRow(store.Id, Marketplace.Yandex, "A", "A", "Quần A", 1250, "", "{}"),
                    new ProductRow(store.Id, Marketplace.Yandex, "B", "B", "Quần B", 1350, "", "{}") });
                app.Db.UpsertOrders(store.Id, Marketplace.Yandex, new[] { a with { Sku = "B", Name = "Quần B", NeedsKiz = true } });
                if(!app.Db.MarketplaceReceivedOrderIds(store).Contains(a.ExternalOrderId))app.Db.CreateMarketplaceFbsBatch(store,new[]{a.ExternalOrderId});
                Page(form, "ShowFbs");
                var packed = (Task<PriceUpdateResult>)typeof(MainForm).GetMethod("CreateShipmentWithKizAsync", instance)!
                    .Invoke(form, new object[] { store, new[] { a }, true })!;
                Pump(packed);
                Expect(!packed.Result.Success && count > 0 && packed.Result.Message.Contains("GTIN"), "Unselected mandatory-KIZ line was skipped before shipping. Reads="+count+"; result="+packed.Result.Message);
            });
            Check("Partial sync and full sync never overlap for one store", () =>
            {
                var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;var paths=new System.Collections.Concurrent.ConcurrentQueue<string>();
                typeof(MarketplaceGateway).GetField("http", instance)!.SetValue(app.Api,
                    new HttpClient(new AsyncFixtureHttp((request, _) =>
                    {
                        Interlocked.Increment(ref requests);paths.Enqueue(request.RequestUri!.AbsolutePath);
                        if(request.RequestUri.AbsolutePath.EndsWith("/1"))return Task.FromResult(Json("{\"order\":{\"id\":1,\"status\":\"PROCESSING\",\"substatus\":\"STARTED\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2},{\"id\":3,\"offerId\":\"B\",\"count\":1}]}}"));
                        return response.Task;
                    })));
                var partial = app.SyncOrdersAsync(store);
                var full = app.SyncStoreAsync(store);
                var blocked = full.IsCompletedSuccessfully && !full.Result.Ok;
                response.SetResult(Json("{\"status\":\"OK\",\"orders\":[],\"result\":{\"offerMappings\":[]}}"));
                Pump(partial); Pump(full);
                Expect(blocked && partial.Result.Ok && requests == 2 && paths.All(x=>x.Contains("/orders")), "Full sync bypassed the store lock or cached-order reconciliation did not run exactly once.");
            });
            Check("KIZ barcode is GS1 DataMatrix with exact separator payload", () =>
            {
                const string code = "010460123456789321serialABCDEFG\u001d91ABCD\u001d92proof";
                foreach (var input in new[] { code, "\u001d" + code })
                {
                    var pixels = (ZXing.Rendering.PixelData)typeof(MainForm)
                        .GetMethod("CreateKizBarcode", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new[] { input })!;
                    var result = new ZXing.BarcodeReaderGeneric().Decode(pixels.Pixels, pixels.Width, pixels.Height,
                        ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
                    Expect(result is not null && result.RawBytes[0] == 232 && result.Text.TrimStart('\u001d') == code,
                        "KIZ must start with FNC1 and preserve the full AI/GS data without duplicating scanner prefixes.");
                }
            });
            Check("WB print uses the selected size instead of the card's first size", () =>
            {
                var order = new FbsOrderRow(store.Id,Marketplace.Wildberries,"101","SKU","fixture",1,"confirm",false,"{\"chrtId\":2,\"skus\":[\"4601234567886\"]}");
                var product = new ProductRow(store.Id,Marketplace.Wildberries,"100","SKU","fixture",null,"","{\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"4601234567893\"]},{\"chrtID\":2,\"techSize\":\"50\",\"skus\":[\"4601234567886\"]}]}");
                var result = (WbPrintOrder)typeof(MainForm).GetMethod("BuildWbPrintOrder",instance)!.Invoke(form,new object[] {order,product,new LabelResult(true,"fixture",null,"official","1","2"),Array.Empty<string>(),true})!;
                Expect(result.Size=="50" && result.Barcode=="4601234567886","The first card size/barcode was printed for another variant.");
            });
            Check("WB print options and preview render without retaining image handles", () =>
            {
                var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");
                Directory.CreateDirectory(dir);
                using var options = new WbPrintOptionsDialog(true);options.Show(form);Application.DoEvents();
                Expect(options.Options.IncludeKiz && options.Options.ProductCopies==1,"Print defaults changed.");
                using(var picture=new Bitmap(options.Width,options.Height)) {options.DrawToBitmap(picture,new Rectangle(Point.Empty,options.Size));picture.Save(Path.Combine(dir,"WbPrintOptions.png"));}
                options.Close();
                var order=new WbPrintOrder("101","Куртка","SKU","Черный","48","Brand","4601234567893",1,false,Array.Empty<string>(),new LabelResult(true,"fixture",null,"!official-101","231648","9753"));
                var bundle=new WbPrintBundleService().Prepare("fixture",new[]{order},new WbPrintOptions(true,false,false,1),Path.Combine(Path.GetTempPath(),"MarketplaceHub-ui-print"));
                using var preview=new WbPrintPreviewDialog(bundle,_=>{});preview.Show(form);Application.DoEvents();
                using(var picture=new Bitmap(preview.Width,preview.Height)) {preview.DrawToBitmap(picture,new Rectangle(Point.Empty,preview.Size));picture.Save(Path.Combine(dir,"WbPrintPreview.png"));}
                Expect(All(preview).OfType<PictureBox>().Single().Image is not null,"Prepared label was not previewed.");
                preview.Close();
            });
            Check("WB rejects a stale sole catalog size when order identity disagrees", () =>
            {
                var order=new FbsOrderRow(store.Id,Marketplace.Wildberries,"101","SKU","fixture",1,"confirm",false,"{\"chrtId\":2,\"skus\":[\"4601234567886\"]}");
                var product=new ProductRow(store.Id,Marketplace.Wildberries,"100","SKU","fixture",null,"","{\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"4601234567893\"]}]}");
                var rejected=false;
                try {typeof(MainForm).GetMethod("BuildWbPrintOrder",instance)!.Invoke(form,new object[] {order,product,new LabelResult(true,"fixture",null,"official","1","2"),Array.Empty<string>(),true});}
                catch(TargetInvocationException ex) {rejected=ex.InnerException is InvalidOperationException;}
                Expect(rejected,"Wrong sole catalog size was silently printed.");
            });
            Check("WB submission stays successful when history persistence fails", () =>
            {
                var bundle=new WbPrintBundle("fixture","fixture.pdf","details.pdf","job.json",new[]{new WbPrintPage("1","sticker","fixture.png")});
                var result=WbPrintPreviewDialog.RecordSubmission(bundle,"fixture",1,_=>throw new IOException("audit disk full"),(_,_)=>throw new IOException("file disk full"));
                Expect(result.Contains("Đã gửi 1/1") && result.Contains("lịch sử") && !result.Contains("Chưa gửi"),"History failure changed the known successful spool outcome.");
            });
            Check("WB printer orientation and spool copies are independent of driver defaults", () =>
            {
                using var document=WbPrintPreviewDialog.CreatePrintDocument("fixture");
                Expect(!document.DefaultPageSettings.Landscape && document.PrinterSettings.Copies==1,"Driver defaults override prepared page order/copies.");
                Expect(document.DefaultPageSettings.PaperSize.Width==228 && document.DefaultPageSettings.PaperSize.Height==157,"58×40 paper size was lost.");
            });
            Check("WB header select-all filters rows and reveals receipt action", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-selection","","","","","fixture",true));
                try {
                    app.Db.UpsertOrders(wb.Id,Marketplace.Wildberries,new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"101","A","Quần nam A",1,"new",false,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"102","B","Quần nam B",1,"new",false,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"103","C","Bộ thể thao",1,"new",false,"{}")});
                    var observed=DateTimeOffset.UtcNow;
                    app.Db.UpsertOrderRemoteStates(wb.Id,Marketplace.Wildberries,new Dictionary<string,OrderRemoteState>(StringComparer.Ordinal){
                        ["101"]=new("new","waiting",true,observed),["102"]=new("new","waiting",true,observed),["103"]=new("new","waiting",true,observed)});
                    typeof(MainForm).GetMethod("RefreshStores",instance)!.Invoke(form,new object?[]{0L});
                    for(var i=0;i<picker.Items.Count;i++)if(picker.Items[i] is StoreProfile s && s.Id==wb.Id)picker.SelectedIndex=i;
                    Page(form,"ShowFbs");Application.DoEvents();
                    var grid=All(form).OfType<DataGridView>().Single(x=>x.Name=="fbsNewOrders");
                    Expect(grid.Columns[0] is DataGridViewCheckBoxColumn && grid.Columns[0].HeaderText=="☐" && grid.Columns[1].HeaderText=="ORDER ID","Select-all checkbox is not in the header beside ORDER ID.");
                    typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(grid,new object[]{new DataGridViewCellMouseEventArgs(0,-1,1,1,new MouseEventArgs(MouseButtons.Left,1,1,1,0))});
                    Application.DoEvents();
                    Expect(grid.Rows.Count==3 && grid.Rows.Cast<DataGridViewRow>().All(r=>r.Cells[0].Value is true),"Header select-all did not select every visible order.");
                    var search=All(form).OfType<TextBox>().Single(x=>x.PlaceholderText.Contains("Tìm theo đơn"));search.Text="Quần";Application.DoEvents();
                    Expect(grid.Rows.Count==2 && grid.Rows.Cast<DataGridViewRow>().All(r=>r.Cells[0].Value is true),"Filtering lost selected visible orders.");
                    Expect(All(form).Any(x=>x.Text=="Nhận đơn (2)" && x.Visible),"Receipt action did not appear after header selection.");
                    Expect(!All(form).Any(x=>x.Visible && x is Button && (x.Text.Contains("Xuất nhãn")||x.Text.Contains("In nhãn")||x.Text.Contains("Tự động KIZ"))),"New-order page exposes KIZ/printing before shipment receipt.");
                } finally {app.Db.DeleteStore(wb.Id);typeof(MainForm).GetMethod("RefreshStores",instance)!.Invoke(form,new object?[]{0L});}
            });
            Check("WB reserved KIZ stays with its shop and order across retries and imports", () =>
            {
                var a=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-reserve-A","","","","","fixture",true));
                var b=app.Db.SaveStore(a with{Id=0,Name=marker+"-reserve-B"});
                const string gtin="04609999999999";
                var code1="01"+gtin+"21"+Guid.NewGuid().ToString("N");var code2="01"+gtin+"21"+Guid.NewGuid().ToString("N");
                try {
                    app.Db.UpsertKiz(code1,gtin,"AVAILABLE");app.Db.UpsertKiz(code2,gtin,"AVAILABLE");
                    var first=app.Db.ReserveWbKiz(a.Id,"1",gtin);
                    var retry=app.Db.ReserveWbKiz(a.Id,"1",gtin);
                    var other=app.Db.ReserveWbKiz(b.Id,"1",gtin);
                    Expect(first==retry && first!=other && first is not null && other is not null,"Reservation was reused by another shop or changed on retry.");
                    app.Db.UpsertKiz(first!,gtin,"AVAILABLE");
                    Expect(app.Db.Kiz().Single(x=>x.Code==first).Status=="RESERVED","Reimport released a reserved KIZ.");
                    app.Db.ConfirmWbKiz(a.Id,"1",gtin,first!);
                    Expect(app.Db.Kiz().Single(x=>x.Code==first).Status=="ASSIGNED","Confirmed KIZ state was not retained.");
                } finally {app.Db.DeleteStore(a.Id);app.Db.DeleteStore(b.Id);}
            });
            Check("WB uncertain KIZ PUT resumes by reading metadata without assigning a new code", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-retry","","","","","fixture",true));
                const string gtin="04601234567893";var code="01"+gtin+"21"+Guid.NewGuid().ToString("N")[..13]+"\u001d91ABCD\u001d92proof";
                var row=new FbsOrderRow(wb.Id,Marketplace.Wildberries,"777","SKU","fixture",1,"confirm",true,"{}");
                var httpField=typeof(MarketplaceGateway).GetField("http",instance)!;var old=httpField.GetValue(app.Api);
                var applied=false;var puts=0;
                var client=new HttpClient(new AsyncFixtureHttp((r,ct)=>{
                    var path=r.RequestUri!.AbsolutePath;
                    if(path.EndsWith("/status"))return Task.FromResult(Json("{\"orders\":[{\"id\":777,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}"));
                    if(path.EndsWith("/order-ids"))return Task.FromResult(Json("{\"orderIds\":[777]}"));
                    if(path.EndsWith("/supplies/RETRY"))return Task.FromResult(Json("{\"id\":\"RETRY\",\"name\":\"fixture\",\"done\":false,\"createdAt\":\"2026-10-02T00:00:00Z\"}"));
                    if(r.Method==HttpMethod.Put){puts++;applied=true;throw new HttpRequestException("connection lost after WB accepted request");}
                    return Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(new{orders=new[]{new{id=777,metaDetails=new[]{new{key="sgtin",value=applied?code:null,decision=applied?"filled":"required"}}}}})));
                }));
                try {
                    app.Db.UpsertKiz(code,gtin,"AVAILABLE");app.Db.StoreWbSupplyMembership(wb.Id,"RETRY",new[]{"777"});httpField.SetValue(app.Api,client);
                    var ids=new Dictionary<string,string>{{"777",gtin}};
                    app.Db.UpsertOrders(wb.Id,wb.Marketplace,new[]{row});var preparation=WorkflowTestSupport.PreparedContext(app,new(wb.Id,wb.Marketplace,LabelTargetKind.WbSupply,"RETRY"),new[]{code},wbGtin:_=>gtin);Pump(preparation);var context=preparation.Result;
                    var first=app.EnsureWbSupplyKizAsync(wb,new[]{row},ids,true,workflowContext:context);Pump(first);
                    Expect(!first.Result.Success && app.Db.Kiz().Single(x=>x.Code==code).Status=="RESERVED","Uncertain code was released or marked confirmed.");
                    var retry=app.EnsureWbSupplyKizAsync(wb,new[]{row},ids,true,workflowContext:context);Pump(retry);
                    Expect(retry.Result.Success && puts==1 && app.Db.Kiz().Single(x=>x.Code==code).Status=="ASSIGNED","Retry sent another KIZ or did not confirm remote code.");
                } finally {httpField.SetValue(app.Api,old);client.Dispose();app.Db.DeleteStore(wb.Id);}
            });
            Check("WB shipment choices show only today's open supplies", () =>
            {
                var now=DateTimeOffset.UtcNow;
                using var dialog=new WbShipmentDialog(119,new[]{new WbSupply("OPEN","Today",now,false),new WbSupply("OLD","Yesterday",now.AddDays(-1),false),new WbSupply("CLOSED","Closed",now,true)},true);
                dialog.Show(form);Application.DoEvents();
                var combo=All(dialog).OfType<ComboBox>().Single();
                Expect(combo.Items.Count==1 && ((WbSupply)combo.Items[0]!).Id=="OPEN" && dialog.Choice.SupplyId=="OPEN","Unsafe shipment choices were offered.");
                var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);
                using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));image.Save(Path.Combine(dir,"WbShipmentChoice.png"));}
                dialog.Close();
            });
            Check("WB remote KIZ cannot steal another marketplace assignment", () =>
            {
                const string gtin="04607777777777";var code="01"+gtin+"21"+Guid.NewGuid().ToString("N");
                app.Db.UpsertKiz(code,gtin,"ASSIGNED","OZON-LEGACY-ORDER");
                var blocked=false;try{app.Db.ConfirmWbKiz(store.Id,"WB-ORDER",gtin,code);}catch(InvalidOperationException){blocked=true;}
                var saved=app.Db.Kiz().Single(x=>x.Code==code);
                Expect(blocked && saved.Status=="ASSIGNED" && saved.Assigned=="OZON-LEGACY-ORDER","WB overwrote another marketplace's KIZ owner.");
            });
            Check("WB marking validation rejects GTIN mismatch duplicates ownership and cancelled rows", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-validate","","","","","fixture",true));
                const string gtin="04606666666666";var code="01"+gtin+"21"+Guid.NewGuid().ToString("N");
                var rows=new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"901","A","A",1,"confirm",true,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"902","B","B",1,"confirm",true,"{}")};
                var gtins=rows.ToDictionary(x=>x.ExternalOrderId,_=>gtin);
                try {
                    var duplicated=rows.ToDictionary(x=>x.ExternalOrderId,_=>new WbPrintKizMetadata(true,new[]{code}));
                    Expect(!app.ValidateWbSupplyKiz(wb,rows,gtins,duplicated).Success,"Duplicate code was accepted for delivery.");
                    var one=new Dictionary<string,WbPrintKizMetadata>{{"901",new(true,new[]{code})}};
                    Expect(!app.ValidateWbSupplyKiz(wb,new[]{rows[0]},new Dictionary<string,string>{{"901","04605555555555"}},one).Success,"Wrong GTIN was accepted for delivery.");
                    Expect(!app.ValidateWbSupplyKiz(wb,new[]{rows[0] with{Status="cancel"}},gtins,one).Success,"Cancelled order was accepted for delivery.");
                    app.Db.UpsertKiz(code,gtin,"ASSIGNED","YANDEX-LEGACY-ORDER");
                    Expect(!app.ValidateWbSupplyKiz(wb,new[]{rows[0]},gtins,one).Success,"A code owned by another marketplace was accepted for delivery.");
                } finally {app.Db.DeleteStore(wb.Id);}
            });
            Check("WB invalid marking and cancelled membership never send deliver", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-deliver","","","","","fixture",true));
                const string gtin="04603333333333";
                var field=typeof(MarketplaceGateway).GetField("http",instance)!;var old=field.GetValue(app.Api);var deliveries=0;
                try {
                    foreach(var scenario in new[]{"gtin","duplicate","owner","cancel"}) {
                        var code="01"+gtin+"21"+Guid.NewGuid().ToString("N");var code2="01"+gtin+"21"+Guid.NewGuid().ToString("N");
                        app.Db.UpsertOrders(wb.Id,Marketplace.Wildberries,new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"991","A","A",1,"confirm",true,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"992","B","B",1,"confirm",true,"{}")});
                        if(scenario=="owner")app.Db.UpsertKiz(code,gtin,"ASSIGNED","OZON-OWNER");
                        using var client=new HttpClient(new AsyncFixtureHttp((r,_)=>{
                            var path=r.RequestUri!.AbsolutePath;
                            if(path.EndsWith("/deliver")){deliveries++;return Task.FromResult(Json("{}"));}
                            if(path.EndsWith("/order-ids"))return Task.FromResult(Json("{\"orderIds\":[991,992]}"));
                            if(path.EndsWith("/status"))return Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(new{orders=new[]{new{id=991,supplierStatus="confirm",wbStatus=scenario=="cancel"?"canceled":"waiting"},new{id=992,supplierStatus="confirm",wbStatus="waiting"}}})));
                            if(path.EndsWith("/meta"))return Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(new{orders=new[]{new{id=991,metaDetails=new[]{new{key="sgtin",value=new[]{code},decision="filled"}}},new{id=992,metaDetails=new[]{new{key="sgtin",value=new[]{scenario=="duplicate"?code:code2},decision="filled"}}}}})));
                            return Task.FromResult(Json("{\"id\":\"DELIVER-SAFE\",\"done\":false,\"createdAt\":\""+DateTimeOffset.UtcNow.ToString("O")+"\"}"));
                        }));
                        field.SetValue(app.Api,client);
                        var gtins=new Dictionary<string,string>{{"991",scenario=="gtin"?"04604444444444":gtin},{"992",gtin}};
                        var task=app.DeliverVerifiedWbSupplyAsync(wb,"DELIVER-SAFE",gtins);Pump(task);
                        Expect(!task.Result.Success,"Unsafe scenario was delivered: "+scenario);
                    }
                    Expect(deliveries==0,"A deliver request was sent before marking verification.");
                }finally{field.SetValue(app.Api,old);app.Db.DeleteStore(wb.Id);}
            });
            Check("WB server membership hydrates every cached row with current status", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-hydrate","","","","","fixture",true));
                var field=typeof(MarketplaceGateway).GetField("http",instance)!;var old=field.GetValue(app.Api);
                using var client=new HttpClient(new AsyncFixtureHttp((r,_)=>Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/order-ids")?Json("{\"orderIds\":[321,322]}"):Json("{\"orders\":[{\"id\":321,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"},{\"id\":322,\"supplierStatus\":\"confirm\",\"wbStatus\":\"waiting\"}]}"))));
                try {
                    app.Db.UpsertOrders(wb.Id,Marketplace.Wildberries,new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"321","A","A",1,"new",false,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"322","B","B",1,"new",false,"{}")});
                    field.SetValue(app.Api,client);var task=app.ReadWbSupplyOrdersAsync(wb,"FULL-SUPPLY");Pump(task);
                    Expect(task.Result.Count==2 && task.Result.All(x=>x.Status=="confirm" && app.Db.FindWbSupplyForOrder(wb.Id,x.ExternalOrderId)=="FULL-SUPPLY"),"Whole server membership was not hydrated and recorded.");
                }finally{field.SetValue(app.Api,old);app.Db.DeleteStore(wb.Id);}
            });
            Check("Product synchronization view paginates fifty exact variants", () =>
            {
                var catalogStore=app.Db.SaveStore(new StoreProfile(0,Marketplace.Yandex,marker+"-catalog","","fixture","456","789","",true));
                try {
                    var scope=ProductCatalog.Scope(catalogStore);app.Db.BeginProductCatalog(catalogStore,scope);
                    var entries=Enumerable.Range(1,51).Select(i=>ProductCatalog.Entry(new ProductRow(catalogStore.Id,Marketplace.Yandex,i.ToString(),"SKU-"+i,"Quần "+i,null,"","{\"offer\":{\"barcodes\":[\"4601234567893\"],\"size\":\"48\"}}"))).ToArray();
                    app.Db.ApplyProductCatalogPage(catalogStore,"",scope,new ProductCatalogPage(entries,"",true));
                    typeof(MainForm).GetMethod("RefreshStores",instance)!.Invoke(form,new object?[]{0L});
                    for(var i=0;i<picker.Items.Count;i++)if(picker.Items[i] is StoreProfile shop && shop.Id==catalogStore.Id)picker.SelectedIndex=i;
                    Page(form,"ShowProductSynchronization");
                    var deadline=DateTime.UtcNow.AddSeconds(8);
                    while(DateTime.UtcNow<deadline && All(form).OfType<DataGridView>().Single().Rows.Count==0){Application.DoEvents();Thread.Sleep(1);}
                    var grid=All(form).OfType<DataGridView>().Single();Expect(grid.Rows.Count==50,"Product view rendered all variants without pagination.");
                    var next=All(form).OfType<Button>().Single(b=>b.Text=="›");next.PerformClick();Application.DoEvents();Expect(grid.Rows.Count==1,"Second variant page was incorrect.");
                    var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(dir,"ProductSynchronization.png"));}
                }finally{app.Db.DeleteStore(catalogStore.Id);typeof(MainForm).GetMethod("RefreshStores",instance)!.Invoke(form,new object?[]{0L});}
            });
            Check("WB full supply detail renders all orders and verified KIZ state", () =>
            {
                var wb=app.Db.SaveStore(new StoreProfile(0,Marketplace.Wildberries,marker+"-detail","","","","","fixture",true));
                try {
                    var rows=new[]{new FbsOrderRow(wb.Id,Marketplace.Wildberries,"1","A","Quần nam",1,"confirm",true,"{}"),new FbsOrderRow(wb.Id,Marketplace.Wildberries,"2","B","Bộ thể thao",1,"confirm",true,"{}")};
                    app.Db.ReplaceProducts(wb.Id,Marketplace.Wildberries,new[]{new ProductRow(wb.Id,Marketplace.Wildberries,"100","A","Quần nam",1000,"","{\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"4601234567893\"]}]}"),new ProductRow(wb.Id,Marketplace.Wildberries,"200","B","Bộ thể thao",2000,"","{\"sizes\":[{\"chrtID\":2,\"techSize\":\"50\",\"skus\":[\"4601234567894\"]}]}")});
                    var marking=new Dictionary<string,WbPrintKizMetadata>{{"1",new(true,new[]{"010460123456789321fixture"})},{"2",new(true,Array.Empty<string>())}};
                    typeof(MainForm).GetMethod("ShowWbSupplyDetail",instance)!.Invoke(form,new object?[]{wb,new WbSupply("WB-GI-FIXTURE","Today",DateTimeOffset.UtcNow,false),rows,marking,null});
                    Application.DoEvents();
                    var grid=All(form).OfType<DataGridView>().Single();
                    Expect(grid.Columns["detail"].Width>=220,"WB product details collapsed at compact width.");
                    var supplyWork=(Panel)typeof(MainForm).GetField("work",instance)!.GetValue(form)!;var delivery=All(form).OfType<Button>().Single(b=>b.Text=="Chuyển sang giao hàng");Expect(delivery.Right<=supplyWork.ClientSize.Width,"Delivery action is clipped at compact width.");
                    Expect(grid.Rows.Count==2 && grid.Rows[0].Cells["kiz"].Value!.ToString()!.Contains("Đã tích KIZ") && grid.Rows[1].Cells["kiz"].Value!.ToString()!.Contains("Chưa gắn"),"Full shipment or confirmed KIZ status was missing.");
                    var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(dir,"WbSupplyDetail.png"));}
                } finally {app.Db.DeleteStore(wb.Id);}
            });

            Check("Ozon batch detail shows every posting and retained unit progress", () =>
            {
                var oz=app.Db.SaveStore(new StoreProfile(0,Marketplace.Ozon,marker+"-batch","123","fixture","","","",true));
                try {
                    app.Db.UpsertOrders(oz.Id,Marketplace.Ozon,new[]{new FbsOrderRow(oz.Id,Marketplace.Ozon,"P1","A","Quần nam",2,"awaiting_deliver",false,"{}"),new FbsOrderRow(oz.Id,Marketplace.Ozon,"P1","B","Áo khoác",1,"awaiting_deliver",false,"{}"),new FbsOrderRow(oz.Id,Marketplace.Ozon,"P2","C","Bộ thể thao",1,"awaiting_packaging",false,"{}")});
                    var batch=app.Db.CreateMarketplaceFbsBatch(oz,new[]{"P1","P2"});app.Db.SaveMarketplaceFbsOrder(oz,batch.Id,"P1","PACKED");app.Db.SaveMarketplaceFbsOrder(oz,batch.Id,"P2","ERROR","Cần đồng bộ barcode đúng biến thể");
                    typeof(MainForm).GetMethod("ShowMarketplaceFbsBatch",instance)!.Invoke(form,new object?[]{oz,batch.Id,null});Application.DoEvents();
                    var grid=All(form).OfType<DataGridView>().Single(g=>g.Name=="marketplaceBatchOrders");Expect(grid.Columns["items"].Width>=220,"Whole posting product column collapsed.");Expect(grid.Rows.Count==2 && grid.Rows[0].Cells["items"].Value!.ToString()!.Contains("Áo khoác") && grid.Rows[1].Cells["error"].Value!.ToString()!.Contains("barcode"),"Whole batch or recovery reason was hidden.");
                    var dir=Environment.GetEnvironmentVariable("MARKETPLACE_SCREENSHOTS")??Path.Combine(Path.GetTempPath(),"MarketplaceHub-screenshots");Directory.CreateDirectory(dir);using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(dir,"OzonFbsBatch.png"));
                }finally{app.Db.DeleteStore(oz.Id);}
            });

        }
        finally { app.Db.DeleteStore(store.Id); }
        Console.WriteLine($"{checks - failures.Count}/{checks} UI regressions passed");
        return failures.Count == 0 ? 0 : 1;
    }
}

sealed class AsyncFixtureHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
