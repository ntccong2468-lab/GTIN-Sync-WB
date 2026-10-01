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
        void Check(string name, Action test)
        {
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
        var app = new AppServices();
        var marker = "UI-FIXTURE-" + Guid.NewGuid().ToString("N");
        var store = app.Db.SaveStore(new StoreProfile(0, Marketplace.Yandex, marker, "123", "fixture", "456", "789", "fixture", true));
        app.Db.ReplaceProducts(store.Id, Marketplace.Yandex,
            new[] { new ProductRow(store.Id, Marketplace.Yandex, "A", "A", "Quần nam • A", 1250, "", "{}") });
        app.Db.UpsertOrders(store.Id, Marketplace.Yandex,
            new[] { new FbsOrderRow(store.Id, Marketplace.Yandex, "1", "A", "Quần nam • A", 2, "PROCESSING/STARTED", false, "{\"creationDate\":\"2026-09-30\",\"items\":[{\"id\":2,\"offerId\":\"A\",\"count\":2}]}") });
        try
        {
            using var form = new MainForm(app);
            var picker = (ComboBox)typeof(MainForm).GetField("storePicker", instance)!.GetValue(form)!;
            for (var i = 0; i < picker.Items.Count; i++)
                if (picker.Items[i] is StoreProfile s && s.Id == store.Id) picker.SelectedIndex = i;
            form.Show();
            Application.DoEvents();
            Check("Packed Ozon and Yandex orders remain available for label printing", () =>
            {
                foreach (var state in new[] { "awaiting_deliver", "PROCESSING/READY_TO_SHIP" })
                    Expect(!State("IsNew", state) && State("IsPacking", state) && !State("IsShipping", state), "Packed order is absent from packing or shown in multiple tabs.");
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
                    if (r.RequestUri!.AbsolutePath.EndsWith("/boxes")) return response.Task;
                    return Task.FromResult(Json("{\"status\":\"OK\",\"orders\":[]}"));
                });
                typeof(MarketplaceGateway).GetField("http", instance)!.SetValue(app.Api, new HttpClient(handler));
                var order = app.Db.Orders(store.Id).First();
                typeof(MainForm).GetMethod("ShowFbsSupplyDetail", instance)!.Invoke(form, new object?[] { order, null });
                All(form).OfType<Button>().Single(b => b.Text.Contains("Tạo shipment")).PerformClick();
                Expect(requests > 0, "Shipment request never started.");
                Page(form, "ShowReport");
                response.SetResult(Json("{\"status\":\"OK\"}"));
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
                    new HttpClient(new AsyncFixtureHttp((_, _) => { count++; return Task.FromResult(Json("{\"status\":\"OK\"}")); })));
                var a = app.Db.Orders(store.Id).First();
                app.Db.ReplaceProducts(store.Id, Marketplace.Yandex, new[] {
                    new ProductRow(store.Id, Marketplace.Yandex, "A", "A", "Quần A", 1250, "", "{}"),
                    new ProductRow(store.Id, Marketplace.Yandex, "B", "B", "Quần B", 1350, "", "{}") });
                app.Db.UpsertOrders(store.Id, Marketplace.Yandex, new[] { a with { Sku = "B", Name = "Quần B", NeedsKiz = true } });
                Page(form, "ShowFbs");
                var packed = (Task<PriceUpdateResult>)typeof(MainForm).GetMethod("CreateShipmentWithKizAsync", instance)!
                    .Invoke(form, new object[] { store, new[] { a }, true })!;
                Pump(packed);
                Expect(!packed.Result.Success && count == 0 && packed.Result.Message.Contains("GTIN"), "Unselected mandatory-KIZ line was skipped before shipping.");
            });
            Check("Partial sync and full sync never overlap for one store", () =>
            {
                var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                typeof(MarketplaceGateway).GetField("http", instance)!.SetValue(app.Api,
                    new HttpClient(new AsyncFixtureHttp((_, _) =>
                    {
                        Interlocked.Increment(ref requests);
                        return response.Task;
                    })));
                var partial = app.SyncOrdersAsync(store);
                var full = app.SyncStoreAsync(store);
                var blocked = full.IsCompletedSuccessfully && !full.Result.Ok;
                response.SetResult(Json("{\"status\":\"OK\",\"orders\":[],\"result\":{\"offerMappings\":[]}}"));
                Pump(partial); Pump(full);
                Expect(blocked && requests == 1, "Manual order sync bypassed the store lock used by background sync.");
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
                var order = new FbsOrderRow(store.Id,Marketplace.Wildberries,"101","SKU","fixture",1,"confirm",false,"{\"chrtId\":2,\"skus\":[\"2222222222222\"]}");
                var product = new ProductRow(store.Id,Marketplace.Wildberries,"100","SKU","fixture",null,"","{\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"1111111111111\"]},{\"chrtID\":2,\"techSize\":\"50\",\"skus\":[\"2222222222222\"]}]}");
                var result = (WbPrintOrder)typeof(MainForm).GetMethod("BuildWbPrintOrder",instance)!.Invoke(form,new object[] {order,product,new LabelResult(true,"fixture",null,"official","1","2"),Array.Empty<string>(),true})!;
                Expect(result.Size=="50" && result.Barcode=="2222222222222","The first card size/barcode was printed for another variant.");
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
                var order=new FbsOrderRow(store.Id,Marketplace.Wildberries,"101","SKU","fixture",1,"confirm",false,"{\"chrtId\":2,\"skus\":[\"2222222222222\"]}");
                var product=new ProductRow(store.Id,Marketplace.Wildberries,"100","SKU","fixture",null,"","{\"sizes\":[{\"chrtID\":1,\"techSize\":\"48\",\"skus\":[\"1111111111111\"]}]}");
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
        }
        finally { app.Db.DeleteStore(store.Id); }
        Console.WriteLine($"{16 - failures.Count}/16 UI regressions passed");
        return failures.Count == 0 ? 0 : 1;
    }
}

sealed class AsyncFixtureHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
