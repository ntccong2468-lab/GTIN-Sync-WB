using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed class MainForm : Form
{
    private readonly AppServices app;
    private readonly Panel work = new() { Dock = DockStyle.Fill, BackColor = C.Main };
    private readonly ComboBox storePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 275 };
    private readonly Label statusLabel = new() { AutoSize = true, ForeColor = C.Green, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly RoundedButton syncButton = new() { Text = "↻  Đồng bộ", Width = 175, Height = 44 };
    private readonly HttpClient imageHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

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
            Left = 13, Top = 225, Width = 244, Height = 465,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = C.Side
        };
        p.Controls.Add(menu);

        AddSide(menu, "▦  Dashboard", ShowDashboard);
        AddSide(menu, "▱  Đóng hàng FBS", ShowFbs);
        AddSide(menu, "◇  Đóng hàng FBO", ShowFboPacking);
        AddSide(menu, "☷  Đơn hàng FBO", ShowFboOrders);
        AddSide(menu, "⌁  KIZ Mapping", ShowKizMapping);
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
        help.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/ntccong2468-lab/GTIN-Sync-WB") { UseShellExecute = true });
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
            Text = text, Width = width, Height = 45, Radius = 10,
            BackColor = bg, ForeColor = Color.White, BorderColor = bg,
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand
        };
    }

    private RoundedButton DarkOutline(string text, int width)
    {
        return new RoundedButton
        {
            Text = text, Width = width, Height = 42, Radius = 9,
            BackColor = C.Side, ForeColor = Color.White, BorderColor = C.Border,
            Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand
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
            Text = text, Width = width, Height = 42, Radius = 9,
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
        var s = CurrentStore();
        syncButton.Text = s?.Marketplace == Marketplace.Wildberries ? "↻  Đồng bộ WB" : "↻  Đồng bộ";
    }

    private async Task SyncAllForCurrentStore()
    {
        var s = CurrentStore();
        if (s is null) { ShowInfo("Chưa có cửa hàng. Hãy thêm cửa hàng trước."); return; }

        syncButton.Enabled = false;
        statusLabel.Text = "• Đang đồng bộ...";
        var p = await app.SyncProductsAsync(s);
        var o = await app.SyncOrdersAsync(s);
        syncButton.Enabled = true;
        statusLabel.Text = p.Ok && o.Ok ? "• Đang hoạt động" : "• Có lỗi đồng bộ";
        statusLabel.ForeColor = p.Ok && o.Ok ? C.Green : Color.OrangeRed;
        ShowInfo(p.Message + Environment.NewLine + o.Message);
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
        return new ComboBox
        {
            Width = width, Height = 44, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = C.Card, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10)
        };
    }

    private void ShowDashboard()
    {
        ClearWork();
        var title = Title("Dashboard");
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

    private void ShowFbs()
    {
        ClearWork();
        work.Controls.Add(Title("Đóng hàng FBS"));

        var update = ActionButton("↻ Cập nhật đơn hàng", 220);
        update.Left = 235; update.Top = 0;
        update.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            update.Enabled = false;
            var r = await app.SyncOrdersAsync(s);
            update.Enabled = true;
            ShowInfo(r.Message);
            ShowFbs();
        };
        work.Controls.Add(update);

        var tabHost = CardPanel();
        tabHost.Left = 4; tabHost.Top = 66; tabHost.Height = 72; tabHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        tabHost.Width = work.ClientSize.Width - 55;
        work.Controls.Add(tabHost);

        var newTab = TabButton("Đơn mới", true, 112); newTab.Left = 10; newTab.Top = 10;
        var packTab = TabButton("Đang đóng gói", false, 165); packTab.Left = 128; packTab.Top = 10;
        var shipTab = TabButton("Đang giao", false, 125); shipTab.Left = 299; shipTab.Top = 10;
        tabHost.Controls.Add(newTab); tabHost.Controls.Add(packTab); tabHost.Controls.Add(shipTab);

        var search = DarkText("Tìm theo đơn hàng, sản phẩm, article...", 400);
        search.Left = 4; search.Top = 145; work.Controls.Add(search);
        var category = DarkCombo(250); category.Left = 414; category.Top = 145; category.Items.AddRange(new object[] { "Tất cả danh mục", "Có KIZ", "Không KIZ" }); category.SelectedIndex = 0; work.Controls.Add(category);

        var packSelected = ActionButton("Đóng hàng", 125, true); packSelected.Top = 145; packSelected.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(packSelected);
        var labelSelected = ActionButton("Tải nhãn", 120); labelSelected.Top = 145; labelSelected.Anchor = AnchorStyles.Top | AnchorStyles.Right; work.Controls.Add(labelSelected);

        var card = CardPanel();
        card.Left = 4; card.Top = 205; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 235; work.Controls.Add(card);

        var grid = FbsGrid(); grid.Dock = DockStyle.Fill; card.Controls.Add(grid);
        string mode = "new";

        void SetMode(string m)
        {
            mode = m;
            StyleTab(newTab, m == "new"); StyleTab(packTab, m == "pack"); StyleTab(shipTab, m == "ship");
            LoadRows();
        }

        async void LoadRows()
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            var products = app.Db.Products(s.Id).ToDictionary(x => x.Sku, StringComparer.OrdinalIgnoreCase);
            var q = search.Text.Trim();
            foreach (var o in app.Db.Orders(s.Id))
            {
                if (mode == "new" && !IsNew(o.Status)) continue;
                if (mode == "pack" && !IsPacking(o.Status)) continue;
                if (mode == "ship" && !IsShipping(o.Status)) continue;
                if (!string.IsNullOrWhiteSpace(q) && !o.ExternalOrderId.Contains(q, StringComparison.OrdinalIgnoreCase) && !o.Sku.Contains(q, StringComparison.OrdinalIgnoreCase) && !o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (category.SelectedIndex == 1 && !o.NeedsKiz) continue;
                if (category.SelectedIndex == 2 && o.NeedsKiz) continue;

                products.TryGetValue(o.Sku, out var product);
                var row = grid.Rows.Add(false, o.ExternalOrderId, null, string.IsNullOrWhiteSpace(o.Name) ? product?.Name ?? o.Sku : o.Name, product?.Price is null ? "" : $"{product.Price:0.##} ₽");
                grid.Rows[row].Tag = o;
                grid.Rows[row].Height = 98;
                if (!string.IsNullOrWhiteSpace(product?.ImageUrl)) _ = LoadImageAsync(grid, row, 2, product.ImageUrl);
            }
        }

        newTab.Click += (_, _) => SetMode("new");
        packTab.Click += (_, _) => SetMode("pack");
        shipTab.Click += (_, _) => SetMode("ship");
        search.TextChanged += (_, _) => LoadRows();
        category.SelectedIndexChanged += (_, _) => LoadRows();

        packSelected.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            var order = CheckedOrder(grid); if (order is null) { ShowInfo("Hãy chọn một đơn hàng."); return; }
            if (order.NeedsKiz && app.Db.Kiz().All(x => x.Status != "AVAILABLE")) { ShowInfo("Đơn hàng cần KIZ nhưng không có mã KIZ sẵn sàng."); return; }
            if (MessageBox.Show($"Xác nhận đóng đơn {order.ExternalOrderId}?", "Đóng hàng FBS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var r = await app.Api.PackOrderAsync(s, order);
            app.Db.Audit("FBS", r.Success ? "Đóng hàng" : "Lỗi đóng hàng", $"{order.ExternalOrderId}:{r.Message}");
            ShowInfo(r.Message);
            if (r.Success) await app.SyncOrdersAsync(s);
            LoadRows();
        };

        labelSelected.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            var order = CheckedOrder(grid); if (order is null) { ShowInfo("Hãy chọn một đơn hàng."); return; }
            var r = await app.Api.DownloadLabelAsync(s, order.ExternalOrderId);
            app.Db.Audit("In nhãn", r.Success ? "Đã tải" : "Lỗi", $"{order.ExternalOrderId}:{r.Message}");
            if (r.Success && r.FilePath is not null)
            {
                if (MessageBox.Show("Đã tải nhãn. Mở thư mục?", "Nhãn FBS", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.FilePath}\"") { UseShellExecute = true });
            }
            else ShowInfo(r.Message);
        };

        work.Resize += (_, _) =>
        {
            tabHost.Width = work.ClientSize.Width - 55;
            labelSelected.Left = work.ClientSize.Width - labelSelected.Width - 30;
            packSelected.Left = labelSelected.Left - packSelected.Width - 12;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(260, work.ClientSize.Height - 235);
        };

        LoadRows();
    }

    private DataGridView FbsGrid()
    {
        var g = DarkGrid();
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "check", HeaderText = "", Width = 55 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "order", HeaderText = "Order ID", Width = 235 });
        g.Columns.Add(new DataGridViewImageColumn { Name = "image", HeaderText = "Ảnh", Width = 130, ImageLayout = DataGridViewImageCellLayout.Zoom });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "product", HeaderText = "Sản phẩm", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = "Giá", Width = 150 });
        return g;
    }

    private FbsOrderRow? CheckedOrder(DataGridView grid)
    {
        foreach (DataGridViewRow r in grid.Rows)
            if (r.Cells[0].Value is bool b && b && r.Tag is FbsOrderRow o) return o;
        return grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as FbsOrderRow : null;
    }

    private void ShowZnakRegistration()
    {
        ClearWork();
        work.Controls.Add(Title("Đăng ký thẻ Znack cho WB"));

        var docs = ActionButton("Cấu hình giấy tờ", 175); docs.Anchor = AnchorStyles.Top | AnchorStyles.Right; docs.Top = 0; docs.Click += (_, _) => ShowZnakSettings(); work.Controls.Add(docs);
        var sync = ActionButton("Đồng bộ Znack", 165); sync.Anchor = AnchorStyles.Top | AnchorStyles.Right; sync.Top = 0; sync.Click += (_, _) => ShowInfo("Đã tải danh sách sản phẩm từ dữ liệu đồng bộ cục bộ."); work.Controls.Add(sync);

        var search = DarkText("Tìm tên, article, barcode hoặc nmID...", 380); search.Left = 4; search.Top = 66; work.Controls.Add(search);
        var category = DarkCombo(160); category.Left = 394; category.Top = 66; category.Items.AddRange(new object[] { "Danh mục", "Tất cả" }); category.SelectedIndex = 0; work.Controls.Add(category);
        var status = DarkCombo(225); status.Left = 564; status.Top = 66; status.Items.AddRange(new object[] { "Tất cả trạng thái", "Chưa tạo", "Đã xếp hàng" }); status.SelectedIndex = 0; work.Controls.Add(status);
        var clear = ActionButton("Xóa bộ lọc", 125); clear.Left = 799; clear.Top = 66; clear.Click += (_, _) => { search.Clear(); status.SelectedIndex = 0; }; work.Controls.Add(clear);

        var card = CardPanel();
        card.Left = 4; card.Top = 138; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 210; work.Controls.Add(card);

        var grid = ZnakGrid(); grid.Dock = DockStyle.Fill; card.Controls.Add(grid);
        var footer = new Label { Text = "", AutoSize = true, Left = 4, Top = work.ClientSize.Height - 55, ForeColor = C.Muted, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        work.Controls.Add(footer);

        async void LoadRows()
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            var products = app.Db.Products(s.Id);
            var q = search.Text.Trim();
            foreach (var p in products)
            {
                if (!string.IsNullOrWhiteSpace(q) && !p.Sku.Contains(q, StringComparison.OrdinalIgnoreCase) && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !p.ExternalId.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                var meta = ProductMeta(p);
                var row = grid.Rows.Add(false, null, p.Name, meta.Gender, meta.Color, meta.Size, meta.Barcode, "Chưa tạo", "Tạo...");
                grid.Rows[row].Tag = p;
                grid.Rows[row].Height = 104;
                if (!string.IsNullOrWhiteSpace(p.ImageUrl)) _ = LoadImageAsync(grid, row, 1, p.ImageUrl);
            }
            footer.Text = $"Sản phẩm: {grid.Rows.Count}  •  Chưa tạo: {grid.Rows.Count}";
        }

        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 8) return;
            if (grid.Rows[e.RowIndex].Tag is not ProductRow p) return;
            var z = app.Db.GetZnakConfig();
            if (!z.Enabled) { ShowInfo("Hãy bật và lưu Cấu hình Znack trước."); return; }
            app.Db.Audit("Đăng ký Znack", "Đã xếp hàng", $"{p.Sku}:{ProductMeta(p).Barcode}");
            grid.Rows[e.RowIndex].Cells[7].Value = "Đã xếp hàng";
            ShowInfo($"Đã xếp hàng đăng ký cho SKU {p.Sku}. Việc gửi thật cần True API/CryptoPro trên máy seller.");
        };

        search.TextChanged += (_, _) => LoadRows();
        status.SelectedIndexChanged += (_, _) => LoadRows();

        work.Resize += (_, _) =>
        {
            sync.Left = work.ClientSize.Width - sync.Width - 30;
            docs.Left = sync.Left - docs.Width - 12;
            card.Width = work.ClientSize.Width - 55;
            card.Height = Math.Max(300, work.ClientSize.Height - 210);
            footer.Top = work.ClientSize.Height - 55;
        };

        LoadRows();
    }

    private DataGridView ZnakGrid()
    {
        var g = DarkGrid();
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.Columns.Add(new DataGridViewCheckBoxColumn { Width = 55, HeaderText = "" });
        g.Columns.Add(new DataGridViewImageColumn { Width = 85, HeaderText = "Ảnh", ImageLayout = DataGridViewImageCellLayout.Zoom });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 340, HeaderText = "Tên" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 115, HeaderText = "Giới tính" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 115, HeaderText = "Màu" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 80, HeaderText = "Size" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 190, HeaderText = "Barcode WB / GTIN" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Width = 135, HeaderText = "Trạng thái" });
        g.Columns.Add(new DataGridViewButtonColumn { Width = 110, HeaderText = "Thao tác", FlatStyle = FlatStyle.Flat });
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

        var omsId = LabeledDark(host, "omsId", z.OmsId, 18, 64, 960);
        var omsConnection = LabeledDark(host, "omsConnection", z.OmsConnection, 18, 124, 960);

        var sig = CardPanel(); sig.Left = 18; sig.Top = 200; sig.Width = host.Width - 45; sig.Height = 235; sig.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; host.Controls.Add(sig);
        sig.Controls.Add(new Label { Text = "Chữ ký số", AutoSize = true, Left = 16, Top = 16, ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold) });

        var certs = AppServices.Certificates().Where(x => x.HasPrivateKey).ToList();
        var certBox = DarkCombo(Math.Max(500, sig.Width - 45)); certBox.Left = 16; certBox.Top = 58; certBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        foreach (var c in certs) certBox.Items.Add(new CertificateItem(c.Subject, c.Thumbprint, c.NotAfter, c.HasPrivateKey));
        certBox.DisplayMember = nameof(CertificateItem.Display);
        var selected = certBox.Items.Cast<CertificateItem>().ToList().FindIndex(x => x.Thumbprint.Equals(z.CertificateThumbprint, StringComparison.OrdinalIgnoreCase));
        if (selected >= 0) certBox.SelectedIndex = selected; else if (certBox.Items.Count > 0) certBox.SelectedIndex = 0;
        sig.Controls.Add(certBox);

        var check = ActionButton("Kiểm tra chữ ký", 168, true); check.Left = 16; check.Top = 126; sig.Controls.Add(check);
        var verified = new Label { Text = "", AutoSize = true, Left = 16, Top = 184, ForeColor = C.Muted }; sig.Controls.Add(verified);
        check.Click += (_, _) =>
        {
            if (certBox.SelectedItem is not CertificateItem c) { verified.Text = "CHƯA CÓ CHỨNG THƯ"; verified.ForeColor = Color.OrangeRed; return; }
            verified.Text = c.NotAfter > DateTime.Now ? "ĐÃ XÁC MINH" : "CHỨNG THƯ HẾT HẠN";
            verified.ForeColor = c.NotAfter > DateTime.Now ? C.Green : Color.OrangeRed;
        };

        var auto = new CheckBox { Text = "Tự động đưa vào lưu thông sau khi tải mã", Left = 18, Top = 465, Width = 390, ForeColor = Color.White, BackColor = C.Card, Checked = z.AutoCirculation, Font = new Font("Segoe UI", 10) };
        host.Controls.Add(auto);

        var save = ActionButton("Lưu cài đặt", 130, true); save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; host.Controls.Add(save);
        host.Resize += (_, _) => save.Location = new Point(host.ClientSize.Width - save.Width - 20, host.ClientSize.Height - save.Height - 18);
        save.Location = new Point(host.Width - save.Width - 20, host.Height - save.Height - 18);
        save.Click += (_, _) =>
        {
            var c = certBox.SelectedItem as CertificateItem;
            app.Db.SaveZnakConfig(new ZnakConfig(
                z.Inn, z.Environment, c?.Thumbprint ?? "", c?.Subject ?? "",
                z.AutoSignMode, true, omsId.Text.Trim(), omsConnection.Text.Trim(), auto.Checked));
            ShowInfo("Đã lưu cấu hình Znack.");
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
        var card = CardPanel(); card.Left = 4; card.Top = 70; card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; work.Controls.Add(card);
        var grid = DarkGrid(); grid.Dock = DockStyle.Fill;
        grid.Columns.Add("gtin", "GTIN");
        grid.Columns.Add("status", "Trạng thái");
        grid.Columns.Add("order", "Đơn hàng");
        grid.Columns.Add("code", "Mã KIZ");
        foreach (var k in app.Db.Kiz()) grid.Rows.Add(k.Gtin, TranslateKizStatus(k.Status), k.Assigned, k.Code);
        card.Controls.Add(grid);
        work.Resize += (_, _) => { card.Width = work.ClientSize.Width - 55; card.Height = work.ClientSize.Height - 100; };
    }

    private void ShowDesignTools()
    {
        ClearWork();
        work.Controls.Add(Title("Thiết kế mẫu"));

        var tabs = CardPanel(); tabs.Left = 4; tabs.Top = 70; tabs.Height = 68; tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; tabs.Width = work.ClientSize.Width - 55; work.Controls.Add(tabs);
        var price = TabButton("Thay đổi giá", true, 145); price.Left = 10; price.Top = 10; tabs.Controls.Add(price);
        var copy = TabButton("Sao chép bài đăng", false, 190); copy.Left = 165; copy.Top = 10; tabs.Controls.Add(copy);

        var host = CardPanel(); host.Left = 4; host.Top = 140; host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; host.Width = work.ClientSize.Width - 55; host.Height = work.ClientSize.Height - 170; work.Controls.Add(host);

        void RenderPrice()
        {
            StyleTab(price, true); StyleTab(copy, false); host.Controls.Clear();
            var grid = DarkGrid(); grid.Left = 0; grid.Top = 0; grid.Width = host.Width * 2 / 3; grid.Height = host.Height; grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            grid.Columns.Add("sku", "SKU"); grid.Columns.Add("name", "Tên sản phẩm"); grid.Columns.Add("price", "Giá hiện tại");
            var s = CurrentStore(); if (s != null) foreach (var p in app.Db.Products(s.Id)) grid.Rows.Add(p.Sku, p.Name, p.Price?.ToString("0.##") ?? "");
            host.Controls.Add(grid);

            var right = new Panel { Left = grid.Width + 18, Top = 20, Width = host.Width - grid.Width - 35, Height = 350, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = C.Card }; host.Controls.Add(right);
            right.Controls.Add(new Label { Text = "Giá mới (RUB)", Left = 10, Top = 10, AutoSize = true, ForeColor = Color.White });
            var newPrice = new NumericUpDown { Left = 10, Top = 35, Width = 180, DecimalPlaces = 2, Maximum = 100000000, Minimum = 1 }; right.Controls.Add(newPrice);
            right.Controls.Add(new Label { Text = "Thay đổi %", Left = 10, Top = 80, AutoSize = true, ForeColor = Color.White });
            var percent = new NumericUpDown { Left = 10, Top = 105, Width = 180, DecimalPlaces = 2, Minimum = -99, Maximum = 500, Value = 10 }; right.Controls.Add(percent);
            var calc = ActionButton("Tính theo %", 135); calc.Left = 10; calc.Top = 150; right.Controls.Add(calc);
            var apply = ActionButton("Cập nhật giá", 145, true); apply.Left = 10; apply.Top = 205; right.Controls.Add(apply);

            calc.Click += (_, _) =>
            {
                if (grid.SelectedRows.Count == 0) return;
                if (decimal.TryParse(grid.SelectedRows[0].Cells[2].Value?.ToString(), out var p))
                    newPrice.Value = Math.Min(newPrice.Maximum, Math.Max(1, Math.Round(p * (1 + percent.Value / 100m), 2)));
            };
            apply.Click += async (_, _) =>
            {
                var store = CurrentStore(); if (store is null || grid.SelectedRows.Count == 0) return;
                var sku = grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
                var product = app.Db.Product(store.Id, sku); if (product is null) return;
                if (MessageBox.Show($"Cập nhật {sku} thành {newPrice.Value:0.##} RUB?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var r = await app.ChangePriceAsync(store, product, newPrice.Value);
                ShowInfo(r.Message);
            };
        }

        void RenderCopy()
        {
            StyleTab(price, false); StyleTab(copy, true); host.Controls.Clear();
            var stores = app.Db.Stores().ToList();
            host.Controls.Add(FieldLabel("Cửa hàng nguồn", 22, 22));
            var src = DarkCombo(300); src.Left = 22; src.Top = 48; src.DataSource = stores.ToList(); src.DisplayMember = nameof(StoreProfile.Name); host.Controls.Add(src);
            host.Controls.Add(FieldLabel("Cửa hàng đích", 104, 22));
            var dst = DarkCombo(300); dst.Left = 22; dst.Top = 130; dst.DataSource = stores.ToList(); dst.DisplayMember = nameof(StoreProfile.Name); host.Controls.Add(dst);
            host.Controls.Add(FieldLabel("SKU nguồn", 186, 22));
            var sourceSku = DarkText("SKU nguồn", 300); sourceSku.Left = 22; sourceSku.Top = 212; host.Controls.Add(sourceSku);
            host.Controls.Add(FieldLabel("SKU đích", 268, 22));
            var destSku = DarkText("SKU đích", 300); destSku.Left = 22; destSku.Top = 294; host.Controls.Add(destSku);
            var run = ActionButton("Sao chép bài đăng", 180, true); run.Left = 22; run.Top = 360; host.Controls.Add(run);
            var note = new Label { Text = "Ảnh sản phẩm được sao chép và xác minh sau khi card đích được tạo.", Left = 220, Top = 372, AutoSize = true, ForeColor = C.Green }; host.Controls.Add(note);
            run.Click += async (_, _) =>
            {
                if (src.SelectedItem is not StoreProfile a || dst.SelectedItem is not StoreProfile b) return;
                var p = app.Db.Product(a.Id, sourceSku.Text.Trim()); if (p is null) { ShowInfo("Không tìm thấy SKU nguồn."); return; }
                if (string.IsNullOrWhiteSpace(destSku.Text)) { ShowInfo("Hãy nhập SKU đích."); return; }
                var r = await app.Api.CopySameMarketplaceAsync(a, b, p, destSku.Text.Trim());
                app.Db.Audit("Sao chép", r.Success ? "Đã gửi" : "Lỗi", r.Message);
                ShowInfo(r.Message);
            };
        }

        price.Click += (_, _) => RenderPrice();
        copy.Click += (_, _) => RenderCopy();
        work.Resize += (_, _) => { tabs.Width = work.ClientSize.Width - 55; host.Width = work.ClientSize.Width - 55; host.Height = Math.Max(320, work.ClientSize.Height - 170); };
        RenderPrice();
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
        ShowUnsupported("Đóng hàng FBO", "Module FBO chưa được nối API trong phiên bản này. Giao diện được giữ đúng vị trí để tương thích bố cục WCode.");
    }

    private void ShowFboOrders()
    {
        ShowUnsupported("Đơn hàng FBO", "Module danh sách FBO chưa được nối API trong phiên bản này.");
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

    private async Task LoadImageAsync(DataGridView grid, int row, int column, string url)
    {
        try
        {
            var bytes = await imageHttp.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            using var temp = Image.FromStream(ms);
            var img = new Bitmap(temp);
            if (!grid.IsDisposed && row < grid.Rows.Count) grid.Rows[row].Cells[column].Value = img;
        }
        catch { }
    }

    private ProductMetaValue ProductMeta(ProductRow p)
    {
        try
        {
            var root = JsonNode.Parse(p.RawJson);
            var size = root?["sizes"]?.AsArray()?.FirstOrDefault()?["techSize"]?.ToString() ?? "";
            var barcode = root?["sizes"]?.AsArray()?.FirstOrDefault()?["skus"]?.AsArray()?.FirstOrDefault()?.ToString() ?? "";
            string Char(params string[] names)
            {
                foreach (var c in root?["characteristics"]?.AsArray() ?? new JsonArray())
                {
                    var n = c?["name"]?.ToString() ?? "";
                    if (names.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        var v = c?["value"];
                        if (v is JsonArray a) return string.Join(", ", a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                        return v?.ToString() ?? "";
                    }
                }
                return "";
            }
            return new ProductMetaValue(Char("Пол", "gender", "Giới tính"), Char("Цвет", "color", "Màu"), size, barcode);
        }
        catch { return new ProductMetaValue("", "", "", ""); }
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
        "SHIPPED" => "Đã giao", "INVALID" => "Không hợp lệ", "RETIRED" => "Đã ngừng", _ => s
    };

    private static string TranslateModule(string s) => s switch
    {
        "Stores" => "Cửa hàng", "Products" => "Sản phẩm", "Price" => "Giá",
        "Copy" => "Sao chép", "KIZ" => "Mã KIZ", "FBS" => "Đóng hàng FBS",
        "SelfTest" => "Tự kiểm tra", _ => s
    };

    private static string TranslateAction(string s) => s switch
    {
        "Save" => "Lưu", "Sync" => "Đồng bộ", "Accepted" => "Đã tiếp nhận",
        "Failed" => "Lỗi", "Upsert" => "Cập nhật", "Database" => "Cơ sở dữ liệu", _ => s
    };

    private sealed record CertificateItem(string Subject, string Thumbprint, DateTime NotAfter, bool HasPrivateKey)
    {
        public string Display => $"{Thumbprint} / Hết hạn: {NotAfter:dd.MM.yyyy}";
    }

    private sealed record ProductMetaValue(string Gender, string Color, string Size, string Barcode);
}

internal static class C
{
    public static readonly Color Main = Color.FromArgb(15, 24, 42);
    public static readonly Color Side = Color.FromArgb(29, 41, 59);
    public static readonly Color Card = Color.FromArgb(30, 43, 62);
    public static readonly Color RowAlt = Color.FromArgb(23, 36, 55);
    public static readonly Color Border = Color.FromArgb(49, 68, 91);
    public static readonly Color Purple = Color.FromArgb(153, 58, 255);
    public static readonly Color Orange = Color.FromArgb(255, 159, 10);
    public static readonly Color Green = Color.FromArgb(0, 214, 143);
    public static readonly Color Muted = Color.FromArgb(106, 133, 168);
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
    public int Radius { get; set; } = 9;
    public Color BorderColor { get; set; } = Color.Transparent;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Resize += (_, _) => UpdateShape();
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
        using var brush = new SolidBrush(BackColor);
        pevent.Graphics.FillPath(brush, path);
        if (BorderColor != Color.Transparent)
        {
            using var pen = new Pen(BorderColor);
            pevent.Graphics.DrawPath(pen, path);
        }
        TextRenderer.DrawText(pevent.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
