using MarketplaceHub.Core;
using MarketplaceHub.Services;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed class MainForm : Form
{
    private readonly AppServices app;
    private readonly Panel work = new() { Dock = DockStyle.Fill, BackColor = C.Main };
    private readonly ComboBox storePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 275 };
    private readonly Label statusLabel = new() { AutoSize = true, ForeColor = C.Green, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly RoundedButton syncButton = new() { Text = "↻  Đồng bộ", Width = 175, Height = 44 };
    private readonly HttpClient imageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, byte[]> imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim znakWorkers = new(2, 2);
    private readonly ConcurrentDictionary<string, string> znakQueueStatus = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool znakQueuePaused;

    public MainForm(AppServices services)
    {
        app = services;
        Text = "Marketplace Hub";
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
    }

    private Control BuildSidebar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Side };
        p.Paint += (_, e) => e.Graphics.DrawLine(new Pen(C.Border), p.Width - 1, 0, p.Width - 1, p.Height);

        var logoBox = new RoundedPanel { Radius = 10, BackColor = C.Purple, Width = 48, Height = 48, Left = 58, Top = 10 };
        logoBox.Controls.Add(new Label { Text = "▥", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI Symbol", 20, FontStyle.Bold) });
        p.Controls.Add(logoBox);
        p.Controls.Add(new Label { Text = "Marketplace", AutoSize = true, Left = 118, Top = 18, ForeColor = Color.White, Font = new Font("Segoe UI", 16, FontStyle.Bold) });

        var design = NavButton("✎  Thiết kế mẫu", C.Orange, 238);
        design.Left = 13; design.Top = 75; design.Click += (_, _) => ShowDesignTools(); p.Controls.Add(design);

        var addStore = NavButton("＋  Thêm cửa hàng", C.Purple, 238);
        addStore.Left = 13; addStore.Top = 133; addStore.Click += (_, _) => ShowStores(true); p.Controls.Add(addStore);

        p.Controls.Add(new Label { Text = "-", AutoSize = true, Left = 129, Top = 194, ForeColor = Color.White });

        var menu = new FlowLayoutPanel
        {
            Left = 13, Top = 225, Width = 244, Height = 410,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = C.Side,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
        };
        p.Controls.Add(menu);

        AddSide(menu, "▦  Tổng quan", ShowDashboard);
        AddSide(menu, "₽  Tài chính", ShowFinance);
        AddSide(menu, "↻  Đồng bộ Ozon", () => ShowMarketplaceSync(Marketplace.Ozon));
        AddSide(menu, "↻  Đồng bộ Yandex", () => ShowMarketplaceSync(Marketplace.Yandex));
        AddSide(menu, "₽  Thay đổi giá", ShowPriceTools);
        AddSide(menu, "⇄  Sao chép bài đăng", ShowCopyListing);
        AddSide(menu, "▱  Đóng hàng FBS", ShowFbs);
        AddSide(menu, "◇  Đóng hàng FBO", ShowFboPacking);
        AddSide(menu, "☷  Đơn hàng FBO", ShowFboOrders);
        AddSide(menu, "⌁  Ánh xạ KIZ", ShowKizMapping);
        AddSide(menu, "▣  Đăng ký Znack", ShowZnakRegistration);
        AddSide(menu, "◇  Cấu hình Znack", ShowZnakSettings);

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
            printHistory.Top = Math.Max(650, p.ClientSize.Height - 230);
            menu.Height = Math.Max(230, printHistory.Top - menu.Top - 12);
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
        storePicker.BackColor = C.Card; storePicker.ForeColor = Color.White;
        storePicker.Font = new Font("Segoe UI", 11);
        ConfigureDarkCombo(storePicker);
        storePicker.SelectedIndexChanged += (_, _) => UpdateSyncButton();
        p.Controls.Add(storePicker);

        var edit = IconButton("✎"); edit.Left = 310; edit.Top = 18; edit.Click += (_, _) => ShowStores(false); p.Controls.Add(edit);
        var del = IconButton("♲"); del.Left = 360; del.Top = 18; del.Click += (_, _) => DeleteCurrentStore(); p.Controls.Add(del);

        syncButton.BackColor = C.Purple; syncButton.ForeColor = Color.White; syncButton.BorderColor = C.Purple;
        syncButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; syncButton.Top = 20;
        syncButton.Click += async (_, _) => await SyncAllForCurrentStore();
        p.Controls.Add(syncButton);

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
        };

        return p;
    }

    private void AddSide(FlowLayoutPanel menu, string text, Action action)
    {
        var b = NavButton(text, C.Purple, 238);
        b.Margin = new Padding(0, 0, 0, 14);
        b.Click += (_, _) => action();
        menu.Controls.Add(b);
    }

    private RoundedButton NavButton(string text, Color bg, int width)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 45, Radius = 8,
            BackColor = bg, ForeColor = Color.White, BorderColor = bg,
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 10, 0)
        };
    }

    private RoundedButton DarkOutline(string text, int width)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 42, Radius = 8,
            BackColor = C.Side, ForeColor = Color.White, BorderColor = C.Border,
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 10, 0)
        };
    }

    private RoundedButton IconButton(string text)
    {
        return new RoundedButton
        {
            Text = text, Width = 40, Height = 40, Radius = 8,
            BackColor = C.Main, ForeColor = Color.White, BorderColor = C.Main,
            Font = new Font("Segoe UI Symbol", 16), Cursor = Cursors.Hand
        };
    }

    private RoundedButton ActionButton(string text, int width = 130, bool purple = false)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 42, Radius = 8,
            BackColor = purple ? C.Purple : C.Card,
            ForeColor = Color.White,
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
        var p = await app.SyncProductsAsync(s);
        var o = await app.SyncOrdersAsync(s);
        var fbo = s.Marketplace is Marketplace.Wildberries or Marketplace.Ozon
            ? await app.SyncFboSuppliesAsync(s)
            : (true, "FBO/FBW không áp dụng cho cửa hàng này.");
        syncButton.Enabled = true;
        statusLabel.Text = p.Ok && o.Ok && fbo.Item1 ? "• Đang hoạt động" : "• Có lỗi đồng bộ";
        statusLabel.ForeColor = p.Ok && o.Ok && fbo.Item1 ? C.Green : Color.OrangeRed;
        ShowInfo(p.Message + Environment.NewLine + o.Message + Environment.NewLine + fbo.Item2);
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
        work.Controls.Clear();
        work.Padding = new Padding(28, 26, 26, 24);
    }

    private Label Title(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), Left = 4, Top = 5 };
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
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = C.Card;
        g.AlternatingRowsDefaultCellStyle.BackColor = C.RowAlt;
        g.DefaultCellStyle.ForeColor = Color.White;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 53, 73);
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
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
            BackColor = C.Card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10)
        };
    }

    private ComboBox DarkCombo(int width)
    {
        var combo = new ComboBox
        {
            Width = width, Height = 44, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = C.Card, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
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
            TextRenderer.DrawText(e.Graphics, value, combo.Font, rect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
    }

    private void ShowDashboard()
    {
        ClearWork();
        var title = Title("Tổng quan");
        work.Controls.Add(title);

        var refresh = ActionButton("↻ Làm mới", 130);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Top = 0;
        refresh.Click += (_, _) => ShowDashboard();
        work.Controls.Add(refresh);

        var sync = ActionButton("↻ Đồng bộ", 150);
        sync.Anchor = AnchorStyles.Top | AnchorStyles.Right; sync.Top = 0;
        sync.Click += async (_, _) => await SyncAllForCurrentStore();
        work.Controls.Add(sync);

        work.Resize += (_, _) =>
        {
            refresh.Left = work.ClientSize.Width - refresh.Width - 30;
            sync.Left = refresh.Left - sync.Width - 12;
        };

        var range = new FlowLayoutPanel { Left = 4, Top = 58, Width = 850, Height = 52, BackColor = C.Main, WrapContents = false };
        range.Controls.Add(ActionButton("7 ngày", 94));
        range.Controls.Add(ActionButton("30 ngày", 104));
        range.Controls.Add(ActionButton("90 ngày", 104));
        range.Controls.Add(new Label { Text = "Từ ngày", ForeColor = C.Muted, Width = 70, Height = 42, TextAlign = ContentAlignment.MiddleCenter });
        var from = new DateTimePicker { Width = 150, Format = DateTimePickerFormat.Short, CalendarMonthBackground = C.Card };
        range.Controls.Add(from);
        range.Controls.Add(new Label { Text = "Đến ngày", ForeColor = C.Muted, Width = 78, Height = 42, TextAlign = ContentAlignment.MiddleCenter });
        var to = new DateTimePicker { Width = 150, Format = DateTimePickerFormat.Short };
        range.Controls.Add(to);
        work.Controls.Add(range);

        var s = CurrentStore();
        var stores = app.Db.Stores();
        var products = s is null ? 0 : app.Db.Products(s.Id).Count;
        var orders = s is null ? Array.Empty<FbsOrderRow>() : app.Db.Orders(s.Id).ToArray();
        var kiz = app.Db.Kiz();
        var certs = AppServices.Certificates();

        var metrics = new (string Label, string Value)[]
        {
            ("Sản phẩm", products.ToString()),
            ("Đơn FBS", orders.Length.ToString()),
            ("Đơn mới", orders.Count(x => IsNew(x.Status)).ToString()),
            ("Đang đóng gói", orders.Count(x => IsPacking(x.Status)).ToString()),
            ("Đang giao", orders.Count(x => IsShipping(x.Status)).ToString()),
            ("Mã KIZ", kiz.Count.ToString()),
            ("KIZ sẵn sàng", kiz.Count(x => x.Status == "AVAILABLE").ToString()),
            ("Cửa hàng", stores.Count.ToString()),
            ("Chứng thư số", certs.Count(x => x.HasPrivateKey).ToString()),
            ("Lỗi gần đây", app.Db.AuditRows(50).Count(x => x.Action.Contains("Lỗi", StringComparison.OrdinalIgnoreCase) || x.Action.Contains("Failed", StringComparison.OrdinalIgnoreCase)).ToString())
        };

        var metricPanel = new TableLayoutPanel { Left = 4, Top = 118, Height = 132, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 5, RowCount = 2, BackColor = C.Main };
        metricPanel.Width = work.ClientSize.Width - 55;
        for (var i = 0; i < 5; i++) metricPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        metricPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        metricPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        for (var i = 0; i < metrics.Length; i++)
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = C.Main, Margin = new Padding(4) };
            box.Controls.Add(new Label { Text = metrics[i].Label, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Left = 3, Top = 4 });
            box.Controls.Add(new Label { Text = metrics[i].Value, AutoSize = true, ForeColor = i == 9 ? C.Green : Color.White, Font = new Font("Segoe UI", 10), Left = 3, Top = 30 });
            metricPanel.Controls.Add(box, i % 5, i / 5);
        }
        work.Controls.Add(metricPanel);

        var card = CardPanel();
        card.Left = 4; card.Top = 270; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 300;
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("time", "Thời gian");
        grid.Columns.Add("module", "Mục");
        grid.Columns.Add("action", "Thao tác");
        grid.Columns.Add("detail", "Chi tiết");
        foreach (var a in app.Db.AuditRows(20))
            grid.Rows.Add(a.At.ToLocalTime().ToString("dd/MM/yy HH:mm"), TranslateModule(a.Module), TranslateAction(a.Action), a.Detail);
        card.Controls.Add(grid);
        work.Controls.Add(card);

        work.Resize += (_, _) =>
        {
            metricPanel.Width = work.ClientSize.Width - 55;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(220, work.ClientSize.Height - 300);
        };
    }

    private void ShowFinance()
    {
        ClearWork();
        work.Controls.Add(Title("Dashboard tài chính"));

        var store = CurrentStore();
        if (store is null)
        {
            ShowInfo("Chưa có cửa hàng.");
            return;
        }

        var quick = new FlowLayoutPanel { Left = 4, Top = 60, Width = 820, Height = 48, BackColor = C.Main, WrapContents = false };
        var b7 = ActionButton("7 ngày", 92, true);
        var b30 = ActionButton("30 ngày", 100);
        var b45 = ActionButton("45 ngày", 100);
        var from = new DateTimePicker { Width = 145, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddDays(-6) };
        var to = new DateTimePicker { Width = 145, Format = DateTimePickerFormat.Short, Value = DateTime.Today };
        quick.Controls.Add(b7); quick.Controls.Add(b30); quick.Controls.Add(b45);
        quick.Controls.Add(from); quick.Controls.Add(to);
        work.Controls.Add(quick);

        var refresh = ActionButton("↻ Đồng bộ tài chính", 190, true);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Top = 60; work.Controls.Add(refresh);

        var status = new Label { Left = 4, Top = 112, AutoSize = true, ForeColor = C.Muted, Text = "Chưa tải dữ liệu tài chính." };
        work.Controls.Add(status);

        var cards = new TableLayoutPanel
        {
            Left = 4, Top = 145, Height = 210, ColumnCount = 5, RowCount = 2,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = C.Main
        };
        cards.Width = work.ClientSize.Width - 55;
        for (var i = 0; i < 5; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        work.Controls.Add(cards);

        var tableCard = CardPanel();
        tableCard.Left = 4; tableCard.Top = 375;
        tableCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tableCard.Width = work.ClientSize.Width - 55; tableCard.Height = work.ClientSize.Height - 405;
        work.Controls.Add(tableCard);
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("time", "Thời gian");
        grid.Columns.Add("stream", "Luồng đồng bộ");
        grid.Columns.Add("read", "Đọc");
        grid.Columns.Add("written", "Ghi");
        grid.Columns.Add("result", "Kết quả");
        tableCard.Controls.Add(grid);

        void FillRuns()
        {
            grid.Rows.Clear();
            foreach (var run in app.Db.SyncRuns(store.Id, 30))
                grid.Rows.Add(run.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), run.Stream, run.ReadCount, run.WrittenCount,
                    run.Success ? "Thành công" : $"Lỗi: {run.Error}");
        }

        void Render(FinanceSnapshot f)
        {
            cards.Controls.Clear();
            var cost = f.Delivery + f.Storage + f.Acceptance + f.Deductions + f.Penalties;
            var net = f.Payout + f.AdditionalPayments - f.Cashback;
            var items = new[]
            {
                ("Doanh thu", f.Revenue), ("Thanh toán", f.Payout), ("Logistics", f.Delivery),
                ("Lưu kho", f.Storage), ("Nhập kho", f.Acceptance), ("Khấu trừ", f.Deductions),
                ("Phạt", f.Penalties), ("Thanh toán bổ sung", f.AdditionalPayments),
                ("Cashback", f.Cashback), ("Ròng theo báo cáo", net - cost)
            };
            foreach (var item in items)
            {
                var box = CardPanel(); box.Dock = DockStyle.Fill; box.Margin = new Padding(5); box.Padding = new Padding(12);
                box.Controls.Add(new Label { Text = item.Item1, AutoSize = true, ForeColor = C.Muted, Left = 12, Top = 12, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
                box.Controls.Add(new Label { Text = $"{item.Item2:N2} ₽", AutoSize = true, ForeColor = item.Item2 < 0 ? C.Danger : Color.White, Left = 12, Top = 42, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
                cards.Controls.Add(box);
            }
            status.Text = $"Kỳ {f.From} → {f.To} · {f.ReportCount} báo cáo · tiền tệ {f.Currency}";
            status.ForeColor = C.Green;
        }

        async Task LoadFinance()
        {
            if (store.Marketplace != Marketplace.Wildberries)
            {
                status.Text = "Tài chính trực tiếp hiện hỗ trợ Wildberries; dữ liệu cũ vẫn được giữ nguyên.";
                status.ForeColor = C.Orange;
                return;
            }
            refresh.Enabled = false;
            status.Text = "Đang đọc báo cáo quyết toán...";
            var r = await app.ReadFinanceAsync(store, from.Value.Date, to.Value.Date);
            refresh.Enabled = true;
            if (r.Ok && r.Snapshot is not null) Render(r.Snapshot);
            else { status.Text = r.Message; status.ForeColor = C.Danger; }
            FillRuns();
        }

        void SetDays(int days)
        {
            from.Value = DateTime.Today.AddDays(-(days - 1));
            to.Value = DateTime.Today;
            StyleTab(b7, days == 7); StyleTab(b30, days == 30); StyleTab(b45, days == 45);
        }

        b7.Click += (_, _) => SetDays(7);
        b30.Click += (_, _) => SetDays(30);
        b45.Click += (_, _) => SetDays(45);
        refresh.Click += async (_, _) => await LoadFinance();

        work.Resize += (_, _) =>
        {
            refresh.Left = work.ClientSize.Width - refresh.Width - 30;
            cards.Width = work.ClientSize.Width - 55;
            tableCard.Width = work.ClientSize.Width - 55;
            tableCard.Height = Math.Max(220, work.ClientSize.Height - 405);
        };
        FillRuns();
    }

    private void ShowMarketplaceSync(Marketplace marketplace)
    {
        ClearWork();
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
                state.Text = $"Đang đồng bộ {display}: {shop.Name}...";
                state.ForeColor = C.Muted;
                var products = await app.SyncProductsAsync(shop);
                var orders = await app.SyncOrdersAsync(shop);
                var ok = products.Ok && orders.Ok;
                var extra = "";
                if (marketplace == Marketplace.Ozon)
                {
                    var fbo = await app.SyncFboSuppliesAsync(shop);
                    ok &= fbo.Ok;
                    extra = $" · {fbo.Message}";
                }
                messages.Add($"{shop.Name}: {(ok ? "OK" : "LỖI")} · {products.Message} · {orders.Message}{extra}");
            }
            run.Enabled = true;
            state.Text = $"Đã hoàn tất đồng bộ {display}.";
            state.ForeColor = C.Green;
            LoadRows();
            ShowInfo(string.Join(Environment.NewLine + Environment.NewLine, messages));
        };

        work.Resize += (_, _) =>
        {
            run.Left = work.ClientSize.Width - run.Width - 30;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(280, work.ClientSize.Height - 120);
        };
        LoadRows();
    }

    private void ShowFbs()
    {
        ClearWork();
        work.Controls.Add(Title("Đóng hàng FBS"));

        var update = ActionButton("↻ Cập nhật đơn hàng", 220);
        update.Left = 235; update.Top = 0;
        update.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null) return;
            update.Enabled = false;
            var result = await app.SyncOrdersAsync(store);
            update.Enabled = true;
            statusLabel.Text = result.Ok ? "• Đang hoạt động" : "• Có lỗi đồng bộ";
            statusLabel.ForeColor = result.Ok ? C.Green : C.Danger;
            ShowFbs();
        };
        work.Controls.Add(update);

        var tabHost = CardPanel();
        tabHost.Left = 4; tabHost.Top = 66; tabHost.Height = 72;
        tabHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        tabHost.Width = work.ClientSize.Width - 55;
        work.Controls.Add(tabHost);

        var newTab = TabButton("Đơn mới", true, 112); newTab.Left = 0; newTab.Top = 0; newTab.Height = 56;
        var packTab = TabButton("Đang đóng gói", false, 165); packTab.Left = 118; packTab.Top = 0; packTab.Height = 56;
        var shipTab = TabButton("Đang giao", false, 130); shipTab.Left = 289; shipTab.Top = 0; shipTab.Height = 56;
        tabHost.Controls.Add(newTab); tabHost.Controls.Add(packTab); tabHost.Controls.Add(shipTab);

        var search = DarkText("Tìm theo đơn hàng, sản phẩm, article...", 400);
        search.Left = 4; search.Top = 145; work.Controls.Add(search);
        var category = DarkCombo(250); category.Left = 414; category.Top = 145;
        category.Items.AddRange(new object[] { "Tất cả danh mục", "Có KIZ", "Không KIZ" });
        category.SelectedIndex = 0; work.Controls.Add(category);
        var clear = IconButton("×"); clear.Left = 674; clear.Top = 146; clear.Width = 44;
        clear.Click += (_, _) => { search.Clear(); category.SelectedIndex = 0; };
        work.Controls.Add(clear);

        var createShipment = ActionButton("Tạo shipment", 160, true);
        createShipment.Top = 145; createShipment.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(createShipment);
        var printLabels = ActionButton("In nhãn đã chọn", 155);
        printLabels.Top = 145; printLabels.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(printLabels);

        var selectAll = new CheckBox
        {
            Text = "Chọn tất cả đơn mới đang hiển thị",
            Left = 4, Top = 187, AutoSize = true,
            ForeColor = Color.White, BackColor = C.Main,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        work.Controls.Add(selectAll);
        var changingSelectAll = false;

        var card = CardPanel();
        card.Left = 4; card.Top = 225;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 255;
        work.Controls.Add(card);

        var grid = FbsGrid(); grid.Dock = DockStyle.Fill; card.Controls.Add(grid);
        string mode = "new";

        void SetMode(string value)
        {
            mode = value;
            StyleTab(newTab, value == "new");
            StyleTab(packTab, value == "pack");
            StyleTab(shipTab, value == "ship");
            createShipment.Visible = value == "new";
            selectAll.Visible = value == "new";
            changingSelectAll = true;
            selectAll.Checked = false;
            changingSelectAll = false;
            LoadRows();
        }

        void LoadRows()
        {
            grid.Rows.Clear();
            var store = CurrentStore(); if (store is null) return;
            var q = search.Text.Trim();

            foreach (var order in app.Db.Orders(store.Id))
            {
                if (mode == "new" && !IsNew(order.Status)) continue;
                if (mode == "pack" && !IsPacking(order.Status)) continue;
                if (mode == "ship" && !IsShipping(order.Status)) continue;
                if (!string.IsNullOrWhiteSpace(q) &&
                    !order.ExternalOrderId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !order.Sku.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !order.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (category.SelectedIndex == 1 && !order.NeedsKiz) continue;
                if (category.SelectedIndex == 2 && order.NeedsKiz) continue;

                var product = FindProductForOrder(store, order);
                var title = string.IsNullOrWhiteSpace(order.Name) ? product?.Name ?? order.Sku : order.Name;
                var orderText = $"{order.ExternalOrderId}\n{FormatOrderTime(order)}";
                var productText = $"{title}\n{BuildProductMetaLine(product, order)}";
                var price = product?.Price is null ? "" : $"{product.Price:0.##} ₽";
                var row = grid.Rows.Add(false, orderText, null, productText, price);
                grid.Rows[row].Tag = order;
                grid.Rows[row].Height = 98;
                var image = ProductImageUrl(product);
                if (!string.IsNullOrWhiteSpace(image)) _ = LoadImageAsync(grid, row, 2, image);
            }
        }

        selectAll.CheckedChanged += (_, _) =>
        {
            if (changingSelectAll || mode != "new") return;
            foreach (DataGridViewRow row in grid.Rows)
                if (!row.IsNewRow) row.Cells[0].Value = selectAll.Checked;
            grid.EndEdit();
        };

        newTab.Click += (_, _) => SetMode("new");
        packTab.Click += (_, _) => SetMode("pack");
        shipTab.Click += (_, _) => SetMode("ship");
        search.TextChanged += (_, _) => LoadRows();
        category.SelectedIndexChanged += (_, _) => LoadRows();

        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex != 0 && grid.Rows[e.RowIndex].Tag is FbsOrderRow order)
                ShowFbsSupplyDetail(order);
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is FbsOrderRow order)
            {
                e.SuppressKeyPress = true;
                ShowFbsSupplyDetail(order);
            }
        };

        createShipment.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null) return;
            var selected = CheckedOrders(grid);
            if (selected.Count == 0) { ShowInfo("Hãy đánh dấu ít nhất một đơn mới."); return; }

            createShipment.Enabled = false;
            var result = await CreateShipmentWithKizAsync(store, selected);
            createShipment.Enabled = true;
            ShowInfo(result.Message);
            if (result.Success)
            {
                await app.SyncOrdersAsync(store);
                SetMode("pack");
            }
        };

        printLabels.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null) return;
            var selected = CheckedOrders(grid);
            if (selected.Count == 0 && grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is FbsOrderRow focused)
                selected.Add(focused);
            if (selected.Count == 0) { ShowInfo("Hãy chọn đơn cần in nhãn."); return; }

            printLabels.Enabled = false;
            var errors = new List<string>();
            var printed = 0;
            foreach (var order in selected)
            {
                var label = await app.Api.DownloadLabelAsync(store, order.ExternalOrderId);
                app.Db.Audit("In nhãn", label.Success ? "Đã tải" : "Lỗi", $"{order.ExternalOrderId}:{label.Message}");
                if (!label.Success || string.IsNullOrWhiteSpace(label.FilePath))
                {
                    errors.Add($"{order.ExternalOrderId}: {label.Message}");
                    continue;
                }
                if (PrintLabelFile(label.FilePath, out var printError)) printed++;
                else errors.Add($"{order.ExternalOrderId}: {printError}");
            }
            printLabels.Enabled = true;
            var summary = $"Đã gửi {printed}/{selected.Count} nhãn tới máy in.";
            ShowInfo(errors.Count == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, errors));
        };

        work.Resize += (_, _) =>
        {
            tabHost.Width = work.ClientSize.Width - 55;
            createShipment.Left = work.ClientSize.Width - createShipment.Width - 30;
            printLabels.Left = createShipment.Left - printLabels.Width - 12;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(260, work.ClientSize.Height - 255);
        };

        LoadRows();
    }

    private void ShowFbsSupplyDetail(FbsOrderRow order, string? supplyOverride = null)
    {
        ClearWork();
        var store = CurrentStore(); if (store is null) return;
        var product = FindProductForOrder(store, order);
        var meta = product is null ? new ProductMetaValue("", "", "", "", "", "", order.Sku, "") : ProductMeta(product);
        var supplyId = !string.IsNullOrWhiteSpace(supplyOverride) ? supplyOverride! : SupplyIdFrom(order);
        var assignedKiz = app.Db.Kiz().FirstOrDefault(x => x.Assigned.Equals(order.ExternalOrderId, StringComparison.OrdinalIgnoreCase));

        var back = IconButton("←"); back.Left = 0; back.Top = 0; back.Click += (_, _) => ShowFbs(); work.Controls.Add(back);
        var title = Title($"Supply {(string.IsNullOrWhiteSpace(supplyId) ? "chưa tạo" : supplyId)}");
        title.Left = 55; work.Controls.Add(title);

        var print = ActionButton("▣  In nhãn dán WB", 185, true);
        print.BackColor = C.Green; print.BorderColor = C.Green;
        print.Anchor = AnchorStyles.Top | AnchorStyles.Right; print.Top = 0;
        print.Click += async (_, _) =>
        {
            print.Enabled = false;
            var result = await app.Api.DownloadLabelAsync(store, order.ExternalOrderId);
            print.Enabled = true;
            app.Db.Audit("In nhãn", result.Success ? "Đã tải" : "Lỗi", $"{order.ExternalOrderId}:{result.Message}");
            if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath)) { ShowInfo(result.Message); return; }

            if (PrintLabelFile(result.FilePath, out var error))
                ShowInfo("Đã gửi nhãn WB tới máy in mặc định.");
            else
                ShowInfo($"Đã tải nhãn nhưng chưa in được: {error}\nFile: {result.FilePath}");
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
                ForeColor = Color.White, BackColor = C.Main,
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

        move.Enabled = true;
        move.Click += async (_, _) =>
        {
            move.Enabled = false;
            if (IsNew(order.Status))
            {
                var result = await CreateShipmentWithKizAsync(store, new[] { order });
                app.Db.Audit("FBS", result.Success ? "Tạo shipment" : "Lỗi tạo shipment", $"{order.ExternalOrderId}:{result.Message}");
                if (!result.Success) { ShowInfo(result.Message); move.Enabled = true; return; }
                await app.SyncOrdersAsync(store);
                ShowFbsSupplyDetail(order with { Status = "confirm" }, result.ExternalTaskId);
                return;
            }

            if (string.IsNullOrWhiteSpace(supplyId))
            {
                ShowInfo("Không tìm thấy supplyId. Hãy cập nhật đơn hàng trước.");
                move.Enabled = true;
                return;
            }

            var delivered = await app.Api.DeliverSupplyAsync(store, supplyId);
            app.Db.Audit("FBS", delivered.Success ? "Giao lô" : "Lỗi giao lô", $"{supplyId}:{delivered.Message}");
            ShowInfo(delivered.Message);
            if (delivered.Success)
            {
                await app.SyncOrdersAsync(store);
                ShowFbs();
            }
            else move.Enabled = true;
        };

        work.Resize += (_, _) =>
        {
            move.Left = work.ClientSize.Width - move.Width - 10;
            print.Left = move.Left - print.Width - 12;
            card.Width = work.ClientSize.Width - 30;
            card.Height = Math.Max(300, work.ClientSize.Height - 135);
        };
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
        g.Columns.Add(new DataGridViewImageColumn { Name = "image", HeaderText = "Ảnh", Width = 130, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "product", HeaderText = "Sản phẩm", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = "Giá", Width = 150, ReadOnly = true });
        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell)
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return g;
    }

    private List<FbsOrderRow> CheckedOrders(DataGridView grid)
    {
        return grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells.Count > 0 && r.Cells[0].Value is bool selected && selected)
            .Select(r => r.Tag as FbsOrderRow)
            .Where(x => x is not null)
            .Cast<FbsOrderRow>()
            .ToList();
    }

    private async Task<PriceUpdateResult> CreateShipmentWithKizAsync(
        StoreProfile store,
        IReadOnlyList<FbsOrderRow> orders)
    {
        if (orders.Count == 0) return new PriceUpdateResult(false, "Chưa chọn đơn hàng.");
        if (store.Marketplace != Marketplace.Wildberries)
            return await app.Api.CreateShipmentAsync(store, orders);

        var requirements = new List<(FbsOrderRow Order, ProductRow Product, string Gtin)>();
        foreach (var order in orders.Where(x => x.NeedsKiz))
        {
            var product = FindProductForOrder(store, order);
            if (product is null)
                return new PriceUpdateResult(false, $"Không tìm thấy sản phẩm local cho đơn {order.ExternalOrderId}.");

            var gtin = AppServices.NormalizeGtin14(ProductMeta(product).Barcode);
            if (string.IsNullOrWhiteSpace(gtin))
                return new PriceUpdateResult(false, $"Đơn {order.ExternalOrderId} cần KIZ nhưng {product.Sku} chưa có GTIN hợp lệ.");
            requirements.Add((order, product, gtin));
        }

        // Chuẩn bị đủ KIZ trước khi tạo supply để tránh lô bị dở dang vì thiếu mã.
        foreach (var group in requirements.GroupBy(x => x.Gtin))
        {
            var first = group.First();
            var ensured = await app.EnsureKizQuantityAsync(store.Id, first.Product.Sku, group.Key, group.Count());
            if (!ensured.Ok)
                return new PriceUpdateResult(false, $"Không đủ KIZ cho GTIN {group.Key}: {ensured.Message}");
        }

        // WB chỉ cho gắn SGTIN sau khi order được đưa vào supply và chuyển sang confirm.
        var shipment = await app.Api.CreateShipmentAsync(store, orders);
        if (!shipment.Success) return shipment;

        var attachErrors = new List<string>();
        foreach (var item in requirements)
        {
            var code = app.Db.FindAvailableKiz(item.Gtin);
            if (string.IsNullOrWhiteSpace(code))
            {
                attachErrors.Add($"{item.Order.ExternalOrderId}: không còn KIZ sẵn sàng cho {item.Gtin}");
                continue;
            }

            var attached = await app.Api.AttachWbSgtinAsync(store, item.Order.ExternalOrderId, code);
            if (!attached.Success)
            {
                attachErrors.Add($"{item.Order.ExternalOrderId}: {attached.Message}");
                continue;
            }
            app.Db.MarkKizAssigned(code, item.Order.ExternalOrderId);
        }

        if (attachErrors.Count > 0)
            return new PriceUpdateResult(false,
                $"{shipment.Message}\nShipment đã tạo nhưng có lỗi gắn KIZ:\n{string.Join(Environment.NewLine, attachErrors)}",
                shipment.ExternalTaskId);

        app.Db.Audit("FBS", "Tạo shipment", $"{shipment.ExternalTaskId}:{orders.Count}");
        return new PriceUpdateResult(true,
            requirements.Count == 0 ? shipment.Message : $"{shipment.Message} Đã tự động chuẩn bị và gắn {requirements.Count} KIZ.",
            shipment.ExternalTaskId);
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
        var pageLabel = new Label { AutoSize = true, ForeColor = Color.White, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, TextAlign = ContentAlignment.MiddleCenter };
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

        work.Resize += (_, _) =>
        {
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
        };

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

        work.Resize += (_, _) =>
        {
            tabs.Width = work.ClientSize.Width - 55;
            content.Width = work.ClientSize.Width - 55;
            content.Height = Math.Max(350, work.ClientSize.Height - 185);
        };
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
        host.Controls.Add(new Label { Text = "Cài đặt cơ bản", AutoSize = true, Left = 18, Top = 20, ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold) });

        var omsId = LabeledDark(host, "omsId", z.OmsId, 18, 64, 900);
        var omsConnection = LabeledDark(host, "omsConnection", z.OmsConnection, 18, 124, 900);

        host.Controls.Add(new Label { Text = "Chữ ký số", AutoSize = true, Left = 18, Top = 205, ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold) });

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

    private void ShowKizMapping()
    {
        ClearWork();
        work.Controls.Add(Title("KIZ Mapping"));

        var search = DarkText("Tìm GTIN hoặc tên", 300); search.Left = 176; search.Top = 0; work.Controls.Add(search);
        var filter = DarkCombo(120); filter.Left = 486; filter.Top = 0;
        filter.Items.AddRange(new object[] { "Lọc", "Có KIZ", "Không có KIZ", "Có lỗi" }); filter.SelectedIndex = 0; work.Controls.Add(filter);
        var refresh = IconButton("↻"); refresh.Left = 618; refresh.Top = 0; refresh.Click += (_, _) => ShowKizMapping(); work.Controls.Add(refresh);

        var card = CardPanel();
        card.Left = 4; card.Top = 70; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; work.Controls.Add(card);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "gtin", HeaderText = "GTIN", Width = 240 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Tên", Width = 280 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mapping", HeaderText = "Mapping", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kiz", HeaderText = "KIZ", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "stock", HeaderText = "Mua / lưu thư", Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "error", HeaderText = "Lỗi gần nhất", Width = 190 });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "link", HeaderText = "Thao tác", Text = "↗", UseColumnTextForButtonValue = true, Width = 52, FlatStyle = FlatStyle.Flat });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "export", HeaderText = "", Text = "⇩", UseColumnTextForButtonValue = true, Width = 52, FlatStyle = FlatStyle.Flat });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "box", HeaderText = "", Text = "▣", UseColumnTextForButtonValue = true, Width = 52, FlatStyle = FlatStyle.Flat });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "add", HeaderText = "", Text = "+", UseColumnTextForButtonValue = true, Width = 52, FlatStyle = FlatStyle.Flat });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "delete", HeaderText = "", Text = "⌫", UseColumnTextForButtonValue = true, Width = 52, FlatStyle = FlatStyle.Flat });
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        card.Controls.Add(grid);

        void Load()
        {
            grid.Rows.Clear();
            var store = CurrentStore(); if (store is null) return;
            var q = search.Text.Trim();
            var kiz = app.Db.Kiz();
            var pipelines = app.Db.ZnakPipelines(store.Id);
            var groups = app.Db.Products(store.Id)
                .Select(p => (Product: p, Meta: ProductMeta(p)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Meta.Barcode))
                .GroupBy(x => x.Meta.Barcode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key);

            foreach (var group in groups)
            {
                var first = group.First();
                var gtin = group.Key;
                var allCodes = kiz.Where(x => x.Gtin.Equals(gtin, StringComparison.OrdinalIgnoreCase)).ToList();
                var available = allCodes.Count(x => x.Status == "AVAILABLE");
                var pipe = pipelines.FirstOrDefault(x => x.Gtin.Equals(gtin, StringComparison.OrdinalIgnoreCase) || group.Any(y => y.Product.Sku.Equals(x.Sku, StringComparison.OrdinalIgnoreCase)));
                var lastError = pipe?.Stage == "ERROR" ? pipe.Detail : "";
                if (!string.IsNullOrWhiteSpace(q) && !gtin.Contains(q, StringComparison.OrdinalIgnoreCase) && !first.Product.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (filter.SelectedIndex == 1 && allCodes.Count == 0) continue;
                if (filter.SelectedIndex == 2 && allCodes.Count > 0) continue;
                if (filter.SelectedIndex == 3 && string.IsNullOrWhiteSpace(lastError)) continue;

                var mapping = !string.IsNullOrWhiteSpace(pipe?.Sku) ? "Đã ánh xạ" : "Chưa";
                var row = grid.Rows.Add(gtin, first.Product.Name, mapping, allCodes.Count, $"{available} sẵn sàng", lastError);
                grid.Rows[row].Tag = gtin;
                grid.Rows[row].Height = 66;
            }
        }

        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Rows[e.RowIndex].Tag is not string gtin) return;
            var col = grid.Columns[e.ColumnIndex].Name;
            if (col == "link")
            {
                var products = CurrentStore() is StoreProfile store
                    ? app.Db.Products(store.Id).Where(p => ProductMeta(p).Barcode == gtin).Select(p => p.Sku).ToArray()
                    : Array.Empty<string>();
                ShowInfo(products.Length == 0 ? $"GTIN {gtin} chưa có sản phẩm liên kết." : $"GTIN {gtin}\nSKU: {string.Join(", ", products)}");
            }
            else if (col == "export")
            {
                var codes = app.Db.Kiz().Where(x => x.Gtin == gtin).Select(x => x.Code).ToArray();
                if (codes.Length == 0) { ShowInfo("GTIN này chưa có mã KIZ để xuất."); return; }
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "KIZ");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{gtin}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllLines(path, codes);
                app.Db.Audit("KIZ", "Xuất file", $"{gtin}:{codes.Length}:{path}");
                ShowInfo($"Đã xuất {codes.Length} mã KIZ.\n{path}");
            }
            else if (col == "box")
            {
                var all = app.Db.Kiz().Where(x => x.Gtin == gtin).ToList();
                ShowInfo($"GTIN {gtin}: {all.Count} mã · {all.Count(x => x.Status == "AVAILABLE")} sẵn sàng · {all.Count(x => x.Status == "ASSIGNED")} đã gán.");
            }
            else if (col == "add")
            {
                var code = PromptText("Thêm mã KIZ", $"Nhập mã KIZ/DataMatrix cho GTIN {gtin}:");
                if (string.IsNullOrWhiteSpace(code)) return;
                app.Db.UpsertKiz(code.Trim(), gtin, "AVAILABLE");
                Load();
            }
            else if (col == "delete")
            {
                if (MessageBox.Show($"Xóa toàn bộ mã KIZ của GTIN {gtin}?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                app.Db.DeleteKizByGtin(gtin);
                Load();
            }
        };

        search.TextChanged += (_, _) => Load();
        filter.SelectedIndexChanged += (_, _) => Load();
        work.Resize += (_, _) =>
        {
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(300, work.ClientSize.Height - 100);
        };
        Load();
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
            Left = 22, Top = 22, AutoSize = true, ForeColor = Color.White,
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
        right.Controls.Add(new Label { Text = "Giá mới (RUB)", Left = 10, Top = 10, AutoSize = true, ForeColor = Color.White });
        var newPrice = new NumericUpDown { Left = 10, Top = 35, Width = 190, DecimalPlaces = 2, Maximum = 100000000, Minimum = 1 }; right.Controls.Add(newPrice);
        right.Controls.Add(new Label { Text = "Thay đổi %", Left = 10, Top = 82, AutoSize = true, ForeColor = Color.White });
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

        work.Resize += (_, _) =>
        {
            host.Width = work.ClientSize.Width - 55; host.Height = Math.Max(320, work.ClientSize.Height - 100);
            grid.Width = host.Width * 2 / 3; grid.Height = host.Height;
            right.Left = grid.Width + 18; right.Width = Math.Max(250, host.Width - grid.Width - 35);
        };
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
        var info = new Label { Left = 210, Top = 18, Width = preview.Width - 230, Height = 230, ForeColor = Color.White, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }; preview.Controls.Add(info);

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

        long id = 0;
        if (!newStore && CurrentStore() is StoreProfile s)
        {
            id = s.Id; market.SelectedItem = s.Marketplace; name.Text = s.Name; client.Text = s.ClientId; api.Text = s.ApiKey; business.Text = s.BusinessId; campaign.Text = s.CampaignId; token.Text = s.Token;
        }

        StoreProfile Read() => new(id, (Marketplace)market.SelectedItem!, name.Text.Trim(), client.Text.Trim(), api.Text, business.Text.Trim(), campaign.Text.Trim(), token.Text, true);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { ShowInfo("Hãy nhập tên cửa hàng."); return; }
            var saved = app.Db.SaveStore(Read()); RefreshStores(saved.Id); ShowInfo("Đã lưu cửa hàng."); ShowDashboard();
        };
        test.Click += async (_, _) =>
        {
            var r = await app.Api.TestAsync(Read()); ShowInfo(r.Message);
        };
    }

    private void ShowPrintHistory()
    {
        ClearWork();
        work.Controls.Add(Title("Lịch sử in"));
        var card = CardPanel(); card.Left = 4; card.Top = 70; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; work.Controls.Add(card);
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill; grid.Columns.Add("time", "Thời gian"); grid.Columns.Add("action", "Trạng thái"); grid.Columns.Add("detail", "Chi tiết");
        foreach (var a in app.Db.AuditRows().Where(x => x.Module.Contains("nhãn", StringComparison.OrdinalIgnoreCase) || x.Module.Contains("In", StringComparison.OrdinalIgnoreCase)))
            grid.Rows.Add(a.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), TranslateAction(a.Action), a.Detail);
        card.Controls.Add(grid);
    }

    private void ShowFboPacking()
    {
        ClearWork();
        work.Controls.Add(Title("Đóng hàng FBO"));
        var hint = new Label
        {
            Text = "Danh sách sản phẩm local để chuẩn bị barcode/KIZ cho lô FBO. Dữ liệu lấy từ catalog đã đồng bộ.",
            Left = 4, Top = 48, AutoSize = true, ForeColor = C.Muted
        };
        work.Controls.Add(hint);

        var search = DarkText("Tìm SKU hoặc tên sản phẩm...", 420); search.Left = 4; search.Top = 82; work.Controls.Add(search);
        var card = CardPanel(); card.Left = 4; card.Top = 140;
        card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 170; work.Controls.Add(card);

        var grid = DarkGrid(); grid.Dock = DockStyle.Fill; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewImageColumn { HeaderText = "Ảnh", Width = 95, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SKU", Width = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sản phẩm", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Barcode / GTIN", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "KIZ sẵn sàng", Width = 130 });
        card.Controls.Add(grid);

        void Load()
        {
            grid.Rows.Clear();
            var store = CurrentStore(); if (store is null) return;
            var q = search.Text.Trim();
            foreach (var p in app.Db.Products(store.Id))
            {
                if (!string.IsNullOrWhiteSpace(q) && !p.Sku.Contains(q, StringComparison.OrdinalIgnoreCase) && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                var meta = ProductMeta(p);
                var kizCount = string.IsNullOrWhiteSpace(meta.Barcode) ? 0 : app.Db.Kiz().Count(x => x.Gtin == meta.Barcode && x.Status == "AVAILABLE");
                var row = grid.Rows.Add(null, p.Sku, p.Name, meta.Barcode, kizCount);
                grid.Rows[row].Height = 88;
                var image = ProductImageUrl(p);
                if (!string.IsNullOrWhiteSpace(image)) _ = LoadImageAsync(grid, row, 0, image);
            }
        }
        search.TextChanged += (_, _) => Load();
        work.Resize += (_, _) => { card.Width = work.ClientSize.Width - 55; card.Height = Math.Max(280, work.ClientSize.Height - 170); };
        Load();
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
        work.Resize += (_, _) =>
        {
            refresh.Left = work.ClientSize.Width - refresh.Width - 30;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(280, work.ClientSize.Height - 165);
        };
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
        b.BorderColor = active ? C.Purple : C.Card;
        b.ForeColor = Color.White;
    }

    private TextBox LabeledDark(Control host, string label, string value, int left, int top, int width, bool password = false)
    {
        host.Controls.Add(FieldLabel(label, top, left));
        var t = DarkText("", width); t.Left = left + 230; t.Top = top - 8; t.Text = value; t.UseSystemPasswordChar = password; host.Controls.Add(t); return t;
    }

    private Label FieldLabel(string text, int top, int left = 18)
    {
        return new Label { Text = text, AutoSize = true, Left = left, Top = top, ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    }

    private async Task<byte[]> GetImageBytesAsync(string url)
    {
        if (imageCache.TryGetValue(url, out var cached)) return cached;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 MarketplaceHub");
        req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        using var res = await imageHttp.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 0) imageCache[url] = bytes;
        return bytes;
    }


    private static Bitmap DecodeProductImage(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("Không giải mã được ảnh sản phẩm.");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        using var ms = new MemoryStream(data.ToArray());
        using var temp = Image.FromStream(ms);
        return new Bitmap(temp);
    }



    private async Task LoadImageAsync(DataGridView grid, int row, int column, string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var bytes = await GetImageBytesAsync(url);
            var img = DecodeProductImage(bytes);
            if (!grid.IsDisposed && row >= 0 && row < grid.Rows.Count && column >= 0 && column < grid.Columns.Count)
            {
                var old = grid.Rows[row].Cells[column].Value as Image;
                grid.Rows[row].Cells[column].Value = img;
                old?.Dispose();
            }
            else img.Dispose();
        }
        catch { }
    }

    private ProductRow? FindProductForOrder(StoreProfile store, FbsOrderRow order)
    {
        var products = app.Db.Products(store.Id);
        var direct = products.FirstOrDefault(x => x.Sku.Equals(order.Sku, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;

        try
        {
            var raw = JsonNode.Parse(order.RawJson);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "nmId", "nmID", "product_id", "sku", "article" })
            {
                var value = raw?[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) ids.Add(value);
            }
            foreach (var value in raw?["skus"]?.AsArray() ?? new JsonArray())
                if (value is not null) ids.Add(value.ToString());

            var byId = products.FirstOrDefault(x => ids.Contains(x.ExternalId) || ids.Contains(x.Sku));
            if (byId is not null) return byId;

            return products.FirstOrDefault(p =>
            {
                var barcode = ProductMeta(p).Barcode;
                return !string.IsNullOrWhiteSpace(barcode) && ids.Contains(barcode);
            });
        }
        catch { return null; }
    }

    private string ProductImageUrl(ProductRow? product)
    {
        if (product is null) return "";
        var direct = NormalizeImageUrl(product.ImageUrl);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        try { return FirstHttpImage(JsonNode.Parse(product.RawJson)); }
        catch { return ""; }
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

    private bool PrintLabelFile(string path, out string error)
    {
        error = "";
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                error = "Nhãn PDF đã được mở bằng ứng dụng mặc định; in tự động trực tiếp áp dụng cho nhãn ảnh WB.";
                return false;
            }

            using var image = Image.FromFile(path);
            using var document = new PrintDocument();
            document.PrintController = new StandardPrintController();
            document.DocumentName = "Marketplace Hub - WB sticker";
            document.DefaultPageSettings.PaperSize = new PaperSize("58x40mm", 228, 157);
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.PrintPage += (_, e) =>
            {
                e.Graphics.DrawImage(image, e.PageBounds);
                e.HasMorePages = false;
            };
            document.Print();
            app.Db.Audit("In nhãn", "Đã in", path);
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
            BackColor = C.Main, ForeColor = Color.White, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        dialog.Controls.Add(new Label { Text = prompt, Left = 18, Top = 18, Width = 580, Height = 42, ForeColor = C.Muted });
        var box = new TextBox { Left = 18, Top = 70, Width = 585, BackColor = C.Card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
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
            ForeColor = Color.White,
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
            BackColor = C.Card, ForeColor = Color.White,
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
        status.Contains("new", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("awaiting_packaging", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("STARTED", StringComparison.OrdinalIgnoreCase);

    private static bool IsPacking(string status) =>
        status.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("assembling", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("PROCESSING", StringComparison.OrdinalIgnoreCase);

    private static bool IsShipping(string status) =>
        status.Contains("awaiting_deliver", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("READY_TO_SHIP", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("deliver", StringComparison.OrdinalIgnoreCase);

    private static string TranslateKizStatus(string s) => s switch
    {
        "AVAILABLE" => "Sẵn sàng", "RESERVED" => "Đã giữ", "ASSIGNED" => "Đã gán",
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
    public static readonly Color Side = Color.FromArgb(30, 41, 59);
    public static readonly Color Card = Color.FromArgb(30, 41, 59);
    public static readonly Color RowAlt = Color.FromArgb(24, 34, 53);
    public static readonly Color Border = Color.FromArgb(51, 65, 85);
    public static readonly Color Purple = Color.FromArgb(154, 65, 254);
    public static readonly Color Orange = Color.FromArgb(245, 158, 11);
    public static readonly Color Green = Color.FromArgb(16, 185, 129);
    public static readonly Color Danger = Color.FromArgb(239, 68, 68);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
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
