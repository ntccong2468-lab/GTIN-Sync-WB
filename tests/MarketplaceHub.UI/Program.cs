using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.UI;
using System.Reflection;

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
        IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
        var app = new AppServices();
        var marker = "UI-FIXTURE-" + Guid.NewGuid().ToString("N");
        var store = app.Db.SaveStore(new StoreProfile(0, Marketplace.Yandex, marker, "123", "fixture", "456", "789", "fixture", true));
        app.Db.ReplaceProducts(store.Id, Marketplace.Yandex,
            new[] { new ProductRow(store.Id, Marketplace.Yandex, "A", "A", "Quần nam • A", 1250, "", "{}") });
        app.Db.UpsertOrders(store.Id, Marketplace.Yandex,
            new[] { new FbsOrderRow(store.Id, Marketplace.Yandex, "1", "A", "Quần nam • A", 2, "PROCESSING/STARTED", false, "{\"creationDate\":\"2026-09-30\"}") });
        try
        {
            using var form = new MainForm(app);
            var picker = (ComboBox)typeof(MainForm).GetField("storePicker", instance)!.GetValue(form)!;
            for (var i = 0; i < picker.Items.Count; i++)
                if (picker.Items[i] is StoreProfile s && s.Id == store.Id) picker.SelectedIndex = i;
            form.Show();
            Application.DoEvents();
            Check("Yandex READY_TO_SHIP appears only in shipping tab", () =>
                Expect(!State("IsNew", "PROCESSING/READY_TO_SHIP") && !State("IsPacking", "PROCESSING/READY_TO_SHIP") && State("IsShipping", "PROCESSING/READY_TO_SHIP"), "READY_TO_SHIP is shown in multiple tabs."));
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
        }
        finally { app.Db.DeleteStore(store.Id); }
        Console.WriteLine($"{6 - failures.Count}/6 UI regressions passed");
        return failures.Count == 0 ? 0 : 1;
    }
}
