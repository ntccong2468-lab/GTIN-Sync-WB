using MarketplaceHub.Core;
using MarketplaceHub.Services;
using SkiaSharp;
using System.Collections.Concurrent;
using ZXing;
using ZXing.Common;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed partial class MainForm : Form
{
    private readonly AppServices app;
    private readonly Panel work = new() { Dock = DockStyle.Fill, BackColor = C.Main };
    private readonly ComboBox storePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 275 };
    private readonly Label statusLabel = new() { AutoSize = true, ForeColor = C.Green, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly RoundedButton syncButton = new() { Text = "↻  Đồng bộ", Width = 175, Height = 44 };
    private readonly HttpClient imageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, byte[]> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource pageCts = new();
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly SemaphoreSlim imageWorkers = new(4, 4);
    private readonly SemaphoreSlim fbsOperations = new(1, 1);
    private readonly Dictionary<long, IReadOnlyList<ProductRow>> pageProducts = new();
    private readonly System.Windows.Forms.Timer autoSync = new() { Interval = 60_000 };
    private bool autoSyncRunning;
    private bool resourcesDisposed;
    private Action? refreshActivePage;
    private Action? activePage;
    private EventHandler? activeWorkResize;
    private readonly SemaphoreSlim znakWorkers = new(2, 2);
    private readonly ConcurrentDictionary<string, string> znakQueueStatus = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool znakQueuePaused;

    public MainForm(AppServices services)
    {
        app = services;
        Text = "Marketplace Hub";
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "MarketplaceHub.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
            else Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch { }
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1280, 780);
        Size = new Size(1600, 900);
        Font = new Font("Segoe UI", 10);
        BackColor = C.Main;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = C.Main };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        shell.Controls.Add(BuildSidebar(), 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = C.Main };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(BuildTopbar(), 0, 0);
        right.Controls.Add(work, 0, 1);
        shell.Controls.Add(right, 1, 0);

        RefreshStores();
        ShowDashboard();
        autoSync.Tick += async (_, _) =>
        {
            if (autoSyncRunning || IsDisposed) return;
            autoSyncRunning = true;
            try
            {
                foreach (var store in app.Db.Stores().Where(x => x.Enabled))
                {
                    var result = await app.SyncStoreAsync(store, lifetimeCts.Token);
                    if (IsDisposed || lifetimeCts.IsCancellationRequested) break;
                    if (CurrentStore()?.Id == store.Id)
                    {
                        pageProducts.Remove(store.Id);
                        refreshActivePage?.Invoke();
                        statusLabel.Text = result.Ok ? "• Đã cập nhật " + DateTime.Now.ToString("HH:mm:ss") : "• Có lỗi đồng bộ";
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { autoSyncRunning = false; }
        };
        autoSync.Start();
    }

    private Control BuildSidebar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Side };
        p.Paint += (_, e) => e.Graphics.DrawLine(new Pen(C.Border), p.Width - 1, 0, p.Width - 1, p.Height);

        var logoBox = new BrandMarkControl { Width = 48, Height = 48, Left = 20, Top = 10 };
        p.Controls.Add(logoBox);
        p.Controls.Add(new Label { Text = "Marketplace Hub", AutoSize = true, Left = 78, Top = 19, ForeColor = C.OnAccent, Font = new Font("Segoe UI", 13, FontStyle.Bold) });

        var design = NavButton("✎  Thiết kế mẫu", C.Orange, 238);
        design.Left = 13; design.Top = 75; design.Click += (_, _) => ShowDesignTools(); p.Controls.Add(design);

        var addStore = NavButton("＋  Thêm cửa hàng", C.Purple, 238);
        addStore.Left = 13; addStore.Top = 133; addStore.Click += (_, _) =>
        {
            var gate = app.License.CanOpenAddStore();
            if (!gate.Allowed) { ShowInfo(gate.Message); return; }
            ShowStores(true);
        }; p.Controls.Add(addStore);


        var menu = new FlowLayoutPanel
        {
            Left = 13, Top = 225, Width = 244, Height = 410,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = C.Side,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
        };
        p.Controls.Add(menu);

        AddSide(menu, "▦  Báo cáo", ShowReport);
        AddSide(menu, "↻  Đồng bộ dữ liệu", ShowSyncCenter);
        AddSide(menu, "▦  Đồng bộ sản phẩm", ShowProductSynchronization);
        AddSide(menu, "₽  Thay đổi giá", ShowPriceTools);
        AddSide(menu, "⇄  Sao chép bài đăng", ShowCopyListing);
        AddSide(menu, "▱  Đóng hàng FBS", ShowFbs);
        AddSide(menu, "◇  Đóng hàng FBO", ShowFboPacking);
        AddSide(menu, "☷  Đơn hàng FBO", ShowFboOrders);
        AddSide(menu, "⌁  Ánh xạ KIZ", ShowKizMapping);
        AddSide(menu, "▣  Đăng ký Znack", ShowZnakRegistration);
        AddSide(menu, "◇  Cấu hình Znack", ShowZnakSettings);
        AddSide(menu, "⌕  Kiểm tra tích hợp", ShowIntegrationTestCenter);

        var printHistory = DarkOutline("◷  Lịch sử in", 238);
        printHistory.Left = 13; printHistory.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        printHistory.Top = 637; printHistory.Click += (_, _) => ShowPrintHistory(); p.Controls.Add(printHistory);

        var settings = DarkOutline("⚙  Cài đặt                   ⌄", 238);
        settings.Left = 13; settings.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        settings.Top = 770; settings.Click += (_, _) => ShowStores(false); p.Controls.Add(settings);

        statusLabel.Text = "• Đang hoạt động";
        statusLabel.Left = 14; statusLabel.Top = 842; statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        p.Controls.Add(statusLabel);

        p.Resize += (_, _) =>
        {
            printHistory.Top = p.ClientSize.Height - 168;
            menu.Height = Math.Max(100, printHistory.Top - menu.Top - 12);
            settings.Top = p.ClientSize.Height - 100;
            statusLabel.Top = p.ClientSize.Height - 32;
        };

        return p;
    }

    private Control BuildTopbar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Main };
        p.Paint += (_, e) => e.Graphics.DrawLine(new Pen(C.Border), 0, p.Height - 1, p.Width, p.Height - 1);

        storePicker.Left = 20; storePicker.Top = 8; storePicker.Height = 68;
        storePicker.BackColor = C.Card; storePicker.ForeColor = C.Text;
        storePicker.Font = new Font("Segoe UI", 11);
        ConfigureDarkCombo(storePicker);
        storePicker.SelectedIndexChanged += (_, _) => { UpdateSyncButton(); activePage?.Invoke(); };
        p.Controls.Add(storePicker);

        var edit = IconButton("✎"); edit.Left = 310; edit.Top = 18; edit.Click += (_, _) => ShowStores(false); p.Controls.Add(edit);
        var del = IconButton("♲"); del.Left = 360; del.Top = 18; del.Click += (_, _) => DeleteCurrentStore(); p.Controls.Add(del);

        syncButton.BackColor = C.Purple; syncButton.ForeColor = C.OnAccent; syncButton.BorderColor = C.Purple;
        syncButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; syncButton.Top = 20;
        syncButton.Click += async (_, _) => await SyncAllForCurrentStore();
        p.Controls.Add(syncButton);

        var search = DarkText("Tìm SKU, đơn hàng, sản phẩm...", 330);
        search.Left = 415; search.Top = 20;
        search.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ShowLocalSearch(search.Text.Trim());
            }
        };
        p.Controls.Add(search);

        var help = NavButton("⌕  Hỗ trợ", C.Purple, 118);
        help.Anchor = AnchorStyles.Top | AnchorStyles.Right; help.Top = 20;
        help.TextAlign = ContentAlignment.MiddleCenter;
        help.Padding = Padding.Empty;
        help.Click += (_, _) => ShowSupportDialog();
        p.Controls.Add(help);

        p.Resize += (_, _) =>
        {
            help.Left = p.ClientSize.Width - help.Width - 10;
            syncButton.Left = help.Left - syncButton.Width - 14;
            var searchSpace = syncButton.Left - search.Left - 12;
            search.Visible = searchSpace >= 160;
            search.Width = Math.Max(160, searchSpace);
        };

        return p;
    }

    private void AddSide(FlowLayoutPanel menu, string text, Action action)
    {
        var b = NavButton(text, C.Purple, 216);
        b.Margin = new Padding(0, 0, 0, 14);
        b.Click += (_, _) => action();
        menu.Controls.Add(b);
    }

    private RoundedButton NavButton(string text, Color bg, int width)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 45, Radius = 8,
            BackColor = bg, ForeColor = C.OnAccent, BorderColor = bg,
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 10, 0)
        };
    }

    private RoundedButton DarkOutline(string text, int width)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 42, Radius = 8,
            BackColor = C.Side, ForeColor = C.OnAccent, BorderColor = Color.FromArgb(255, 255, 255, 70),
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 10, 0)
        };
    }

    private RoundedButton IconButton(string text)
    {
        return new RoundedButton
        {
            Text = text, Width = 40, Height = 40, Radius = 8,
            BackColor = C.Main, ForeColor = C.Text, BorderColor = C.Main,
            Font = new Font("Segoe UI Symbol", 16), Cursor = Cursors.Hand
        };
    }

    private RoundedButton ActionButton(string text, int width = 130, bool purple = false)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 42, Radius = 8,
            BackColor = purple ? C.Purple : C.Card,
            ForeColor = purple ? C.OnAccent : C.Text,
            BorderColor = purple ? C.Purple : C.Border,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
    }

    private StoreProfile? CurrentStore() => storePicker.SelectedItem as StoreProfile;

    private void RefreshStores(long selectId = 0)
    {
        var old = selectId != 0 ? selectId : CurrentStore()?.Id ?? 0;
        var stores = app.Db.Stores().ToList();
        storePicker.DataSource = null;
        storePicker.DisplayMember = nameof(StoreProfile.Name);
        storePicker.DataSource = stores;
        if (old != 0)
        {
            var idx = stores.FindIndex(x => x.Id == old);
            if (idx >= 0) storePicker.SelectedIndex = idx;
        }
        UpdateSyncButton();
    }

    private void UpdateSyncButton()
    {
        syncButton.Text = CurrentStore()?.Marketplace switch
        {
            Marketplace.Wildberries => "↻  Đồng bộ WB",
            Marketplace.Ozon => "↻  Đồng bộ Ozon",
            Marketplace.Yandex => "↻  Đồng bộ Yandex",
            _ => "↻  Đồng bộ"
        };
    }

    private async Task SyncAllForCurrentStore()
    {
        var s = CurrentStore();
        if (s is null) { ShowInfo("Chưa có cửa hàng. Hãy thêm cửa hàng trước."); return; }

        syncButton.Enabled = false;
        statusLabel.Text = "• Đang đồng bộ...";
        try
        {
            var result = await app.SyncStoreAsync(s, lifetimeCts.Token);
            if (IsDisposed || lifetimeCts.IsCancellationRequested) return;
            pageProducts.Remove(s.Id);
            if (CurrentStore()?.Id == s.Id) refreshActivePage?.Invoke();
            statusLabel.Text = result.Ok ? "• Đang hoạt động" : "• Có lỗi đồng bộ";
            statusLabel.ForeColor = result.Ok ? C.Green : C.Danger;
            ShowInfo(result.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!syncButton.IsDisposed) syncButton.Enabled = true;
        }
    }

    private void DeleteCurrentStore()
    {
        var s = CurrentStore();
        if (s is null) return;
        if (MessageBox.Show($"Xóa cửa hàng '{s.Name}' và toàn bộ dữ liệu đã đồng bộ?", "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        app.Db.DeleteStore(s.Id);
        RefreshStores();
        ShowDashboard();
    }

    private void ClearWork()
    {
        try { pageCts.Cancel(); } catch { }
        pageCts.Dispose();
        pageCts = new CancellationTokenSource();
        refreshActivePage = null;
        activePage = null;
        pageProducts.Clear();

        if (activeWorkResize is not null)
        {
            work.Resize -= activeWorkResize;
            activeWorkResize = null;
        }

        DisposeImages(work);
        foreach (var control in work.Controls.Cast<Control>().ToArray()) control.Dispose();
        work.Controls.Clear();
        work.Padding = new Padding(28, 26, 26, 24);
        work.AutoScroll = true;
    }

    private void SetWorkResize(EventHandler handler)
    {
        if (activeWorkResize is not null) work.Resize -= activeWorkResize;
        activeWorkResize = handler;
        work.Resize += activeWorkResize;
        activeWorkResize(work, EventArgs.Empty);
    }

    private static void DisposeImages(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is PictureBox picture)
            {
                picture.Image?.Dispose();
                picture.Image = null;
            }
            if (control is DataGridView grid)
            {
                foreach (DataGridViewRow row in grid.Rows)
                    foreach (DataGridViewCell cell in row.Cells)
                        if (cell.Value is Image image) image.Dispose();
            }
            if (control.HasChildren) DisposeImages(control);
        }
    }

    private Label Title(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = C.Text, Font = new Font("Segoe UI", 18, FontStyle.Bold), Left = 4, Top = 5 };
    }

    private DataGridView DarkGrid()
    {
        var g = new DataGridView
        {
            BackgroundColor = C.Card, BorderStyle = BorderStyle.None,
            RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false, GridColor = C.Border, ColumnHeadersHeight = 58
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = C.Card;
        g.ColumnHeadersDefaultCellStyle.ForeColor = C.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = C.Soft;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = C.Text;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = C.Card;
        g.AlternatingRowsDefaultCellStyle.BackColor = C.RowAlt;
        g.DefaultCellStyle.ForeColor = C.Text;
        g.DefaultCellStyle.SelectionBackColor = C.Soft;
        g.DefaultCellStyle.SelectionForeColor = C.Text;
        g.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
        g.RowTemplate.Height = 38;
        return g;
    }

    private RoundedPanel CardPanel()
    {
        return new RoundedPanel { Radius = 12, BackColor = C.Card, BorderColor = C.Border, BorderWidth = 1 };
    }

    private TextBox DarkText(string placeholder, int width)
    {
        return new TextBox
        {
            Width = width, Height = 44, PlaceholderText = placeholder,
            BackColor = C.Card, ForeColor = C.Text, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10)
        };
    }

    private ComboBox DarkCombo(int width)
    {
        var combo = new ComboBox
        {
            Width = width, Height = 44, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = C.Card, ForeColor = C.Text, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10)
        };
        ConfigureDarkCombo(combo);
        return combo;
    }

    private void ConfigureDarkCombo(ComboBox combo)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 30;
        combo.IntegralHeight = false;
        combo.DropDownHeight = 240;
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using var bg = new SolidBrush(selected ? C.Purple : C.Card);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var value = combo.GetItemText(combo.Items[e.Index]);
            var rect = new Rectangle(e.Bounds.X + 9, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 18), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, value, combo.Font, rect, selected ? C.OnAccent : C.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
    }


    private void ShowDashboard() => ShowReport();
    private void ShowFinance() => ShowReport();

    private void ShowReport()
    {
        ClearWork();
        activePage = ShowReport;
        var pageToken = pageCts.Token;
        work.Controls.Add(Title("Báo cáo"));

        var store = CurrentStore();
        if (store is null)
        {
            var empty = CardPanel(); empty.Left = 4; empty.Top = 72; empty.Width = 720; empty.Height = 180;
            empty.Controls.Add(new Label { Text = "Hãy thêm hoặc chọn một cửa hàng để xem báo cáo.", Left = 24, Top = 28, AutoSize = true, ForeColor = C.Muted });
            work.Controls.Add(empty);
            return;
        }

        var quick = new FlowLayoutPanel { Left = 4, Top = 56, Width = 600, Height = 46, BackColor = C.Main, WrapContents = false };
        var b7 = ActionButton("7 ngày", 88, true);
        var b30 = ActionButton("30 ngày", 96);
        var b90 = ActionButton("90 ngày", 96);
        var from = new DateTimePicker { Width = 130, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddDays(-6) };
        var to = new DateTimePicker { Width = 130, Format = DateTimePickerFormat.Short, Value = DateTime.Today };
        quick.Controls.Add(b7); quick.Controls.Add(b30); quick.Controls.Add(b90); quick.Controls.Add(from); quick.Controls.Add(to);
        work.Controls.Add(quick);

        var sync = ActionButton("↻ Đồng bộ báo cáo", 180, true);
        sync.Top = 0; sync.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(sync);

        var overview = new TableLayoutPanel
        {
            Left = 4, Top = 112, Height = 106, ColumnCount = 4, RowCount = 1,
            BackColor = C.Main, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        overview.Width = work.ClientSize.Width - 55;
        for (var i = 0; i < 4; i++) overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        work.Controls.Add(overview);

        var analyticsCard = CardPanel();
        analyticsCard.Left = 4; analyticsCard.Top = 234; analyticsCard.Height = 275;
        analyticsCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        analyticsCard.Width = Math.Max(620, (work.ClientSize.Width - 75) * 2 / 3);
        work.Controls.Add(analyticsCard);

        var financeCard = CardPanel();
        financeCard.Top = 234; financeCard.Height = 275;
        financeCard.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        financeCard.Width = Math.Max(280, work.ClientSize.Width - analyticsCard.Width - 70);
        work.Controls.Add(financeCard);

        var bottom = CardPanel();
        bottom.Left = 4; bottom.Top = 525;
        bottom.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        bottom.Width = work.ClientSize.Width - 55; bottom.Height = work.ClientSize.Height - 555;
        work.Controls.Add(bottom);

        var orders = app.Db.Orders(store.Id).ToArray();
        var orderTruth = BuildOrderTruth(store);
        var products = app.Db.Products(store.Id).Count;
        var kiz = app.Db.Kiz();
        string TruthNote()=>orderTruth.IsAuthoritative
            ? orderTruth.ObservedAt is { } at?$"Đã xác minh · {at.ToLocalTime():dd/MM HH:mm}":"Chưa có trạng thái đã xác minh"
            : "Dữ liệu một phần · cần đồng bộ";
        var newCount = orderTruth.NewCount;
        var packCount = orderTruth.PackingCount;
        var shipCount = orderTruth.ShippingCount;

        void AddMetric(string label, string value, string note)
        {
            var card = CardPanel(); card.Dock = DockStyle.Fill; card.Margin = new Padding(5); card.Padding = new Padding(14);
            card.Controls.Add(new Label { Text = label, Left = 14, Top = 12, AutoSize = true, ForeColor = C.Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
            card.Controls.Add(new Label { Text = value, Left = 14, Top = 38, AutoSize = true, ForeColor = C.Text, Font = new Font("Segoe UI", 18, FontStyle.Bold) });
            card.Controls.Add(new Label { Text = note, Left = 14, Top = 72, AutoSize = true, ForeColor = C.Green, Font = new Font("Segoe UI", 8.5f) });
            overview.Controls.Add(card);
        }

        AddMetric("Sản phẩm", products.ToString("N0"), "Hiện tại · " + MarketplaceName(store.Marketplace));
        AddMetric("Đơn mới", newCount.ToString("N0"), TruthNote());
        AddMetric("Đang đóng gói", packCount.ToString("N0"), TruthNote());
        AddMetric("KIZ sẵn sàng", kiz.Count(x => x.Status == "AVAILABLE").ToString("N0"), $"{kiz.Count:N0} mã trong kho");

        analyticsCard.Controls.Add(new Label { Text = "Phân tích đơn hàng", Left = 18, Top = 14, AutoSize = true, ForeColor = C.Text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(-6 + i)).ToArray();
        var dayValues = days.Select(day => (double)orders.Where(o => OrderDate(o)?.Date == day.Date).Select(o => o.ExternalOrderId).Distinct(StringComparer.Ordinal).Count()).ToArray();
        var chart = new ReportBarChart
        {
            Left = 18, Top = 50, Width = analyticsCard.Width - 36, Height = 205,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Values = dayValues,
            Labels = days.Select(x => x.ToString("dd/MM")).ToArray()
        };
        analyticsCard.Controls.Add(chart);

        void RefreshSnapshot()
        {
            if (pageToken.IsCancellationRequested || overview.IsDisposed) return;
            orders = app.Db.Orders(store.Id).ToArray();
            orderTruth = BuildOrderTruth(store);
            foreach (var control in overview.Controls.Cast<Control>().ToArray()) control.Dispose();
            overview.Controls.Clear();
            AddMetric("Sản phẩm", app.Db.Products(store.Id).Count.ToString("N0"), "Hiện tại · " + MarketplaceName(store.Marketplace));
            AddMetric("Đơn mới", orderTruth.NewCount.ToString("N0"), TruthNote());
            AddMetric("Đang đóng gói", orderTruth.PackingCount.ToString("N0"), TruthNote());
            var pool = app.Db.Kiz();
            AddMetric("KIZ sẵn sàng", pool.Count(x => x.Status == "AVAILABLE").ToString("N0"), $"{pool.Count:N0} mã trong kho");
            var count = Math.Clamp((to.Value.Date - from.Value.Date).Days + 1, 1, 366);
            var range = Enumerable.Range(0, count).Select(i => from.Value.Date.AddDays(i)).ToArray();
            chart.Values = range.Select(day => (double)orders.Where(o => OrderDate(o)?.Date == day).Select(o => o.ExternalOrderId).Distinct(StringComparer.Ordinal).Count()).ToArray();
            chart.Labels = range.Select(day => day.ToString("dd/MM")).ToArray();
            chart.Invalidate();
        }
        from.ValueChanged += (_, _) => RefreshSnapshot();
        to.ValueChanged += (_, _) => RefreshSnapshot();

        financeCard.Controls.Add(new Label { Text = "Tài chính & trạng thái", Left = 18, Top = 14, AutoSize = true, ForeColor = C.Text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });
        var financeStatus = new Label
        {
            Left = 18, Top = 48, Width = financeCard.Width - 36, Height = 185,
            ForeColor = C.Muted, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = store.Marketplace == Marketplace.Wildberries
                ? "Nhấn “Đồng bộ báo cáo” để đọc quyết toán WB cho khoảng ngày đã chọn."
                : $"Đang hiển thị sản phẩm và đơn hàng {MarketplaceName(store.Marketplace)} đã đồng bộ. Quyết toán tài chính trực tiếp của sàn này chưa được hỗ trợ."
        };
        financeCard.Controls.Add(financeStatus);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("time", "Thời gian");
        grid.Columns.Add("stream", "Luồng");
        grid.Columns.Add("read", "Đọc");
        grid.Columns.Add("written", "Ghi");
        grid.Columns.Add("result", "Kết quả");
        bottom.Controls.Add(grid);

        void FillRuns()
        {
            grid.Rows.Clear();
            foreach (var run in app.Db.SyncRuns(store.Id, 30))
                grid.Rows.Add(run.StartedAt.ToLocalTime().ToString("dd/MM HH:mm:ss"), run.Stream,
                    run.ReadCount, run.WrittenCount, run.Success ? "Thành công" : "Lỗi: " + run.Error);
        }

        async Task RefreshReport()
        {
            sync.Enabled = false;
            financeStatus.Text = "Đang đồng bộ dữ liệu...";
            var storeSync = await app.SyncStoreAsync(store, pageToken);
            if (pageToken.IsCancellationRequested) return;
            RefreshSnapshot();
            if (store.Marketplace == Marketplace.Wildberries)
            {
                financeStatus.Text = "Đang đọc quyết toán WB...";
                var finance = await app.ReadFinanceAsync(store, from.Value.Date, to.Value.Date, pageToken);
                if (pageToken.IsCancellationRequested) return;
                if (finance.Ok && finance.Snapshot is not null)
                {
                    var f = finance.Snapshot;
                    var totalCosts = f.Delivery + f.Storage + f.Acceptance + f.Deductions + f.Penalties;
                    var net = f.Payout + f.AdditionalPayments - f.Cashback - totalCosts;
                    financeStatus.Text =
                        $"Doanh thu: {f.Revenue:N0} ₽\n" +
                        $"Thanh toán: {f.Payout:N0} ₽\n" +
                        $"Logistics + lưu kho + phí: {totalCosts:N0} ₽\n" +
                        $"Phạt: {f.Penalties:N0} ₽\n" +
                        $"Ròng ước tính theo báo cáo: {net:N0} ₽\n\n" +
                        $"Kỳ {f.From} → {f.To} · {f.ReportCount} dòng báo cáo";
                    financeStatus.ForeColor = C.Text;
                }
                else
                {
                    financeStatus.Text = finance.Message;
                    financeStatus.ForeColor = C.Danger;
                }
            }
            else
            {
                financeStatus.Text = storeSync.Message;
                financeStatus.ForeColor = storeSync.Ok ? C.Text : C.Danger;
            }
            sync.Enabled = true;
            FillRuns();
        }

        void SetDays(int days)
        {
            from.Value = DateTime.Today.AddDays(-(days - 1));
            to.Value = DateTime.Today;
            StyleTab(b7, days == 7); StyleTab(b30, days == 30); StyleTab(b90, days == 90);
        }

        b7.Click += (_, _) => SetDays(7);
        b30.Click += (_, _) => SetDays(30);
        b90.Click += (_, _) => SetDays(90);
        sync.Click += async (_, _) =>
        {
            try { await RefreshReport(); }
            catch (OperationCanceledException) { }
            finally { if (!sync.IsDisposed) sync.Enabled = true; }
        };
        refreshActivePage = () => { RefreshSnapshot(); FillRuns(); };

        SetWorkResize((_, _) =>
        {
            sync.Left = work.ClientSize.Width - sync.Width - 30;
            var available = Math.Max(560, work.ClientSize.Width - 55);
            overview.Width = available;
            quick.Width = available;
            var stacked = available < 900;
            analyticsCard.Width = stacked ? available : (available - 14) * 2 / 3;
            financeCard.Left = stacked ? 4 : analyticsCard.Right + 14;
            financeCard.Top = stacked ? analyticsCard.Bottom + 16 : analyticsCard.Top;
            financeCard.Width = stacked ? available : available - analyticsCard.Width - 14;
            financeCard.Height = stacked ? 210 : 275;
            chart.Width = analyticsCard.Width - 36;
            financeStatus.Width = financeCard.Width - 36;
            financeStatus.Height = financeCard.Height - 66;
            bottom.Top = Math.Max(analyticsCard.Bottom, financeCard.Bottom) + 16;
            bottom.Width = available;
            bottom.Height = Math.Max(180, work.ClientSize.Height - bottom.Top - 30);
        });

        FillRuns();
    }

    private static DateTime? OrderDate(FbsOrderRow order)
    {
        try
        {
            var root = JsonNode.Parse(order.RawJson);
            foreach (var key in new[] { "createdAt", "created_at", "creationDate", "in_process_at", "shipment_date", "date" })
            {
                var value = root?[key]?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (DateTimeOffset.TryParse(value, out var dto)) return dto.LocalDateTime;
                if (DateTime.TryParse(value, out var dt)) return dt;
            }
        }
        catch { }
        return null;
    }



    private void ShowSyncCenter()
    {
        ClearWork();
        var pageToken = pageCts.Token;
        activePage = ShowSyncCenter;
        work.Controls.Add(Title("Đồng bộ dữ liệu"));

        var runAll = ActionButton("↻ Đồng bộ tất cả cửa hàng", 220, true);
        runAll.Top = 0; runAll.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(runAll);

        var intro = new Label
        {
            Text = "Luồng độc lập theo từng sàn: Catalog → ảnh/giá → FBS → FBO/FBW (nếu áp dụng). Không chạy chồng hai luồng trên cùng cửa hàng.",
            Left = 4, Top = 50, Width = 900, Height = 40, ForeColor = C.Muted
        };
        work.Controls.Add(intro);

        var card = CardPanel();
        card.Left = 4; card.Top = 94;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 124; work.Controls.Add(card);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("platform", "Sàn");
        grid.Columns.Add("shop", "Cửa hàng");
        grid.Columns.Add("products", "Sản phẩm");
        grid.Columns.Add("orders", "FBS");
        grid.Columns.Add("last", "Đồng bộ gần nhất");
        grid.Columns.Add("error", "Lỗi gần nhất");
        card.Controls.Add(grid);

        void Load()
        {
            if (pageToken.IsCancellationRequested || grid.IsDisposed) return;
            grid.Rows.Clear();
            foreach (var shop in app.Db.Stores().Where(x => x.Enabled).OrderBy(x => x.Marketplace).ThenBy(x => x.Name))
            {
                var p = app.Db.SyncState(shop.Id, "products");
                var o = app.Db.SyncState(shop.Id, "fbs_orders");
                var last = new[] { p.LastSuccessAt, o.LastSuccessAt }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x).FirstOrDefault() ?? "Chưa đồng bộ";
                var error = !string.IsNullOrWhiteSpace(o.LastError) ? o.LastError : p.LastError;
                grid.Rows.Add(MarketplaceName(shop.Marketplace), shop.Name,
                    app.Db.Products(shop.Id).Count, app.Db.Orders(shop.Id).Count, last, error);
            }
        }

        runAll.Click += async (_, _) =>
        {
            runAll.Enabled = false;
            var messages = new List<string>();
            foreach (var shop in app.Db.Stores().Where(x => x.Enabled))
            {
                if (pageToken.IsCancellationRequested) return;
                var result = await app.SyncStoreAsync(shop, pageToken);
                if (pageToken.IsCancellationRequested || runAll.IsDisposed) return;
                messages.Add($"{MarketplaceName(shop.Marketplace)} · {shop.Name}: {(result.Ok ? "OK" : "LỖI")}\n{result.Message}");
            }
            runAll.Enabled = true;
            Load();
            ShowInfo(string.Join(Environment.NewLine + Environment.NewLine, messages));
        };

        SetWorkResize((_, _) =>
        {
            runAll.Left = work.ClientSize.Width - runAll.Width - 30;
            intro.Width = work.ClientSize.Width - 55;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(300, work.ClientSize.Height - 124);
        });
        refreshActivePage = Load;
        Load();
    }

    private static string MarketplaceName(Marketplace marketplace) => marketplace switch
    {
        Marketplace.Wildberries => "Wildberries",
        Marketplace.Ozon => "Ozon",
        Marketplace.Yandex => "Yandex Market",
        _ => marketplace.ToString()
    };

    private void ShowLocalSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        ClearWork();
        work.Controls.Add(Title($"Tìm kiếm: {query}"));
        var card = CardPanel(); card.Left = 4; card.Top = 70;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; work.Controls.Add(card);
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("kind", "Loại");
        grid.Columns.Add("id", "Mã");
        grid.Columns.Add("name", "Tên / sản phẩm");
        grid.Columns.Add("status", "Trạng thái");
        card.Controls.Add(grid);

        var store = CurrentStore();
        if (store is null) return;
        foreach (var p in app.Db.Products(store.Id).Where(x =>
                     x.Sku.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            grid.Rows.Add("Sản phẩm", p.Sku, p.Name, p.Price is null ? "" : $"{p.Price:0.##} ₽");
        foreach (var o in app.Db.Orders(store.Id).Where(x =>
                     x.ExternalOrderId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Sku.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            grid.Rows.Add("Đơn FBS", o.ExternalOrderId, o.Name, o.Status);

        SetWorkResize((_, _) =>
        {
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(250, work.ClientSize.Height - 100);
        });
    }


    private void ShowMarketplaceSync(Marketplace marketplace)
    {
        ClearWork();
        var pageToken = pageCts.Token;
        activePage = () => ShowMarketplaceSync(marketplace);
        var display = marketplace == Marketplace.Ozon ? "Ozon" : "Yandex Market";
        work.Controls.Add(Title($"Đồng bộ {display}"));

        var run = ActionButton($"↻ Đồng bộ tất cả {display}", 240, true);
        run.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        run.Top = 0;
        work.Controls.Add(run);

        var state = new Label
        {
            Left = 4, Top = 52, AutoSize = true, ForeColor = C.Muted,
            Text = marketplace == Marketplace.Ozon
                ? "Luồng: sản phẩm → ảnh/giá → đơn FBS → yêu cầu nhập kho FBO."
                : "Luồng: sản phẩm → ảnh/giá → đơn hàng Yandex Market."
        };
        work.Controls.Add(state);

        var card = CardPanel();
        card.Left = 4; card.Top = 90;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55;
        card.Height = work.ClientSize.Height - 120;
        work.Controls.Add(card);

        var grid = DarkGrid();
        grid.Dock = DockStyle.Fill;
        grid.Columns.Add("shop", "Cửa hàng");
        grid.Columns.Add("products", "Sản phẩm");
        grid.Columns.Add("orders", "Đơn hàng");
        grid.Columns.Add("last", "Đồng bộ gần nhất");
        grid.Columns.Add("error", "Lỗi gần nhất");
        card.Controls.Add(grid);

        void LoadRows()
        {
            if (pageToken.IsCancellationRequested || grid.IsDisposed) return;
            grid.Rows.Clear();
            foreach (var shop in app.Db.Stores().Where(x => x.Marketplace == marketplace && x.Enabled))
            {
                var p = app.Db.SyncState(shop.Id, "products");
                var o = app.Db.SyncState(shop.Id, "fbs_orders");
                var last = new[] { p.LastSuccessAt, o.LastSuccessAt }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderByDescending(x => x, StringComparer.Ordinal)
                    .FirstOrDefault() ?? "Chưa đồng bộ";
                var error = !string.IsNullOrWhiteSpace(o.LastError) ? o.LastError : p.LastError;
                grid.Rows.Add(shop.Name, app.Db.Products(shop.Id).Count, app.Db.Orders(shop.Id).Count, last, error);
            }
        }

        run.Click += async (_, _) =>
        {
            var shops = app.Db.Stores().Where(x => x.Marketplace == marketplace && x.Enabled).ToList();
            if (shops.Count == 0) { ShowInfo($"Chưa có cửa hàng {display}."); return; }

            run.Enabled = false;
            var messages = new List<string>();
            foreach (var shop in shops)
            {
                if (pageToken.IsCancellationRequested) return;
                state.Text = $"Đang đồng bộ {display}: {shop.Name}...";
                state.ForeColor = C.Muted;
                var result = await app.SyncStoreAsync(shop, pageToken);
                if (pageToken.IsCancellationRequested || run.IsDisposed) return;
                messages.Add($"{shop.Name}: {(result.Ok ? "OK" : "LỖI")} · {result.Message}");
            }
            run.Enabled = true;
            state.Text = $"Đã hoàn tất đồng bộ {display}.";
            state.ForeColor = C.Green;
            LoadRows();
            ShowInfo(string.Join(Environment.NewLine + Environment.NewLine, messages));
        };

        SetWorkResize((_, _) => {
            run.Left = work.ClientSize.Width - run.Width - 30;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(280, work.ClientSize.Height - 120);
        });
        refreshActivePage = LoadRows;
        LoadRows();
    }

    private void ShowFbs()
    {
        fbsWorkspaceMode = "new";
        ShowFbsWorkspace("new");
    }

    private void ShowFbsSupplyDetail(FbsOrderRow order, string? supplyOverride = null)
    {
        var wbStore=CurrentStore();
        if(wbStore?.Marketplace==Marketplace.Wildberries) {
            var known=supplyOverride??app.Db.FindWbSupplyForOrder(wbStore.Id,order.ExternalOrderId)??SupplyIdFrom(order);
            if(!string.IsNullOrWhiteSpace(known)){_=OpenWbSupplyAsync(wbStore,known,pageCts.Token);return;}
        }
        ClearWork();
        var pageToken = pageCts.Token;
        activePage = ShowFbs;
        var store = CurrentStore(); if (store is null) return;
        var product = FindProductForOrder(store, order);
        var meta = product is null ? new ProductMetaValue("", "", "", "", "", "", order.Sku, "") : ProductMeta(product);
        var supplyId = !string.IsNullOrWhiteSpace(supplyOverride) ? supplyOverride! : SupplyIdFrom(order);
        var assignedKiz = app.Db.Kiz().FirstOrDefault(x => x.Assigned.Equals(order.ExternalOrderId, StringComparison.OrdinalIgnoreCase));

        var back = IconButton("←"); back.Left = 0; back.Top = 0; back.Click += (_, _) => ShowFbs(); work.Controls.Add(back);
        var title = Title(store.Marketplace == Marketplace.Wildberries
            ? $"Supply {(string.IsNullOrWhiteSpace(supplyId) ? "chưa tạo" : supplyId)}"
            : $"{MarketplaceName(store.Marketplace)} · {order.ExternalOrderId}");
        title.Left = 55; work.Controls.Add(title);

        var print = ActionButton($"▣  In nhãn {MarketplaceName(store.Marketplace)}", 215, true);
        print.BackColor = C.Green; print.BorderColor = C.Green;
        print.Anchor = AnchorStyles.Top | AnchorStyles.Right; print.Top = 0;
        print.Click += async (_, _) =>
        {
            if (store.Marketplace == Marketplace.Wildberries)
            {
                print.Enabled = false;
                try {
                    if(IsNew(order.Status))await BeginWbShipmentAsync(store,new[]{order},true,pageToken);
                    else ShowInfo("Hãy mở shipment của đơn trước khi xuất nhãn.");
                }
                finally { if (!print.IsDisposed) print.Enabled = true; }
                return;
            }
            await ExportMarketplaceFbsLabelsAsync(store,new[]{order},pageToken);
        };
        work.Controls.Add(print);

        var move = ActionButton(IsNew(order.Status) ? "▣  Tạo shipment" : "▣  Chuyển sang giao hàng", 255, true);
        move.Anchor = AnchorStyles.Top | AnchorStyles.Right; move.Top = 0; work.Controls.Add(move);

        var options = new FlowLayoutPanel { Left = 0, Top = 58, Width = 650, Height = 44, BackColor = C.Main, WrapContents = false };
        foreach (var textValue in new[] { "Danh mục", "Article", "Màu", "Kích cỡ" })
        {
            options.Controls.Add(new CheckBox
            {
                Text = textValue, Checked = true, AutoSize = true,
                ForeColor = C.Text, BackColor = C.Main,
                Font = new Font("Segoe UI", 10), Margin = new Padding(0, 8, 28, 0)
            });
        }
        work.Controls.Add(options);

        var card = CardPanel();
        card.Left = 0; card.Top = 110;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 30; card.Height = work.ClientSize.Height - 135; work.Controls.Add(card);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "STT", Width = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mã nhiệm vụ", Width = 205 });
        grid.Columns.Add(new DataGridViewImageColumn { HeaderText = "Ảnh", Width = 100, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Thông tin chi tiết", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Giá", Width = 160 });
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        card.Controls.Add(grid);

        var kizText = !string.IsNullOrWhiteSpace(assignedKiz.Code)
            ? "✓ Đã gắn KIZ"
            : order.NeedsKiz ? "Sẽ tự mua/gắn KIZ khi tạo shipment" : "Không yêu cầu KIZ";
        var detail = $"{product?.Name ?? order.Name}\n{BuildProductMetaLine(product, order)}\nDanh mục: {(string.IsNullOrWhiteSpace(meta.Category) ? "—" : meta.Category)}\n{kizText}";
        var rowIndex = grid.Rows.Add(1, $"{order.ExternalOrderId}\n{FormatOrderTime(order)}", null, detail,
            product?.Price is null ? "" : $"{product.Price:0.##} ₽");
        grid.Rows[rowIndex].Tag = order;
        grid.Rows[rowIndex].Height = 135;
        var image = ProductImageUrl(product);
        if (!string.IsNullOrWhiteSpace(image)) _ = LoadImageAsync(grid, rowIndex, 2, image);

        move.Enabled = IsNew(order.Status) || store.Marketplace == Marketplace.Wildberries;
        if (!move.Enabled) move.Text = "Đơn đã đóng gói";
        move.Click += async (_, _) =>
        {
            if(store.Marketplace==Marketplace.Wildberries && IsNew(order.Status)) {
                await BeginWbShipmentAsync(store,new[]{order},true,pageToken);return;
            }
            if(store.Marketplace!=Marketplace.Wildberries){await BeginMarketplaceFbsAsync(store,new[]{order},pageToken);return;}
            move.Enabled = false;
            if (IsNew(order.Status))
            {
                var result = await CreateShipmentWithKizAsync(store, new[] { order });
                app.Db.Audit("FBS", result.Success ? "Tạo shipment" : "Lỗi tạo shipment", $"{order.ExternalOrderId}:{result.Message}");
                if (pageToken.IsCancellationRequested || move.IsDisposed) return;
                if (!result.Success) { ShowInfo(result.Message); move.Enabled = true; return; }
                await app.SyncOrdersAsync(store, pageToken);
                if (pageToken.IsCancellationRequested || move.IsDisposed) return;
                if (store.Marketplace == Marketplace.Wildberries)
                    ShowFbsSupplyDetail(order with { Status = "confirm" }, result.ExternalTaskId);
                else ShowFbs();
                return;
            }

            if (string.IsNullOrWhiteSpace(supplyId))
            {
                ShowInfo("Không tìm thấy supplyId. Hãy cập nhật đơn hàng trước.");
                move.Enabled = true;
                return;
            }

            var delivered = await app.Api.DeliverSupplyAsync(store, supplyId, lifetimeCts.Token);
            app.Db.Audit("FBS", delivered.Success ? "Giao lô" : "Lỗi giao lô", $"{supplyId}:{delivered.Message}");
            if (pageToken.IsCancellationRequested || move.IsDisposed) return;
            ShowInfo(delivered.Message);
            if (delivered.Success)
            {
                await app.SyncOrdersAsync(store, pageToken);
                if (pageToken.IsCancellationRequested || move.IsDisposed) return;
                ShowFbs();
            }
            else move.Enabled = true;
        };

        SetWorkResize((_, _) => {
            move.Left = work.ClientSize.Width - move.Width - 10;
            print.Left = move.Left - print.Width - 12;
            card.Width = work.ClientSize.Width - 30;
            card.Height = Math.Max(300, work.ClientSize.Height - 135);
        });
    }

    private DataGridView FbsGrid()
    {
        var g = DarkGrid();
        g.ReadOnly = false;
        g.EditMode = DataGridViewEditMode.EditOnEnter;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "check", HeaderText = "", Width = 60, ReadOnly = false });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "order", HeaderText = "Order ID", Width = 235, ReadOnly = true });
        g.Columns.Add(new DataGridViewImageColumn { Name = "image", HeaderText = "Ảnh", Width = 130, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { NullValue = null } });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "product", HeaderText = "Sản phẩm", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "labelType", HeaderText = "Nhãn sàn", Width = 145, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = "Giá", Width = 125, ReadOnly = true });
        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell)
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return g;
    }

    private List<FbsOrderRow> CheckedOrders(DataGridView grid)
    {
        grid.EndEdit();
        return grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells.Count > 0 && r.Cells[0].Value is bool selected && selected)
            .Select(r => r.Tag as FbsOrderRow)
            .Where(x => x is not null)
            .Cast<FbsOrderRow>()
            .ToList();
    }

    private async Task<PriceUpdateResult> CreateShipmentWithKizAsync(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> orders,
        bool useKiz = true)
    {
        var token = lifetimeCts.Token;
        if (!await fbsOperations.WaitAsync(0, token))
            return new PriceUpdateResult(false, "Đang có một tác vụ đóng đơn FBS. Hãy chờ tác vụ đó hoàn tất.");
        try
        {
            var ids = orders.Select(x => x.ExternalOrderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var completeOrders = app.Db.Orders(store.Id).Where(x => ids.Contains(x.ExternalOrderId)).ToArray();
            return await CreateShipmentWithKizCoreAsync(store, completeOrders, useKiz, token);
        }
        catch (OperationCanceledException) { return new PriceUpdateResult(false, "Tác vụ đã dừng. Đồng bộ lại trạng thái sàn trước khi tiếp tục."); }
        catch (Exception ex) { return new PriceUpdateResult(false, ex.Message); }
        finally { fbsOperations.Release(); }
    }

    private async Task<PriceUpdateResult> CreateShipmentWithKizCoreAsync(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> orders,
        bool useKiz,
        CancellationToken operationToken, WbShipmentChoice? choice = null, IProgress<string>? progress = null)
    {
        if (orders.Count == 0) return new PriceUpdateResult(false, "Chưa chọn đơn hàng.");

        if (store.Marketplace == Marketplace.Wildberries)
        {
            if(choice is null)return new(false,"Hãy chọn tạo shipment mới hoặc thêm vào shipment hôm nay trước khi gắn KIZ.");
            if(!useKiz && orders.Any(x=>x.NeedsKiz))return new(false,"Đơn bắt buộc KIZ. Hãy bật tự động KIZ trước khi đóng đơn.");
            var shipment=await app.Api.CreateShipmentAsync(store,orders,operationToken,choice.SupplyId,choice.Name,progress);
            if(string.IsNullOrWhiteSpace(shipment.ExternalTaskId))return shipment;
            IReadOnlyList<FbsOrderRow> full;
            try {full=await app.ReadWbSupplyOrdersAsync(store,shipment.ExternalTaskId,operationToken);}
            catch(Exception ex){return new(false,shipment.Message+"\n"+ex.Message,shipment.ExternalTaskId);}
            if(!shipment.Success)return shipment;
            var selectedIds=orders.Select(x=>x.ExternalOrderId).ToHashSet(StringComparer.Ordinal);
            if(!selectedIds.IsSubsetOf(full.Select(x=>x.ExternalOrderId)))
                return new(false,"Shipment không còn đủ các đơn đã chọn. Chưa gắn KIZ hoặc xuất nhãn; đồng bộ và chọn lại shipment này.",shipment.ExternalTaskId);
            var marked=await app.EnsureWbSupplyKizAsync(store,full,ResolveWbGtins(store,full),useKiz,operationToken,progress);
            return new(marked.Success,shipment.Message+"\n"+marked.Message,shipment.ExternalTaskId);
        }

        var selected=orders.Select(x=>x.ExternalOrderId).ToHashSet(StringComparer.Ordinal);
        var batches=app.Db.TodayMarketplaceFbsBatches(store).Where(b=>app.Db.MarketplaceFbsBatchOrders(store,b.Id).Select(x=>x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(selected)).ToArray();
        if(batches.Length!=1)return new(false,"workflow_context_required: nhận đơn vào đúng một batch trước khi đóng hàng.");
        return await app.PackMarketplaceFbsAsync(store,Array.Empty<string>(),item=>ResolveMarketplaceGtin(store,item),useKiz,operationToken,progress,batchId:batches[0].Id);
    }

    private void ShowZnakRegistration()
    {
        ClearWork();
        work.Controls.Add(Title("Đăng ký thẻ Znack cho WB"));

        var docs = ActionButton("Cấu hình giấy tờ", 175); docs.Anchor = AnchorStyles.Top | AnchorStyles.Right; docs.Top = 0; docs.Click += (_, _) => ShowZnakSettings(); work.Controls.Add(docs);
        var sync = ActionButton("Đồng bộ Znack", 165); sync.Anchor = AnchorStyles.Top | AnchorStyles.Right; sync.Top = 0; work.Controls.Add(sync);

        var summary = new Label { Left = 4, Top = 53, AutoSize = true, ForeColor = C.Muted, Font = new Font("Segoe UI", 9.5f) };
        work.Controls.Add(summary);

        var search = DarkText("Tìm tên, article, barcode hoặc nmID...", 380); search.Left = 20; search.Top = 94; work.Controls.Add(search);
        var category = DarkCombo(135); category.Left = 410; category.Top = 94; work.Controls.Add(category);
        var status = DarkCombo(225); status.Left = 555; status.Top = 94;
        status.Items.AddRange(new object[] { "Tất cả trạng thái", "Hoàn tất", "Cần xử lý", "Lỗi" }); status.SelectedIndex = 0; work.Controls.Add(status);
        var clear = ActionButton("Xóa bộ lọc", 125); clear.Left = 790; clear.Top = 94; work.Controls.Add(clear);

        var card = CardPanel();
        card.Left = 4; card.Top = 160; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 255; work.Controls.Add(card);
        var grid = ZnakGrid(); grid.Dock = DockStyle.Fill; card.Controls.Add(grid);

        var selectAll = new CheckBox
        {
            Text = "Chọn tất cả kết quả đang lọc", Left = 4, AutoSize = true,
            ForeColor = C.Muted, BackColor = C.Main, Anchor = AnchorStyles.Left | AnchorStyles.Bottom
        };
        work.Controls.Add(selectAll);

        var registerSelected = ActionButton("Đăng ký", 110, true);
        registerSelected.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; work.Controls.Add(registerSelected);

        var prev = ActionButton("‹", 48); prev.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; work.Controls.Add(prev);
        var pageLabel = new Label { AutoSize = true, ForeColor = C.Text, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, TextAlign = ContentAlignment.MiddleCenter };
        work.Controls.Add(pageLabel);
        var next = ActionButton("›", 48); next.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; work.Controls.Add(next);
        var footerStats = new Label { AutoSize = true, ForeColor = C.Muted, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        work.Controls.Add(footerStats);

        var continueQueue = ActionButton("Tiếp tục hàng đợi", 175);
        continueQueue.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        continueQueue.Visible = znakQueuePaused;
        continueQueue.Click += (_, _) => { znakQueuePaused = false; continueQueue.Visible = false; };
        work.Controls.Add(continueQueue);

        const int pageSize = 50;
        var currentPage = 0;
        List<ProductRow> filtered = new();

        string DisplayStatus(ProductRow p, ZnakPipelineRow? pipe, ProductMetaValue meta)
        {
            return pipe?.Stage switch
            {
                "SENT" => "Đã xuất bản",
                "ERROR" => "Lỗi",
                "QUEUED" => "Xếp hàng",
                "READY" => "Chờ gửi",
                _ => !string.IsNullOrWhiteSpace(meta.Barcode) ? "Đã có GTIN" : "Chưa tạo"
            };
        }

        bool StatusMatch(string value)
        {
            return status.SelectedItem?.ToString() switch
            {
                "Hoàn tất" => value is "Đã xuất bản" or "Đã có GTIN",
                "Cần xử lý" => value is "Chưa tạo" or "Xếp hàng" or "Chờ gửi",
                "Lỗi" => value == "Lỗi",
                _ => true
            };
        }

        void BuildCategory()
        {
            var selected = category.SelectedItem?.ToString();
            category.Items.Clear();
            category.Items.Add("Danh mục");
            var store = CurrentStore();
            if (store is not null)
            {
                foreach (var value in app.Db.Products(store.Id).Select(ProductMeta).Select(x => x.Category)
                             .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                    category.Items.Add(value);
            }
            category.SelectedIndex = selected is not null && category.Items.Contains(selected) ? category.Items.IndexOf(selected) : 0;
        }

        void LoadRows()
        {
            grid.Rows.Clear();
            var store = CurrentStore(); if (store is null) return;
            var products = app.Db.Products(store.Id);
            var pipelines = app.Db.ZnakPipelines(store.Id).ToDictionary(x => x.Sku, StringComparer.OrdinalIgnoreCase);
            var q = search.Text.Trim();
            var selectedCategory = category.SelectedIndex > 0 ? category.SelectedItem?.ToString() ?? "" : "";

            filtered = products.Where(p =>
            {
                var meta = ProductMeta(p);
                pipelines.TryGetValue(p.Sku, out var pipe);
                var state = DisplayStatus(p, pipe, meta);
                if (!string.IsNullOrWhiteSpace(q) &&
                    !p.Sku.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !p.ExternalId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !meta.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(selectedCategory) && !meta.Category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase)) return false;
                return StatusMatch(state);
            }).ToList();

            var maxPage = Math.Max(0, (filtered.Count - 1) / pageSize);
            currentPage = Math.Min(currentPage, maxPage);
            var page = filtered.Skip(currentPage * pageSize).Take(pageSize).ToList();

            foreach (var p in page)
            {
                var meta = ProductMeta(p);
                pipelines.TryGetValue(p.Sku, out var pipe);
                var state = DisplayStatus(p, pipe, meta);
                var article = string.IsNullOrWhiteSpace(meta.Article) ? p.Sku : meta.Article;
                var gtin = !string.IsNullOrWhiteSpace(pipe?.Gtin) ? pipe!.Gtin : meta.Barcode;
                var barcodeText = string.IsNullOrWhiteSpace(gtin) ? meta.Barcode : $"{meta.Barcode}\nGTIN: {gtin}";
                var nameText = p.Name;
                if (!string.IsNullOrWhiteSpace(meta.Category)) nameText += $"\n{p.Sku} · {meta.Category}";
                if (!string.IsNullOrWhiteSpace(meta.Tnved)) nameText += $"\nTN VED: {meta.Tnved}";
                var row = grid.Rows.Add(false, null, nameText, article, meta.Gender, meta.Color, meta.Size, barcodeText, state, "Đăng ký");
                grid.Rows[row].Tag = p;
                grid.Rows[row].Height = 104;
                var image = ProductImageUrl(p);
                if (!string.IsNullOrWhiteSpace(image)) _ = LoadImageAsync(grid, row, 1, image);
            }

            var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
            pageLabel.Text = $"{Math.Min(currentPage + 1, totalPages)} / {totalPages} · {filtered.Count}";
            prev.Enabled = currentPage > 0;
            next.Enabled = currentPage + 1 < totalPages;

            var allPipes = pipelines.Values.ToList();
            var errors = allPipes.Count(x => x.Stage == "ERROR");
            var queued = allPipes.Count(x => x.Stage == "QUEUED");
            var ready = allPipes.Count(x => x.Stage == "READY");
            var sent = allPipes.Count(x => x.Stage == "SENT");
            summary.Text = $"{store.Name}: {products.Count} thẻ · lỗi {errors} · hoàn tất {sent}. Hàng đợi tiếp tục chạy theo trạng thái đã lưu.";
            footerStats.Text = $"Xếp hàng {queued} · chờ gửi {ready} · đã xuất bản {sent} · lỗi {errors}";
        }

        sync.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null) return;
            sync.Enabled = false;
            var result = await app.SyncProductsAsync(store);
            sync.Enabled = true;
            summary.Text = result.Message;
            BuildCategory();
            LoadRows();
        };
        clear.Click += (_, _) => { search.Clear(); category.SelectedIndex = 0; status.SelectedIndex = 0; currentPage = 0; };
        search.TextChanged += (_, _) => { currentPage = 0; LoadRows(); };
        category.SelectedIndexChanged += (_, _) => { currentPage = 0; LoadRows(); };
        status.SelectedIndexChanged += (_, _) => { currentPage = 0; LoadRows(); };
        prev.Click += (_, _) => { if (currentPage > 0) { currentPage--; LoadRows(); } };
        next.Click += (_, _) => { currentPage++; LoadRows(); };

        selectAll.CheckedChanged += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = selectAll.Checked;
        };

        grid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 9) return;
            if (grid.Rows[e.RowIndex].Tag is not ProductRow p) return;
            await QueueZnakRegistrationAsync(new[] { p });
            continueQueue.Visible = znakQueuePaused;
            LoadRows();
        };

        registerSelected.Click += async (_, _) =>
        {
            var selected = grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells[0].Value is bool b && b)
                .Select(r => r.Tag as ProductRow)
                .Where(p => p is not null).Cast<ProductRow>().ToList();
            if (selected.Count == 0) { ShowInfo("Hãy chọn ít nhất một sản phẩm."); return; }
            registerSelected.Enabled = false;
            await QueueZnakRegistrationAsync(selected);
            registerSelected.Enabled = true;
            continueQueue.Visible = znakQueuePaused;
            LoadRows();
        };

        SetWorkResize((_, _) => {
            sync.Left = work.ClientSize.Width - sync.Width - 10;
            docs.Left = sync.Left - docs.Width - 12;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(300, work.ClientSize.Height - 255);
            var y = work.ClientSize.Height - 78;
            selectAll.Top = y + 10;
            registerSelected.Left = 270; registerSelected.Top = y;
            prev.Left = 390; prev.Top = y;
            pageLabel.Left = 450; pageLabel.Top = y + 12;
            next.Left = 535; next.Top = y;
            footerStats.Left = 4; footerStats.Top = work.ClientSize.Height - 27;
            continueQueue.Left = work.ClientSize.Width - continueQueue.Width - 30;
            continueQueue.Top = y;
        });

        BuildCategory();
        LoadRows();
    }

    private async Task QueueZnakRegistrationAsync(IReadOnlyList<ProductRow> products)
    {
        var z = app.Db.GetZnakConfig();
        if (!z.Enabled || string.IsNullOrWhiteSpace(z.OmsId) || string.IsNullOrWhiteSpace(z.OmsConnection))
        {
            ShowInfo("Hãy hoàn tất omsId, omsConnection và lưu Cấu hình Znack trước.");
            return;
        }
        if (string.IsNullOrWhiteSpace(z.CertificateThumbprint))
        {
            ShowInfo("Hãy chọn chứng thư số có private key trước khi đăng ký.");
            return;
        }

        znakQueuePaused = false;
        foreach (var p in products)
        {
            znakQueueStatus[p.Sku] = "Đã xếp hàng";
            var meta = ProductMeta(p);
            app.Db.UpsertZnakPipeline(CurrentStore()?.Id ?? p.StoreId, p.Sku, meta.Barcode, "QUEUED", "", "Đang chờ worker");
            app.Db.Audit("Đăng ký Znack", "Đã xếp hàng", $"{p.Sku}:{meta.Barcode}");
        }

        var tasks = products.Select(async p =>
        {
            await znakWorkers.WaitAsync();
            try
            {
                var meta = ProductMeta(p);
                if (string.IsNullOrWhiteSpace(meta.Barcode))
                    throw new InvalidOperationException("Sản phẩm chưa có Barcode WB / GTIN.");

                // Hàng đợi 2 worker được chuẩn bị theo hành vi WCode 1.1.57.
                // Gửi thật sang True API cần endpoint/tài khoản OMS hợp lệ trên máy seller.
                znakQueueStatus[p.Sku] = "Sẵn sàng gửi";
                app.Db.UpsertZnakPipeline(CurrentStore()?.Id ?? p.StoreId, p.Sku, meta.Barcode, "READY", "", "Đã kiểm tra cấu hình/GTIN; chờ True API");
                app.Db.Audit("Đăng ký Znack", "Sẵn sàng gửi", $"{p.Sku}:{meta.Barcode}");
                await Task.Yield();
            }
            catch (Exception ex)
            {
                znakQueueStatus[p.Sku] = "Lỗi";
                znakQueuePaused = true;
                app.Db.UpsertZnakPipeline(CurrentStore()?.Id ?? p.StoreId, p.Sku, ProductMeta(p).Barcode, "ERROR", "", ex.Message);
                app.Db.Audit("Đăng ký Znack", "Lỗi", $"{p.Sku}:{ex.Message}");
            }
            finally
            {
                znakWorkers.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        ShowInfo(znakQueuePaused
            ? "Hàng đợi đã tạm dừng vì có lỗi. Nút Tiếp tục hàng đợi sẽ xuất hiện."
            : $"Đã chuẩn bị {products.Count} sản phẩm bằng hàng đợi tối đa 2 worker. Gửi thật cần True API/CryptoPro hợp lệ.");
    }

    private DataGridView ZnakGrid()
    {
        var g = DarkGrid();
        g.ReadOnly = false;
        g.EditMode = DataGridViewEditMode.EditOnEnter;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        g.Columns.Add(new DataGridViewCheckBoxColumn { Width = 55, HeaderText = "", ReadOnly = false });
        g.Columns.Add(new DataGridViewImageColumn { Width = 70, HeaderText = "Ảnh", ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 280, HeaderText = "Tên", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 140, HeaderText = "Article nguồn", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 90, HeaderText = "Giới tính", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 95, HeaderText = "Màu", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 75, HeaderText = "Size", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 210, HeaderText = "Barcode WB / GTIN", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 135, HeaderText = "Trạng thái", ReadOnly = true });
        g.Columns.Add(new DataGridViewButtonColumn { Width = 110, HeaderText = "Thao tác", Text = "Đăng ký", UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat, ReadOnly = true });
        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell)
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return g;
    }

    private void ShowZnakSettings()
    {
        ClearWork();
        work.Controls.Add(Title("Cấu hình Znack"));

        var tabs = CardPanel();
        tabs.Left = 4; tabs.Top = 84; tabs.Height = 72; tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; tabs.Width = work.ClientSize.Width - 55; work.Controls.Add(tabs);

        var content = CardPanel();
        content.Left = 4; content.Top = 157; content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; content.Width = work.ClientSize.Width - 55; content.Height = work.ClientSize.Height - 185; work.Controls.Add(content);

        var names = new[] { "Cài đặt", "Danh mục GTIN", "Đã xóa", "Đơn hàng và mã", "Nhật ký audit" };
        var buttons = new List<RoundedButton>();
        var x = 10;
        for (var i = 0; i < names.Length; i++)
        {
            var b = TabButton(names[i], i == 0, names[i].Length * 10 + 65);
            b.Left = x; b.Top = 10; x += b.Width + 8; tabs.Controls.Add(b); buttons.Add(b);
            var index = i;
            b.Click += (_, _) =>
            {
                for (var j = 0; j < buttons.Count; j++) StyleTab(buttons[j], j == index);
                RenderZnakTab(content, index);
            };
        }
        RenderZnakTab(content, 0);

        SetWorkResize((_, _) => {
            tabs.Width = work.ClientSize.Width - 55;
            content.Width = work.ClientSize.Width - 55;
            content.Height = Math.Max(350, work.ClientSize.Height - 185);
        });
    }

    private void RenderZnakTab(RoundedPanel host, int index)
    {
        host.Controls.Clear();
        if (index == 0) RenderZnakSettings(host);
        else if (index == 1) RenderGtinCategories(host);
        else if (index == 2) RenderDeleted(host);
        else if (index == 3) RenderOrderCodes(host);
        else RenderAudit(host);
    }

    private void RenderZnakSettings(Control host)
    {
        var z = app.Db.GetZnakConfig();
        host.Controls.Add(new Label { Text = "Cài đặt cơ bản", AutoSize = true, Left = 18, Top = 20, ForeColor = C.Text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });

        var omsId = LabeledDark(host, "omsId", z.OmsId, 18, 64, 900);
        var omsConnection = LabeledDark(host, "omsConnection", z.OmsConnection, 18, 124, 900);

        host.Controls.Add(new Label { Text = "Chữ ký số", AutoSize = true, Left = 18, Top = 205, ForeColor = C.Text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });

        var certs = AppServices.Certificates().Where(x => x.HasPrivateKey).ToList();
        var certBox = DarkCombo(Math.Max(500, host.Width - 65));
        certBox.Left = 18; certBox.Top = 238; certBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        foreach (var c in certs) certBox.Items.Add(new CertificateItem(c.Subject, c.Thumbprint, c.NotAfter, c.HasPrivateKey, c.OwnerName, c.Inn));
        certBox.DisplayMember = nameof(CertificateItem.Display);
        var selected = certBox.Items.Cast<CertificateItem>().ToList().FindIndex(x => x.Thumbprint.Equals(z.CertificateThumbprint, StringComparison.OrdinalIgnoreCase));
        if (selected >= 0) certBox.SelectedIndex = selected; else if (certBox.Items.Count > 0) certBox.SelectedIndex = 0;
        host.Controls.Add(certBox);

        var check = ActionButton("Kiểm tra chữ ký", 168, true); check.Left = 18; check.Top = 300; host.Controls.Add(check);
        var verified = new Label { Text = "", AutoSize = true, Left = 205, Top = 311, ForeColor = C.Muted }; host.Controls.Add(verified);
        check.Click += (_, _) =>
        {
            if (certBox.SelectedItem is not CertificateItem c) { verified.Text = "CHƯA CÓ CHỨNG THƯ"; verified.ForeColor = Color.OrangeRed; return; }
            verified.Text = c.NotAfter > DateTime.Now
                ? $"ĐÃ XÁC MINH · {c.OwnerName}{(string.IsNullOrWhiteSpace(c.Inn) ? "" : $" · INN {c.Inn}")}"
                : "CHỨNG THƯ HẾT HẠN";
            verified.ForeColor = c.NotAfter > DateTime.Now ? C.Green : Color.OrangeRed;
        };

        host.Controls.Add(new Label
        {
            Text = "Tự động đưa mã vào lưu thông sau khi tải mã: BẮT BUỘC",
            AutoSize = true, Left = 18, Top = 370, ForeColor = C.Green,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        });

        var save = ActionButton("Lưu cài đặt", 130, true); save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; host.Controls.Add(save);
        host.Resize += (_, _) => save.Location = new Point(host.ClientSize.Width - save.Width - 20, host.ClientSize.Height - save.Height - 18);
        save.Location = new Point(host.Width - save.Width - 20, host.Height - save.Height - 18);
        save.Click += (_, _) =>
        {
            var c = certBox.SelectedItem as CertificateItem;
            app.Db.SaveZnakConfig(new ZnakConfig(
                z.Inn, z.Environment, c?.Thumbprint ?? "", c?.Subject ?? "",
                z.AutoSignMode, true, omsId.Text.Trim(), omsConnection.Text.Trim(), true));
            ShowInfo("Đã lưu cấu hình Znack. Tự động đưa mã vào lưu thông luôn được bật.");
        };
    }

    private void RenderGtinCategories(Control host)
    {
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("gtin", "GTIN");
        grid.Columns.Add("count", "Số mã");
        grid.Columns.Add("available", "Sẵn sàng");
        foreach (var g in app.Db.Kiz().GroupBy(x => x.Gtin))
            grid.Rows.Add(g.Key, g.Count(), g.Count(x => x.Status == "AVAILABLE"));
        host.Controls.Add(grid);
    }

    private void RenderDeleted(Control host)
    {
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("time", "Thời gian");
        grid.Columns.Add("detail", "Thông tin");
        foreach (var a in app.Db.AuditRows().Where(x => x.Action.Contains("Xóa", StringComparison.OrdinalIgnoreCase)))
            grid.Rows.Add(a.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), a.Detail);
        host.Controls.Add(grid);
    }

    private void RenderOrderCodes(Control host)
    {
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("order", "Đơn hàng");
        grid.Columns.Add("gtin", "GTIN");
        grid.Columns.Add("status", "Trạng thái");
        grid.Columns.Add("code", "Mã KIZ");
        foreach (var k in app.Db.Kiz().Where(x => !string.IsNullOrWhiteSpace(x.Assigned)))
            grid.Rows.Add(k.Assigned, k.Gtin, TranslateKizStatus(k.Status), k.Code);
        host.Controls.Add(grid);
    }

    private void RenderAudit(Control host)
    {
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("at", "Thời gian");
        grid.Columns.Add("module", "Mục");
        grid.Columns.Add("action", "Thao tác");
        grid.Columns.Add("detail", "Chi tiết");
        foreach (var a in app.Db.AuditRows())
            grid.Rows.Add(a.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), TranslateModule(a.Module), TranslateAction(a.Action), a.Detail);
        host.Controls.Add(grid);
    }

    private void ShowDesignTools()
    {
        ClearWork();
        work.Controls.Add(Title("Thiết kế mẫu"));
        var card = CardPanel();
        card.Left = 4; card.Top = 70; card.Width = Math.Min(780, work.ClientSize.Width - 55); card.Height = 360;
        work.Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = "Thiết kế mẫu in",
            Left = 22, Top = 22, AutoSize = true, ForeColor = C.Text,
            Font = new Font("Segoe UI", 12, FontStyle.Bold)
        });
        card.Controls.Add(new Label
        {
            Text = "Mục này chỉ dành cho bố cục tem/nhãn. Thay đổi giá và Sao chép bài đăng đã được tách thành hai mục riêng ở menu trái.",
            Left = 22, Top = 62, Width = 700, Height = 55, ForeColor = C.Muted
        });
        var size = DarkCombo(220); size.Left = 22; size.Top = 130;
        size.Items.AddRange(new object[] { "58 × 40 mm", "A4", "Tùy chỉnh" }); size.SelectedIndex = 0; card.Controls.Add(size);
        var preview = CardPanel(); preview.Left = 280; preview.Top = 128; preview.Width = 310; preview.Height = 180; preview.BackColor = Color.White;
        card.Controls.Add(preview);
        preview.Controls.Add(new Label
        {
            Text = "MARKETPLACE HUB\nSKU / Barcode / KIZ",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Black, Font = new Font("Segoe UI", 12, FontStyle.Bold)
        });
    }

    private void ShowPriceTools()
    {
        ClearWork();
        work.Controls.Add(Title("Thay đổi giá"));
        var host = CardPanel(); host.Left = 4; host.Top = 70;
        host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Width = work.ClientSize.Width - 55; host.Height = work.ClientSize.Height - 100; work.Controls.Add(host);

        var grid = DarkGrid(); grid.Left = 0; grid.Top = 0; grid.Width = host.Width * 2 / 3; grid.Height = host.Height;
        grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewImageColumn { HeaderText = "Ảnh", Width = 85, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SKU", Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tên sản phẩm", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Giá hiện tại", Width = 130 });

        var store = CurrentStore();
        if (store != null)
        {
            foreach (var p in app.Db.Products(store.Id))
            {
                var row = grid.Rows.Add(null, p.Sku, p.Name, p.Price?.ToString("0.##") ?? "");
                grid.Rows[row].Tag = p; grid.Rows[row].Height = 78;
                var image = ProductImageUrl(p);
                if (!string.IsNullOrWhiteSpace(image)) _ = LoadImageAsync(grid, row, 0, image);
            }
        }
        host.Controls.Add(grid);

        var right = new Panel
        {
            Left = grid.Width + 18, Top = 20, Width = host.Width - grid.Width - 35, Height = 360,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = C.Card
        };
        host.Controls.Add(right);
        right.Controls.Add(new Label { Text = "Giá mới (RUB)", Left = 10, Top = 10, AutoSize = true, ForeColor = C.Text });
        var newPrice = new NumericUpDown { Left = 10, Top = 35, Width = 190, DecimalPlaces = 2, Maximum = 100000000, Minimum = 1 }; right.Controls.Add(newPrice);
        right.Controls.Add(new Label { Text = "Thay đổi %", Left = 10, Top = 82, AutoSize = true, ForeColor = C.Text });
        var percent = new NumericUpDown { Left = 10, Top = 108, Width = 190, DecimalPlaces = 2, Minimum = -99, Maximum = 500, Value = 10 }; right.Controls.Add(percent);
        var calc = ActionButton("Tính theo %", 135); calc.Left = 10; calc.Top = 158; right.Controls.Add(calc);
        var apply = ActionButton("Cập nhật giá", 145, true); apply.Left = 10; apply.Top = 215; right.Controls.Add(apply);

        calc.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not ProductRow p || p.Price is null) return;
            newPrice.Value = Math.Min(newPrice.Maximum, Math.Max(1, Math.Round(p.Price.Value * (1 + percent.Value / 100m), 2)));
        };
        apply.Click += async (_, _) =>
        {
            if (CurrentStore() is not StoreProfile current || grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not ProductRow p) return;
            if (MessageBox.Show($"Cập nhật {p.Sku} thành {newPrice.Value:0.##} RUB?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var result = await app.ChangePriceAsync(current, p, newPrice.Value);
            ShowInfo(result.Message);
            if (result.Success) await app.SyncProductsAsync(current);
        };

        SetWorkResize((_, _) => {
            host.Width = work.ClientSize.Width - 55; host.Height = Math.Max(320, work.ClientSize.Height - 100);
            grid.Width = host.Width * 2 / 3; grid.Height = host.Height;
            right.Left = grid.Width + 18; right.Width = Math.Max(250, host.Width - grid.Width - 35);
        });
    }

    private void ShowCopyListing()
    {
        ClearWork();
        work.Controls.Add(Title("Sao chép bài đăng"));
        var host = CardPanel(); host.Left = 4; host.Top = 70;
        host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Width = work.ClientSize.Width - 55; host.Height = work.ClientSize.Height - 100; work.Controls.Add(host);

        var stores = app.Db.Stores().ToList();
        host.Controls.Add(FieldLabel("Cửa hàng nguồn", 28, 22));
        var src = DarkCombo(320); src.Left = 22; src.Top = 52; src.DataSource = stores.ToList(); src.DisplayMember = nameof(StoreProfile.Name); host.Controls.Add(src);
        host.Controls.Add(FieldLabel("Cửa hàng đích", 110, 22));
        var dst = DarkCombo(320); dst.Left = 22; dst.Top = 134; dst.DataSource = stores.ToList(); dst.DisplayMember = nameof(StoreProfile.Name); host.Controls.Add(dst);
        host.Controls.Add(FieldLabel("SKU nguồn", 192, 22));
        var sourceSku = DarkText("SKU nguồn", 320); sourceSku.Left = 22; sourceSku.Top = 216; host.Controls.Add(sourceSku);
        host.Controls.Add(FieldLabel("SKU đích", 274, 22));
        var destSku = DarkText("SKU đích", 320); destSku.Left = 22; destSku.Top = 298; host.Controls.Add(destSku);
        var run = ActionButton("Sao chép bài đăng", 190, true); run.Left = 22; run.Top = 365; host.Controls.Add(run);

        var preview = CardPanel(); preview.Left = 380; preview.Top = 28; preview.Width = Math.Max(360, host.Width - 410); preview.Height = 430;
        preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; host.Controls.Add(preview);
        var picture = new PictureBox { Left = 18, Top = 18, Width = 170, Height = 230, SizeMode = PictureBoxSizeMode.Zoom, BackColor = C.RowAlt }; preview.Controls.Add(picture);
        var info = new Label { Left = 210, Top = 18, Width = preview.Width - 230, Height = 230, ForeColor = C.Text, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }; preview.Controls.Add(info);

        async Task Preview()
        {
            if (src.SelectedItem is not StoreProfile source) return;
            var p = app.Db.Product(source.Id, sourceSku.Text.Trim());
            if (p is null) { info.Text = "Không tìm thấy SKU nguồn."; picture.Image?.Dispose(); picture.Image = null; return; }
            info.Text = $"{p.Name}\nSKU: {p.Sku}\nGiá: {p.Price:0.##} RUB\nSàn: {source.Marketplace}";
            var url = ProductImageUrl(p);
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var bytes = await GetImageBytesAsync(url);
                picture.Image?.Dispose();
                picture.Image = DecodeProductImage(bytes);
            }
            catch { picture.Image?.Dispose(); picture.Image = null; }
        }

        sourceSku.Leave += async (_, _) => await Preview();
        src.SelectedIndexChanged += async (_, _) => await Preview();
        run.Click += async (_, _) =>
        {
            if (src.SelectedItem is not StoreProfile a || dst.SelectedItem is not StoreProfile b) return;
            var p = app.Db.Product(a.Id, sourceSku.Text.Trim()); if (p is null) { ShowInfo("Không tìm thấy SKU nguồn."); return; }
            if (string.IsNullOrWhiteSpace(destSku.Text)) { ShowInfo("Hãy nhập SKU đích."); return; }
            run.Enabled = false;
            var result = await app.Api.CopyListingAsync(a, b, p, destSku.Text.Trim());
            run.Enabled = true;
            app.Db.Audit("Sao chép", result.Success ? "Đã gửi" : "Lỗi", result.Message);
            ShowInfo(result.Message);
            if (result.Success) await app.SyncProductsAsync(b);
        };
    }

    private void ShowStores(bool newStore)
    {
        ClearWork();
        work.Controls.Add(Title(newStore ? "Thêm cửa hàng" : "Cài đặt"));

        var host = CardPanel(); host.Left = 4; host.Top = 70; host.Width = Math.Min(760, work.ClientSize.Width - 55); host.Height = 620; work.Controls.Add(host);

        var stores = app.Db.Stores().ToList();
        var market = DarkCombo(280); market.Left = 20; market.Top = 45; market.DataSource = Enum.GetValues<Marketplace>(); host.Controls.Add(market);
        host.Controls.Add(FieldLabel("Sàn", 18, 20));

        var name = LabeledDark(host, "Tên cửa hàng", "", 20, 100, 520);
        var client = LabeledDark(host, "Ozon Client-Id", "", 20, 160, 520);
        var api = LabeledDark(host, "API Key (Ozon/Yandex)", "", 20, 220, 520, true);
        var business = LabeledDark(host, "Yandex Business ID", "", 20, 280, 520);
        var campaign = LabeledDark(host, "Yandex Campaign ID", "", 20, 340, 520);
        var token = LabeledDark(host, "Wildberries Token", "", 20, 400, 520, true);
        var save = ActionButton("Lưu cửa hàng", 145, true); save.Left = 20; save.Top = 485; host.Controls.Add(save);
        var test = ActionButton("Kiểm tra API", 140); test.Left = 180; test.Top = 485; host.Controls.Add(test);
        var ozonTest = ActionButton("Kiểm tra Ozon chuyên sâu", 220); ozonTest.Left = 335; ozonTest.Top = 485; ozonTest.Visible = (Marketplace)market.SelectedItem! == Marketplace.Ozon; host.Controls.Add(ozonTest);
        market.SelectedIndexChanged += (_,_) => ozonTest.Visible = market.SelectedItem is Marketplace selected && selected == Marketplace.Ozon;

        long id = 0;
        if (!newStore && CurrentStore() is StoreProfile s)
        {
            id = s.Id; market.SelectedItem = s.Marketplace; name.Text = s.Name; client.Text = s.ClientId; api.Text = s.ApiKey; business.Text = s.BusinessId; campaign.Text = s.CampaignId; token.Text = s.Token;
        }

        StoreProfile Read() => new(id, (Marketplace)market.SelectedItem!, name.Text.Trim(), client.Text.Trim(), api.Text, business.Text.Trim(), campaign.Text.Trim(), token.Text, true);
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { ShowInfo("Hãy nhập tên cửa hàng."); return; }
            save.Enabled = false;
            try
            {
                if (id == 0)
                {
                    var result = await app.CreateStoreAsync(Read(), lifetimeCts.Token);
                    if (!result.Success || result.Store is null) { ShowInfo(result.Message); return; }
                    RefreshStores(result.Store.Id); ShowInfo(result.Message); ShowDashboard();
                }
                else
                {
                    var saved = app.Db.SaveStore(Read()); RefreshStores(saved.Id); ShowInfo("Đã lưu cửa hàng."); ShowDashboard();
                }
            }
            finally { if (!save.IsDisposed) save.Enabled = true; }
        };
        test.Click += async (_, _) =>
        {
            var r = await app.Api.TestAsync(Read()); ShowInfo(r.Message);
        };
        ozonTest.Click += (_, _) =>
        {
            using var dialog = new OzonDiagnosticsDialog(app.Api, Read());
            dialog.ShowDialog(this);
        };
    }

    private void ShowPrintHistory()
    {
        ClearWork();
        work.Controls.Add(Title("Lịch sử in"));
        var files = ActionButton("Mở các bộ PDF nhãn WB", 230);
        files.Left = 260; files.Top = 0;
        files.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(WbPrintBundleService.HistoryDirectory);
                Process.Start(new ProcessStartInfo(WbPrintBundleService.HistoryDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowInfo(ex.Message); }
        };
        work.Controls.Add(files);
        var card = CardPanel(); card.Left = 4; card.Top = 70; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; work.Controls.Add(card);
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill; grid.Columns.Add("time", "Thời gian"); grid.Columns.Add("action", "Trạng thái"); grid.Columns.Add("detail", "Chi tiết");
        foreach (var a in app.Db.AuditRows().Where(x => x.Module.Contains("nhãn", StringComparison.OrdinalIgnoreCase) || x.Module.Contains("In", StringComparison.OrdinalIgnoreCase)))
            grid.Rows.Add(a.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), TranslateAction(a.Action), a.Detail);
        card.Controls.Add(grid);
    }

    private void ShowFboOrders()
    {
        ClearWork();
        work.Controls.Add(Title("Đơn hàng FBO / FBW"));
        var refresh = ActionButton("↻ Đồng bộ yêu cầu nhập kho", 235, true); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Top = 0; work.Controls.Add(refresh);
        var state = new Label { Left = 4, Top = 50, AutoSize = true, ForeColor = C.Muted }; work.Controls.Add(state);

        var search = DarkText("Tìm mã yêu cầu, supply hoặc kho...", 430); search.Left = 4; search.Top = 80; work.Controls.Add(search);
        var card = CardPanel(); card.Left = 4; card.Top = 135;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 165; work.Controls.Add(card);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("order", "Mã yêu cầu");
        grid.Columns.Add("supply", "Supply");
        grid.Columns.Add("status", "Trạng thái");
        grid.Columns.Add("warehouse", "Kho");
        grid.Columns.Add("planned", "Lịch giao");
        grid.Columns.Add("qty", "Số lượng");
        grid.Columns.Add("accepted", "Đã nhận");
        card.Controls.Add(grid);

        void Load()
        {
            grid.Rows.Clear();
            var store = CurrentStore(); if (store is null) return;
            var q = search.Text.Trim();
            foreach (var x in app.Db.FboSupplies(store.Id))
            {
                if (!string.IsNullOrWhiteSpace(q) &&
                    !x.OrderId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !x.SupplyId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !x.Warehouse.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                grid.Rows.Add(x.OrderId, x.SupplyId, TranslateFboStatus(x.Status), x.Warehouse, x.PlannedAt, x.TotalQuantity, x.AcceptedQuantity);
            }
            var syncState = app.Db.SyncState(store.Id, "fbo_supplies");
            state.Text = string.IsNullOrWhiteSpace(syncState.LastSuccessAt)
                ? "Chưa có lần đồng bộ thành công."
                : $"Đồng bộ gần nhất: {syncState.LastSuccessAt}";
            if (!string.IsNullOrWhiteSpace(syncState.LastError)) state.Text += $" · Lỗi gần nhất: {syncState.LastError}";
        }

        refresh.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null) return;
            if (store.Marketplace == Marketplace.Yandex) { ShowInfo("Theo dõi FBO/FBW hiện hỗ trợ Wildberries và Ozon."); return; }
            refresh.Enabled = false;
            var r = await app.SyncFboSuppliesAsync(store);
            refresh.Enabled = true;
            state.Text = r.Message;
            state.ForeColor = r.Ok ? C.Green : C.Danger;
            Load();
        };
        search.TextChanged += (_, _) => Load();
        SetWorkResize((_, _) => {
            refresh.Left = work.ClientSize.Width - refresh.Width - 30;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(280, work.ClientSize.Height - 165);
        });
        Load();
    }

    private void ShowUnsupported(string title, string text)
    {
        ClearWork(); work.Controls.Add(Title(title));
        var card = CardPanel(); card.Left = 4; card.Top = 70; card.Width = 760; card.Height = 180; work.Controls.Add(card);
        card.Controls.Add(new Label { Text = text, Left = 22, Top = 30, Width = 700, Height = 100, ForeColor = C.Muted, Font = new Font("Segoe UI", 10) });
    }

    private RoundedButton TabButton(string text, bool selected, int width)
    {
        var b = ActionButton(text, width, selected); StyleTab(b, selected); return b;
    }

    private void StyleTab(RoundedButton b, bool active)
    {
        b.BackColor = active ? C.Purple : C.Card;
        b.BorderColor = active ? C.Purple : C.Border;
        b.ForeColor = active ? C.OnAccent : C.Text;
    }

    private TextBox LabeledDark(Control host, string label, string value, int left, int top, int width, bool password = false)
    {
        host.Controls.Add(FieldLabel(label, top, left));
        var t = DarkText("", width); t.Left = left + 230; t.Top = top - 8; t.Text = value; t.UseSystemPasswordChar = password; host.Controls.Add(t); return t;
    }

    private Label FieldLabel(string text, int top, int left = 18)
    {
        return new Label { Text = text, AutoSize = true, Left = left, Top = top, ForeColor = C.Text, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    }

    private async Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct = default)
    {
        if (!ct.CanBeCanceled) ct = pageCts.Token;
        if (imageCache.TryGetValue(url, out var cached)) return cached;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 MarketplaceHub");
        req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        using var res = await imageHttp.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 0)
        {
            if (imageCache.Count >= 250 || imageCache.Values.Sum(x => (long)x.Length) + bytes.Length > 64L * 1024 * 1024)
                imageCache.Clear();
            if (bytes.Length <= 4 * 1024 * 1024) imageCache[url] = bytes;
        }
        return bytes;
    }

    private static Bitmap DecodeProductImage(byte[] bytes, int maxEdge = 1024)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("Không giải mã được ảnh sản phẩm.");
        var scale = Math.Min(1d, (double)maxEdge / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1 ? bitmap.Resize(new SKImageInfo(
            Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), SKFilterQuality.Medium) : null;
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        using var ms = new MemoryStream(data.ToArray());
        using var temp = Image.FromStream(ms);
        return new Bitmap(temp);
    }



    private async Task LoadImageAsync(DataGridView grid, int row, int column, string url)
    {
        var ct = pageCts.Token;
        if (string.IsNullOrWhiteSpace(url) || grid.IsDisposed || row < 0 || row >= grid.Rows.Count || column < 0 || column >= grid.Columns.Count) return;
        var target = grid.Rows[row];
        try
        {
            await imageWorkers.WaitAsync(ct);
            Bitmap img;
            try
            {
                var bytes = await GetImageBytesAsync(url, ct);
                img = await Task.Run(() => DecodeProductImage(bytes, 160), ct);
            }
            finally { imageWorkers.Release(); }
            if (!grid.IsDisposed && !ct.IsCancellationRequested && target.Index >= 0 && grid.Rows.Contains(target) && column < target.Cells.Count)
            {
                var old = target.Cells[column].Value as Image;
                target.Cells[column].Value = img;
                old?.Dispose();
            }
            else img.Dispose();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private ProductRow? FindProductForOrder(StoreProfile store, FbsOrderRow order)
    {
        if (!pageProducts.TryGetValue(store.Id, out var products))
            pageProducts[store.Id] = products = app.Db.Products(store.Id);
        return ProductCatalog.ResolveOrder(store,order,products);
    }

    private string ProductImageUrl(ProductRow? product)
    {
        return product is null ? "" : ProductCatalog.Image(product);
    }

    private static string NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var textValue = value.Trim().Trim('"');
        if (Uri.TryCreate(textValue, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return textValue;
        try { return FirstHttpImage(JsonNode.Parse(value)); }
        catch { return ""; }
    }

    private static string FirstHttpImage(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue)
        {
            var value = node.ToString().Trim().Trim('"');
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return value;
            return "";
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var value = FirstHttpImage(item);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "big", "c516x688", "square", "url", "file_name", "image_url", "pictures", "images", "primary_photo", "photo" })
            {
                if (obj[key] is null) continue;
                var value = FirstHttpImage(obj[key]);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            foreach (var pair in obj)
            {
                var value = FirstHttpImage(pair.Value);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return "";
    }

    private bool PrintLabelFile(Marketplace marketplace, string path, out string error)
    {
        error = "";
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "print",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                app.Db.Audit("In nhãn", "Đã gửi in", $"{marketplace}:{path}");
                return true;
            }

            using var image = Image.FromFile(path);
            using var document = new PrintDocument();
            document.PrintController = new StandardPrintController();
            document.DocumentName = $"Marketplace Hub - {MarketplaceName(marketplace)} label";
            if (marketplace == Marketplace.Wildberries)
            {
                document.DefaultPageSettings.PaperSize = new PaperSize("58x40mm", 228, 157);
                document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            }
            document.PrintPage += (_, e) =>
            {
                e.Graphics.DrawImage(image, e.PageBounds);
                e.HasMorePages = false;
            };
            document.Print();
            app.Db.Audit("In nhãn", "Đã in", $"{marketplace}:{path}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ZXing.Rendering.PixelData CreateKizBarcode(string code)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.DATA_MATRIX,
            Options = new EncodingOptions { Width = 300, Height = 300, Margin = 2, PureBarcode = true }
        };
        // The legacy encoder maps ASCII GS to FNC1. CompactEncoding in ZXing 0.16.10
        // corrupts a GS after some C40 runs; keep this path and its decode regression.
        return writer.Write("\u001d" + (code.StartsWith('\u001d') ? code[1..] : code));
    }

    private bool PrintKizLabel(string code, string orderId, Marketplace marketplace, out string error)
    {
        error = "";
        try
        {
            var pixels = CreateKizBarcode(code);
            using var matrix = new Bitmap(pixels.Width, pixels.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = matrix.LockBits(
                new Rectangle(0, 0, matrix.Width, matrix.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                matrix.PixelFormat);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels.Pixels, 0, data.Scan0, pixels.Pixels.Length);
            }
            finally { matrix.UnlockBits(data); }

            using var doc = new PrintDocument();
            doc.PrintController = new StandardPrintController();
            doc.DocumentName = $"KIZ {MarketplaceName(marketplace)} {orderId}";
            doc.DefaultPageSettings.PaperSize = new PaperSize("58x40mm", 228, 157);
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.PrintPage += (_, e) =>
            {
                var bounds = e.PageBounds;
                var size = Math.Min(bounds.Height - 18, bounds.Width / 2);
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(matrix, new Rectangle(6, 6, size, size));
                using var font = new Font("Segoe UI", 7, FontStyle.Bold);
                using var small = new Font("Segoe UI", 6);
                e.Graphics.DrawString($"{MarketplaceName(marketplace)} · KIZ", font, Brushes.Black, size + 14, 10);
                e.Graphics.DrawString($"Đơn: {orderId}", small, Brushes.Black, size + 14, 30);
                var safe = code.Replace("\u001d", "<GS>");
                if (safe.Length > 42) safe = safe[..42] + "…";
                e.Graphics.DrawString(safe, small, Brushes.Black, new RectangleF(size + 14, 52, bounds.Width - size - 20, bounds.Height - 58));
                e.HasMorePages = false;
            };
            doc.Print();
            app.Db.Audit("KIZ", "In KIZ", $"{marketplace}:{orderId}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private ProductMetaValue ProductMeta(ProductRow p)
    {
        try
        {
            var root = JsonNode.Parse(p.RawJson);
            var offer = root?["offer"] ?? root;
            var size = root?["sizes"]?.AsArray()?.FirstOrDefault()?["techSize"]?.ToString()
                       ?? offer?["size"]?.ToString()
                       ?? "";
            var barcode = root?["sizes"]?.AsArray()?.FirstOrDefault()?["skus"]?.AsArray()?.FirstOrDefault()?.ToString()
                          ?? offer?["barcodes"]?.AsArray()?.FirstOrDefault()?.ToString()
                          ?? root?["barcodes"]?.AsArray()?.FirstOrDefault()?.ToString()
                          ?? "";

            string Char(params string[] names)
            {
                foreach (var c in root?["characteristics"]?.AsArray() ?? new JsonArray())
                {
                    var name = c?["name"]?.ToString() ?? "";
                    if (!names.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    var value = c?["value"];
                    if (value is JsonArray arr)
                        return string.Join(", ", arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return value?.ToString() ?? "";
                }
                return "";
            }

            var category = root?["subjectName"]?.ToString()
                           ?? root?["categoryName"]?.ToString()
                           ?? offer?["category"]?.ToString()
                           ?? root?["category"]?.ToString()
                           ?? "";
            var article = root?["vendorCode"]?.ToString()
                          ?? offer?["vendorCode"]?.ToString()
                          ?? offer?["offerId"]?.ToString()
                          ?? root?["offer_id"]?.ToString()
                          ?? p.Sku;
            var brand = root?["brand"]?.ToString()
                        ?? offer?["vendor"]?.ToString()
                        ?? "";
            var tnved = Char("ТН ВЭД", "TN VED", "ТНВЭД", "tnved");
            return new ProductMetaValue(
                Char("Пол", "gender", "Giới tính"),
                Char("Цвет", "color", "Màu"),
                size, barcode, category, tnved, article, brand);
        }
        catch
        {
            return new ProductMetaValue("", "", "", "", "", "", p.Sku, "");
        }
    }

    private static string FormatOrderTime(FbsOrderRow order)
    {
        try
        {
            var root = JsonNode.Parse(order.RawJson);
            foreach (var key in new[] { "createdAt", "created_at", "in_process_at", "shipment_date", "date" })
            {
                var value = root?[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, out var at))
                {
                    var local = at.ToLocalTime();
                    var ago = DateTimeOffset.Now - local;
                    var suffix = ago.TotalMinutes >= 0 && ago.TotalHours < 24 ? $" · {Math.Max(0, (int)ago.TotalMinutes)} phút trước" : "";
                    return local.ToString("dd.MM.yyyy HH:mm") + suffix;
                }
            }
        }
        catch { }
        return "";
    }

    private string BuildProductMetaLine(ProductRow? product, FbsOrderRow order)
    {
        if (product is null) return order.Sku;
        var meta = ProductMeta(product);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(meta.Brand)) parts.Add(meta.Brand);
        if (!string.IsNullOrWhiteSpace(meta.Article)) parts.Add(meta.Article);
        if (!string.IsNullOrWhiteSpace(meta.Color)) parts.Add(meta.Color);
        if (!string.IsNullOrWhiteSpace(meta.Size)) parts.Add($"Size: {meta.Size}");
        if (parts.Count == 0) parts.Add(order.Sku);
        return string.Join(" · ", parts);
    }

    private static string SupplyIdFrom(FbsOrderRow order)
    {
        try
        {
            var root = JsonNode.Parse(order.RawJson);
            return root?["supplyId"]?.ToString()
                   ?? root?["supply_id"]?.ToString()
                   ?? root?["supply"]?["id"]?.ToString()
                   ?? "";
        }
        catch { return ""; }
    }

    private string? PromptText(string title, string prompt)
    {
        using var dialog = new Form
        {
            Text = title, Width = 640, Height = 220, StartPosition = FormStartPosition.CenterParent,
            BackColor = C.Main, ForeColor = C.Text, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        dialog.Controls.Add(new Label { Text = prompt, Left = 18, Top = 18, Width = 580, Height = 42, ForeColor = C.Muted });
        var box = new TextBox { Left = 18, Top = 70, Width = 585, BackColor = C.Card, ForeColor = C.Text, BorderStyle = BorderStyle.FixedSingle };
        dialog.Controls.Add(box);
        var ok = ActionButton("Lưu", 110, true); ok.Left = 375; ok.Top = 115; ok.DialogResult = DialogResult.OK; dialog.Controls.Add(ok);
        var cancel = ActionButton("Hủy", 110); cancel.Left = 493; cancel.Top = 115; cancel.DialogResult = DialogResult.Cancel; dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private void ShowSupportDialog()
    {
        using var dialog = new Form
        {
            Text = "Hỗ trợ Marketplace Hub",
            Width = 620,
            Height = 420,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = C.Main,
            ForeColor = C.Text,
            KeyPreview = true,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var info = new Label
        {
            Text = "Mô tả vấn đề. Enter để gửi, Shift+Enter để xuống dòng.",
            Left = 18, Top = 18, Width = 560, ForeColor = C.Muted
        };
        dialog.Controls.Add(info);

        var editor = new TextBox
        {
            Left = 18, Top = 52, Width = 568, Height = 255,
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = C.Card, ForeColor = C.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        dialog.Controls.Add(editor);

        var send = ActionButton("➤  Gửi", 110, true);
        send.Left = 476; send.Top = 320;
        dialog.Controls.Add(send);

        void Submit()
        {
            var body = editor.Text.Trim();
            if (string.IsNullOrWhiteSpace(body)) return;
            app.Db.Audit("Hỗ trợ", "Yêu cầu", body.Length > 500 ? body[..500] : body);
            var url = "https://github.com/ntccong2468-lab/GTIN-Sync-WB/issues/new?title="
                      + Uri.EscapeDataString("Hỗ trợ Marketplace Hub")
                      + "&body=" + Uri.EscapeDataString(body);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            dialog.Close();
        }

        send.Click += (_, _) => Submit();
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                Submit();
            }
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) dialog.Close();
        };

        dialog.ShowDialog(this);
    }

    private void ShowInfo(string message) => MessageBox.Show(message, "Marketplace Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static bool IsNew(string status) =>
        new[] { "new", "awaiting_packaging", "PROCESSING/STARTED", "PROCESSING/CONFIRMED" }.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static bool IsPacking(string status) =>
        new[] { "confirm", "assembling", "awaiting_deliver", "PROCESSING/PACKING", "PROCESSING/READY_FOR_DELIVERY", "PROCESSING/READY_TO_SHIP" }.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static bool IsShipping(string status) =>
        new[] { "complete", "delivering", "deliver", "DELIVERY/", "PICKUP/" }.Contains(status, StringComparer.OrdinalIgnoreCase);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            autoSync.Stop();
            autoSync.Dispose();
            lifetimeCts.Cancel();
            pageCts.Cancel();
            DisposeImages(work);
            imageHttp.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string TranslateKizStatus(string s) => s switch
    {
        "AVAILABLE" => "Sẵn sàng", "RESERVED" => "Đã giữ cho đơn, chờ WB xác nhận",
        "ASSIGNED" => "Đã gán",
        "SHIPPED" => "Đã giao", "CONSUMED" => "Đã sử dụng", "INTRODUCED" => "Đã lưu thông",
        "INVALID" => "Không hợp lệ", "RETIRED" => "Đã ngừng", _ => s
    };

    private static string TranslateFboStatus(string s) => s.ToUpperInvariant() switch
    {
        "1" or "DATA_FILLING" => "Đang chuẩn bị",
        "2" or "3" or "READY_TO_SUPPLY" => "Sẵn sàng",
        "4" or "6" or "ACCEPTED_AT_SUPPLY_WAREHOUSE" or "IN_TRANSIT" or "ACCEPTANCE_AT_STORAGE_WAREHOUSE" => "Đang tiếp nhận",
        "5" or "COMPLETED" => "Hoàn tất",
        "REPORTS_CONFIRMATION_AWAITING" => "Chờ xác nhận",
        "REPORT_REJECTED" or "REJECTED_AT_SUPPLY_WAREHOUSE" or "OVERDUE" => "Có vấn đề",
        "CANCELLED" => "Đã hủy",
        _ => s
    };

    private static string TranslateModule(string s) => s switch
    {
        "Stores" => "Cửa hàng", "Products" => "Sản phẩm", "Price" => "Giá",
        "Copy" => "Sao chép", "KIZ" => "Mã KIZ", "FBS" => "Đóng hàng FBS",
        "FBO" => "Đơn hàng FBO", "Finance" => "Tài chính", "Đăng ký Znack" => "Đăng ký Znack",
        "SelfTest" => "Tự kiểm tra", _ => s
    };

    private static string TranslateAction(string s) => s switch
    {
        "Save" => "Lưu", "Sync" => "Đồng bộ", "Accepted" => "Đã tiếp nhận",
        "Failed" => "Lỗi", "Upsert" => "Cập nhật", "Database" => "Cơ sở dữ liệu", _ => s
    };

    private sealed record CertificateItem(
        string Subject,
        string Thumbprint,
        DateTime NotAfter,
        bool HasPrivateKey,
        string OwnerName,
        string Inn)
    {
        public string Display =>
            $"{OwnerName}{(string.IsNullOrWhiteSpace(Inn) ? "" : $" · INN {Inn}")} · Hết hạn {NotAfter:dd.MM.yyyy}";
    }

    private sealed record ProductMetaValue(
        string Gender, string Color, string Size, string Barcode,
        string Category, string Tnved, string Article, string Brand);
}

internal static class C
{
    public static readonly Color Main = Color.FromArgb(15, 23, 42);
    public static readonly Color Side = Color.FromArgb(15, 23, 42);
    public static readonly Color Card = Color.FromArgb(30, 41, 59);
    public static readonly Color RowAlt = Color.FromArgb(25, 35, 53);
    public static readonly Color Border = Color.FromArgb(51, 65, 85);
    public static readonly Color Purple = Color.FromArgb(163, 68, 252);
    public static readonly Color Orange = Color.FromArgb(251, 146, 60);
    public static readonly Color Green = Color.FromArgb(34, 197, 94);
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);
    public static readonly Color Muted = Color.FromArgb(148, 163, 184);
    public static readonly Color Text = Color.FromArgb(241, 245, 249);
    public static readonly Color OnAccent = Color.White;
    public static readonly Color Soft = Color.FromArgb(76, 45, 110);
}

internal sealed class BrandMarkControl : Control
{
    public BrandMarkControl()
    {
        DoubleBuffered = true;
        BackColor = C.Side;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(2, 2, Width - 5, Height - 5);
        using var white = new SolidBrush(Color.White);
        e.Graphics.FillEllipse(white, rect);
        using var pen = new Pen(C.Side, Math.Max(2, Width / 12f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        var pts = new[]
        {
            new PointF(Width * .25f, Height * .66f),
            new PointF(Width * .25f, Height * .36f),
            new PointF(Width * .41f, Height * .52f),
            new PointF(Width * .50f, Height * .36f),
            new PointF(Width * .60f, Height * .52f),
            new PointF(Width * .75f, Height * .36f),
            new PointF(Width * .75f, Height * .66f)
        };
        e.Graphics.DrawLines(pen, pts);
    }
}

internal sealed class ReportBarChart : Control
{
    public double[] Values { get; set; } = Array.Empty<double>();
    public string[] Labels { get; set; } = Array.Empty<string>();

    public ReportBarChart()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Values.Length == 0) return;

        var chart = new Rectangle(38, 8, Math.Max(10, Width - 48), Math.Max(10, Height - 34));
        using var gridPen = new Pen(C.Border, 1) { DashStyle = DashStyle.Dash };
        for (var i = 1; i <= 3; i++)
        {
            var y = chart.Top + chart.Height * i / 4;
            e.Graphics.DrawLine(gridPen, chart.Left, y, chart.Right, y);
        }

        var max = Math.Max(1d, Values.Max());
        var slot = chart.Width / (double)Math.Max(1, Values.Length);
        using var brush = new SolidBrush(Color.FromArgb(126, 191, 171));
        using var textBrush = new SolidBrush(C.Muted);
        using var font = new Font("Segoe UI", 7.5f);
        var labelStride = Math.Max(1, (int)Math.Ceiling(Values.Length / (double)Math.Max(1, chart.Width / 60)));
        for (var level = 0; level <= 4; level++)
        {
            var value = max * level / 4;
            e.Graphics.DrawString(value.ToString("0.#"), font, textBrush, 2, chart.Bottom - chart.Height * level / 4 - 6);
        }

        for (var i = 0; i < Values.Length; i++)
        {
            var h = (int)Math.Round((Values[i] / max) * (chart.Height - 8));
            var x = (int)Math.Round(chart.Left + i * slot + slot * .22);
            var w = Math.Max(1, (int)Math.Round(slot * .56));
            var y = chart.Bottom - h;
            e.Graphics.FillRectangle(brush, x, y, w, h);
            var label = i < Labels.Length ? Labels[i] : "";
            if (i % labelStride == 0) e.Graphics.DrawString(label, font, textBrush, x - 4, chart.Bottom + 4);
        }
    }
}



internal sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 10;
    public Color BorderColor { get; set; } = Color.Transparent;
    public int BorderWidth { get; set; }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        Resize += (_, _) => UpdateShape();
    }

    private void UpdateShape()
    {
        if (Width <= 1 || Height <= 1) return;
        using var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawPath(pen, path);
    }

    internal static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedButton : Button
{
    public int Radius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.Transparent;
    private bool hovered;
    private bool pressed;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Resize += (_, _) => UpdateShape();
        MouseEnter += (_, _) => { hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { hovered = false; pressed = false; Invalidate(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } };
        MouseUp += (_, _) => { pressed = false; Invalidate(); };
    }

    private void UpdateShape()
    {
        if (Width <= 1 || Height <= 1) return;
        using var path = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPanel.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        var fill = !Enabled ? ControlPaint.Dark(BackColor, 0.25f)
                 : pressed ? ControlPaint.Dark(BackColor, 0.08f)
                 : hovered ? ControlPaint.Light(BackColor, 0.08f)
                 : BackColor;
        using var brush = new SolidBrush(fill);
        pevent.Graphics.FillPath(brush, path);
        if (BorderColor != Color.Transparent)
        {
            using var pen = new Pen(BorderColor);
            pevent.Graphics.DrawPath(pen, path);
        }

        var rect = new Rectangle(
            ClientRectangle.X + Padding.Left,
            ClientRectangle.Y + Padding.Top,
            Math.Max(0, ClientRectangle.Width - Padding.Horizontal),
            Math.Max(0, ClientRectangle.Height - Padding.Vertical));

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft)
            flags |= TextFormatFlags.Left;
        else if (TextAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight)
            flags |= TextFormatFlags.Right;
        else
            flags |= TextFormatFlags.HorizontalCenter;

        TextRenderer.DrawText(pevent.Graphics, Text, Font, rect, Enabled ? ForeColor : Color.FromArgb(150, ForeColor), flags);
    }
}
