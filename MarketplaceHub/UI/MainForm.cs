using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Diagnostics;

namespace MarketplaceHub.UI;

public sealed class MainForm : Form
{
    private readonly AppServices app;
    private readonly Panel work = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 248, 250), Padding = new Padding(14) };
    private readonly RichTextBox log = new()
    {
        Dock = DockStyle.Bottom, Height = 92, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(25, 28, 34), ForeColor = Color.FromArgb(210, 218, 228),
        Font = new Font("Consolas", 9), DetectUrls = false
    };
    private readonly ComboBox storePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly Label connection = new() { AutoSize = true, ForeColor = Color.FromArgb(114, 122, 132) };

    private readonly Color side = Color.FromArgb(31, 34, 39);
    private readonly Color side2 = Color.FromArgb(45, 50, 57);
    private readonly Color accent = Color.FromArgb(34, 176, 135);
    private readonly Color border = Color.FromArgb(216, 220, 226);
    private readonly Color text = Color.FromArgb(48, 55, 66);

    public MainForm(AppServices services)
    {
        app = services;
        Text = "Trung tâm Marketplace 0.4.1";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1220, 780);
        Size = new Size(1520, 930);
        Font = new Font("Segoe UI", 9);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildSidebar(), 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        right.Controls.Add(BuildTopbar(), 0, 0);
        right.Controls.Add(work, 0, 1);
        right.Controls.Add(log, 0, 2);
        root.Controls.Add(right, 1, 0);

        RefreshStores();
        ShowOverview();
        WriteLog("Ứng dụng đã khởi động. Cơ sở dữ liệu: " + app.Db.DbPath);
    }

    private Control BuildSidebar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = side };
        var logo = new Panel { BackColor = accent, Width = 40, Height = 40, Left = 16, Top = 14 };
        logo.Controls.Add(new Label
        {
            Text = "W", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Segoe UI", 19, FontStyle.Bold)
        });
        p.Controls.Add(logo);
        p.Controls.Add(new Label
        {
            Text = "MARKETPLACE HUB",
            AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            Location = new Point(67, 16)
        });
        p.Controls.Add(new Label
        {
            Text = "CÔNG CỤ SELLER",
            AutoSize = true, ForeColor = Color.FromArgb(145, 155, 168),
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Location = new Point(69, 40)
        });
        p.Controls.Add(new Label
        {
            Text = "WB  •  OZON  •  YANDEX",
            AutoSize = true, ForeColor = Color.FromArgb(145, 155, 168),
            Location = new Point(18, 70)
        });

        var menu = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Location = new Point(0, 102), Size = new Size(248, 720),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = side
        };
        p.Controls.Add(menu);

        AddSection(menu, "TỔNG QUAN");
        AddNav(menu, "Tổng quan", ShowOverview);

        AddSection(menu, "ĐÓNG HÀNG");
        AddNav(menu, "Đóng hàng FBS", ShowFbs);
        AddNav(menu, "Mã KIZ", ShowKiz);

        AddSection(menu, "SẢN PHẨM");
        AddNav(menu, "Sản phẩm", ShowProducts);
        AddNav(menu, "Giá sản phẩm", ShowPrices);
        AddNav(menu, "Sao chép bài đăng", ShowCopy);

        AddSection(menu, "ЧЕСТНЫЙ ЗНАК");
        AddNav(menu, "Đăng ký Честный ЗНАК", ShowZnakRegistration);
        AddNav(menu, "Cấu hình Честный ЗНАК", ShowZnakSettings);

        AddSection(menu, "HỆ THỐNG");
        AddNav(menu, "Cửa hàng / API", ShowStores);
        AddNav(menu, "Lịch sử", ShowHistory);
        AddNav(menu, "Cài đặt", ShowSettings);

        return p;
    }

    private Control BuildTopbar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        p.Paint += (_, e) => e.Graphics.DrawLine(new Pen(border), 0, p.Height - 1, p.Width, p.Height - 1);

        p.Controls.Add(new Label { Text = "Cửa hàng:", AutoSize = true, ForeColor = text, Location = new Point(15, 20) });
        storePicker.Location = new Point(80, 14);
        storePicker.SelectedIndexChanged += (_, _) => connection.Text = "";
        p.Controls.Add(storePicker);

        var test = Button("KIỂM TRA API", 115);
        test.Location = new Point(365, 10);
        test.Click += async (_, _) => await TestCurrentStore();
        p.Controls.Add(test);

        connection.Location = new Point(492, 20);
        p.Controls.Add(connection);

        var db = new Label
        {
            Text = "DỮ LIỆU CỤC BỘ • DPAPI",
            AutoSize = true, ForeColor = Color.FromArgb(92, 113, 138),
            Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = new Font("Segoe UI", 8, FontStyle.Bold)
        };
        p.Controls.Add(db);
        p.Resize += (_, _) => db.Location = new Point(p.ClientSize.Width - db.Width - 16, 20);
        return p;
    }

    private void AddSection(FlowLayoutPanel p, string title)
    {
        p.Controls.Add(new Label
        {
            Text = title, Width = 235, Height = 30, ForeColor = Color.FromArgb(132, 143, 157),
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(14, 0, 0, 4),
            Font = new Font("Segoe UI", 8, FontStyle.Bold), Margin = Padding.Empty
        });
    }

    private void AddNav(FlowLayoutPanel p, string caption, Action action)
    {
        var b = new Button
        {
            Text = caption, Width = 248, Height = 40, FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0),
            BackColor = side, ForeColor = Color.FromArgb(220, 225, 232),
            Margin = Padding.Empty, Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = side2;
        b.Click += (_, _) =>
        {
            foreach (var x in p.Controls.OfType<Button>()) x.BackColor = side;
            b.BackColor = accent;
            b.ForeColor = Color.White;
            action();
        };
        p.Controls.Add(b);
    }

    private Button Button(string caption, int width = 120)
    {
        var b = new Button
        {
            Text = caption, Width = width, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = text, Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = border;
        return b;
    }

    private Button Primary(string caption, int width = 130)
    {
        var b = Button(caption, width);
        b.BackColor = accent;
        b.ForeColor = Color.White;
        b.FlatAppearance.BorderColor = accent;
        return b;
    }

    private Label Heading(string title, string subtitle)
    {
        return new Label
        {
            Text = title + Environment.NewLine + subtitle,
            Dock = DockStyle.Top, Height = 61, ForeColor = text,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Padding = new Padding(3, 3, 0, 0)
        };
    }

    private DataGridView Grid(params string[] columns)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
            MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false, GridColor = Color.FromArgb(232, 235, 240)
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 238, 242);
        g.ColumnHeadersDefaultCellStyle.ForeColor = text;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        g.ColumnHeadersHeight = 34;
        g.RowTemplate.Height = 32;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 242, 237);
        g.DefaultCellStyle.SelectionForeColor = text;
        foreach (var c in columns) g.Columns.Add(Guid.NewGuid().ToString(), c);
        return g;
    }

    private StoreProfile? CurrentStore() => storePicker.SelectedItem as StoreProfile;

    private void RefreshStores(long selectId = 0)
    {
        var current = selectId != 0 ? selectId : CurrentStore()?.Id ?? 0;
        var stores = app.Db.Stores().ToList();
        storePicker.DataSource = null;
        storePicker.DisplayMember = nameof(StoreProfile.Name);
        storePicker.DataSource = stores;
        if (current != 0)
        {
            var index = stores.FindIndex(x => x.Id == current);
            if (index >= 0) storePicker.SelectedIndex = index;
        }
    }

    private async Task TestCurrentStore()
    {
        var s = CurrentStore();
        if (s is null) { WriteLog("Chưa có cửa hàng được cấu hình."); return; }
        connection.Text = "Đang kiểm tra...";
        var r = await app.Api.TestAsync(s);
        connection.Text = r.Success ? "ĐÃ KẾT NỐI" : "LỖI";
        connection.ForeColor = r.Success ? Color.SeaGreen : Color.Firebrick;
        WriteLog($"{s.Marketplace}/{s.Name}: {r.Message}");
    }

    private void Clear() => work.Controls.Clear();

    private void WriteLog(string message)
    {
        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    private Panel Metric(string title, string value, string detail)
    {
        var p = new Panel { Width = 245, Height = 108, BackColor = Color.White, Margin = new Padding(0, 0, 12, 0) };
        p.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(border), 0, 0, p.Width - 1, p.Height - 1);
        p.Controls.Add(new Label { Text = title, AutoSize = true, Left = 14, Top = 12, ForeColor = Color.Gray });
        p.Controls.Add(new Label { Text = value, AutoSize = true, Left = 14, Top = 34, ForeColor = text, Font = new Font("Segoe UI", 22, FontStyle.Bold) });
        p.Controls.Add(new Label { Text = detail, AutoSize = true, Left = 14, Top = 80, ForeColor = accent });
        return p;
    }

    private void ShowOverview()
    {
        Clear();
        work.Controls.Add(Heading("TỔNG QUAN", "Trạng thái dữ liệu, FBS, KIZ và kết nối của toàn bộ cửa hàng."));

        var statusBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White };
        statusBar.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(border), 0, 0, statusBar.Width - 1, statusBar.Height - 1);
        statusBar.Controls.Add(new Label { Text = "Trạng thái hệ thống", Left = 14, Top = 14, AutoSize = true, ForeColor = text, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        statusBar.Controls.Add(new Label { Text = "●  Sẵn sàng", Left = 150, Top = 14, AutoSize = true, ForeColor = accent, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        work.Controls.Add(statusBar);

        var cards = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 120, BackColor = Color.FromArgb(247, 248, 250), WrapContents = false };
        var stores = app.Db.Stores();
        var products = stores.Sum(x => app.Db.Products(x.Id).Count);
        var orders = stores.Sum(x => app.Db.Orders(x.Id).Count);
        var kiz = app.Db.Kiz();
        var znak = app.Db.GetZnakConfig();
        cards.Controls.Add(Metric("Cửa hàng", stores.Count.ToString(), "WB • Ozon • Yandex"));
        cards.Controls.Add(Metric("Sản phẩm", products.ToString(), "Đã lưu cục bộ"));
        cards.Controls.Add(Metric("Đơn FBS", orders.ToString(), "Đang theo dõi"));
        cards.Controls.Add(Metric("Mã KIZ", kiz.Count.ToString(), znak.Enabled ? "Честный ЗНАК đã cấu hình" : "Chưa cấu hình Честный ЗНАК"));
        work.Controls.Add(cards);

        var grid = Grid("Sàn", "Cửa hàng", "Sản phẩm", "Đơn FBS", "Trạng thái");
        foreach (var s in stores)
            grid.Rows.Add(MarketName(s.Marketplace), s.Name, app.Db.Products(s.Id).Count, app.Db.Orders(s.Id).Count, s.Enabled ? "Đang dùng" : "Tắt");
        work.Controls.Add(grid);
        grid.BringToFront();
    }

    private void ShowProducts()
    {
        Clear();
        work.Controls.Add(Heading("SẢN PHẨM", "Đồng bộ danh mục sản phẩm thật từ cửa hàng đang chọn."));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(244, 246, 249) };
        var sync = Primary("ĐỒNG BỘ SẢN PHẨM", 155);
        var filter = new TextBox { Width = 260, PlaceholderText = "Tìm SKU / tên sản phẩm", Margin = new Padding(10, 7, 0, 0) };
        toolbar.Controls.Add(sync); toolbar.Controls.Add(filter);
        work.Controls.Add(toolbar);

        var grid = Grid("Mã sàn", "SKU", "Tên sản phẩm", "Giá", "Ảnh");
        work.Controls.Add(grid);
        grid.BringToFront();

        void LoadRows()
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            foreach (var p in app.Db.Products(s.Id))
            {
                if (!string.IsNullOrWhiteSpace(filter.Text) &&
                    !p.Sku.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) &&
                    !p.Name.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)) continue;
                grid.Rows.Add(p.ExternalId, p.Sku, p.Name, p.Price?.ToString("0.##") ?? "", p.ImageUrl);
            }
        }

        sync.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            sync.Enabled = false;
            var r = await app.SyncProductsAsync(s);
            sync.Enabled = true;
            WriteLog(r.Message);
            if (r.Ok) LoadRows();
        };
        filter.TextChanged += (_, _) => LoadRows();
        LoadRows();
    }

    private void ShowPrices()
    {
        Clear();
        work.Controls.Add(Heading("GIÁ SẢN PHẨM", "Chọn sản phẩm, tính giá theo phần trăm và gửi giá mới lên sàn."));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 760, BackColor = Color.FromArgb(244, 246, 249) };
        work.Controls.Add(split);
        split.BringToFront();

        var grid = Grid("Mã sàn", "SKU", "Tên sản phẩm", "Giá hiện tại");
        split.Panel1.Controls.Add(grid);

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18) };
        split.Panel2.Controls.Add(panel);

        var y = 15;
        panel.Controls.Add(FieldLabel("SKU đã chọn", y)); y += 25;
        var sku = new TextBox { Left = 18, Top = y, Width = 300, ReadOnly = true }; panel.Controls.Add(sku); y += 48;
        panel.Controls.Add(FieldLabel("Giá mới (RUB)", y)); y += 25;
        var price = new NumericUpDown { Left = 18, Top = y, Width = 175, DecimalPlaces = 2, Maximum = 100000000, Minimum = 1 }; panel.Controls.Add(price);
        var percent = new NumericUpDown { Left = 205, Top = y, Width = 110, DecimalPlaces = 2, Minimum = -99, Maximum = 500, Value = 10 }; panel.Controls.Add(percent);
        y += 38;
        var calc = Button("TÍNH THEO %", 110); calc.Left = 205; calc.Top = y; panel.Controls.Add(calc);
        var preview = new Label { Left = 18, Top = y + 7, Width = 180, ForeColor = Color.FromArgb(92, 113, 138), Text = "Xem trước: -" }; panel.Controls.Add(preview);
        y += 50;
        var apply = Primary("CẬP NHẬT GIÁ", 145); apply.Left = 18; apply.Top = y; panel.Controls.Add(apply);

        var s = CurrentStore();
        if (s != null)
            foreach (var p in app.Db.Products(s.Id)) grid.Rows.Add(p.ExternalId, p.Sku, p.Name, p.Price?.ToString("0.##") ?? "");

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            sku.Text = grid.SelectedRows[0].Cells[1].Value?.ToString() ?? "";
            if (decimal.TryParse(grid.SelectedRows[0].Cells[3].Value?.ToString(), out var v) && v > 0) price.Value = Math.Min(v, price.Maximum);
        };

        calc.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            if (!decimal.TryParse(grid.SelectedRows[0].Cells[3].Value?.ToString(), out var current) || current <= 0) return;
            var next = Math.Max(1, Math.Round(current * (1 + percent.Value / 100m), 2));
            price.Value = Math.Min(next, price.Maximum);
            preview.Text = $"{current:0.##} → {next:0.##} RUB";
        };

        apply.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null || string.IsNullOrWhiteSpace(sku.Text)) return;
            var product = app.Db.Product(store.Id, sku.Text); if (product is null) return;
            if (MessageBox.Show($"Cập nhật {product.Sku} thành {price.Value:0.##} RUB?", "Xác nhận thay giá", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            apply.Enabled = false;
            var r = await app.ChangePriceAsync(store, product, price.Value);
            apply.Enabled = true;
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "Đã gửi" : "Lỗi", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private void ShowCopy()
    {
        Clear();
        work.Controls.Add(Heading("SAO CHÉP BÀI ĐĂNG", "Sao chép bài đăng giữa các cửa hàng cùng sàn."));

        var panel = new Panel { Dock = DockStyle.Top, Height = 205, BackColor = Color.White, Padding = new Padding(16) };
        work.Controls.Add(panel);
        panel.BringToFront();

        var stores = app.Db.Stores().ToList();
        panel.Controls.Add(FieldLabel("Cửa hàng nguồn", 12));
        var source = new ComboBox { Left = 16, Top = 38, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = stores.ToList(), DisplayMember = nameof(StoreProfile.Name) };
        panel.Controls.Add(source);
        panel.Controls.Add(FieldLabel("Cửa hàng đích", 78));
        var dest = new ComboBox { Left = 16, Top = 104, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = stores.ToList(), DisplayMember = nameof(StoreProfile.Name) };
        panel.Controls.Add(dest);

        panel.Controls.Add(FieldLabel("SKU nguồn", 12, 300));
        var sourceSku = new TextBox { Left = 300, Top = 38, Width = 240 }; panel.Controls.Add(sourceSku);
        panel.Controls.Add(FieldLabel("SKU đích", 78, 300));
        var destSku = new TextBox { Left = 300, Top = 104, Width = 240 }; panel.Controls.Add(destSku);

        var copy = Primary("SAO CHÉP", 120); copy.Left = 570; copy.Top = 102; panel.Controls.Add(copy);
        copy.Click += async (_, _) =>
        {
            if (source.SelectedItem is not StoreProfile src || dest.SelectedItem is not StoreProfile dst) return;
            var p = app.Db.Product(src.Id, sourceSku.Text.Trim());
            if (p is null) { WriteLog("Không tìm thấy SKU nguồn. Hãy đồng bộ sản phẩm trước."); return; }
            if (string.IsNullOrWhiteSpace(destSku.Text)) { WriteLog("Cần nhập SKU đích."); return; }
            if (MessageBox.Show($"Sao chép {p.Sku} từ {src.Name} sang {dst.Name}?", "Xác nhận sao chép", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            copy.Enabled = false;
            var r = await app.Api.CopySameMarketplaceAsync(src, dst, p, destSku.Text.Trim());
            copy.Enabled = true;
            app.Db.Audit("Sao chép", r.Success ? "Đã gửi" : "Lỗi", $"{src.Name}:{p.Sku}->{dst.Name}:{destSku.Text}:{r.Message}");
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "Đã gửi" : "Lỗi", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private void ShowFbs()
    {
        Clear();
        work.Controls.Add(Heading("ĐÓNG HÀNG FBS", "Quy trình: đồng bộ đơn → quét hàng/KIZ → đóng hàng → tải nhãn → lô giao hàng."));

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(150, 34), SizeMode = TabSizeMode.Fixed
        };
        tabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == tabs.SelectedIndex;
            var rect = e.Bounds;
            using var bg = new SolidBrush(selected ? accent : Color.FromArgb(233, 236, 240));
            e.Graphics.FillRectangle(bg, rect);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, new Font("Segoe UI", 9, FontStyle.Bold),
                rect, selected ? Color.White : text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        tabs.TabPages.Add(BuildFbsQueueTab());
        tabs.TabPages.Add(BuildFbsPackingTab());
        tabs.TabPages.Add(BuildFbsLabelsTab());
        tabs.TabPages.Add(BuildFbsShipmentTab());
        work.Controls.Add(tabs);
        tabs.BringToFront();
    }

    private TabPage BuildFbsQueueTab()
    {
        var page = new TabPage("Đơn FBS") { BackColor = Color.FromArgb(245, 246, 248) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        var sync = Primary("ĐỒNG BỘ ĐƠN", 135);
        var search = new TextBox { Width = 310, PlaceholderText = "Tìm mã đơn / SKU", Margin = new Padding(8, 7, 0, 0) };
        toolbar.Controls.Add(sync); toolbar.Controls.Add(search);
        page.Controls.Add(toolbar);

        var grid = Grid("Mã đơn", "SKU", "Tên hàng", "SL", "Trạng thái", "KIZ");
        page.Controls.Add(grid); grid.BringToFront();

        void Load(string q = "")
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            foreach (var o in app.Db.Orders(s.Id))
            {
                if (!string.IsNullOrWhiteSpace(q) &&
                    !o.ExternalOrderId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !o.Sku.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                grid.Rows.Add(o.ExternalOrderId, o.Sku, o.Name, o.Quantity, o.Status, o.NeedsKiz ? "Bắt buộc" : "Không");
            }
        }

        sync.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            sync.Enabled = false;
            var r = await app.SyncOrdersAsync(s);
            sync.Enabled = true;
            WriteLog(r.Message);
            if (r.Ok) Load();
        };
        search.TextChanged += (_, _) => Load(search.Text.Trim());
        Load();
        return page;
    }

    private TabPage BuildFbsPackingTab()
    {
        var page = new TabPage("Đóng hàng") { BackColor = Color.FromArgb(245, 246, 248) };
        var top = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Color.White };
        top.Controls.Add(new Label { Text = "Quét mã đơn / SKU / DataMatrix", Left = 16, Top = 14, AutoSize = true, ForeColor = text, Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        var scan = new TextBox { Left = 16, Top = 42, Width = 430, Height = 30, PlaceholderText = "Đặt con trỏ tại đây và quét..." };
        top.Controls.Add(scan);
        var selected = new Label { Left = 470, Top = 18, Width = 470, Height = 60, ForeColor = text, Text = "Chưa chọn đơn." };
        top.Controls.Add(selected);
        var pack = Primary("XÁC NHẬN ĐÓNG HÀNG", 170); pack.Left = 970; pack.Top = 38; top.Controls.Add(pack);
        page.Controls.Add(top);

        var grid = Grid("Mã đơn", "SKU", "Tên hàng", "SL", "Trạng thái", "KIZ");
        page.Controls.Add(grid); grid.BringToFront();

        FbsOrderRow? chosen = null;

        void Load(string q = "")
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            foreach (var o in app.Db.Orders(s.Id))
            {
                if (!string.IsNullOrWhiteSpace(q) &&
                    !o.ExternalOrderId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !o.Sku.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                grid.Rows.Add(o.ExternalOrderId, o.Sku, o.Name, o.Quantity, o.Status, o.NeedsKiz ? "Bắt buộc" : "Không");
            }
        }

        grid.SelectionChanged += (_, _) =>
        {
            var s = CurrentStore(); if (s is null || grid.SelectedRows.Count == 0) return;
            var orderId = grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            var sku = grid.SelectedRows[0].Cells[1].Value?.ToString() ?? "";
            chosen = app.Db.Orders(s.Id).FirstOrDefault(x => x.ExternalOrderId == orderId && x.Sku == sku);
            selected.Text = chosen is null ? "Chưa chọn đơn." : $"Đơn: {chosen.ExternalOrderId}\nSKU: {chosen.Sku}  •  SL: {chosen.Quantity}  •  KIZ: {(chosen.NeedsKiz ? "Bắt buộc" : "Không")}";
        };

        scan.TextChanged += (_, _) => Load(scan.Text.Trim());
        scan.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            var parsed = AppServices.ParseKiz(scan.Text);
            if (parsed.Ok)
            {
                app.Db.UpsertKiz(scan.Text.Trim(), parsed.Gtin, "AVAILABLE");
                WriteLog("Đã nhận mã KIZ, GTIN: " + parsed.Gtin);
                scan.Clear();
                e.SuppressKeyPress = true;
            }
        };

        pack.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null || chosen is null) return;
            if (chosen.NeedsKiz && app.Db.Kiz().Count == 0)
            {
                MessageBox.Show("Đơn yêu cầu KIZ nhưng kho KIZ đang trống.", "Thiếu KIZ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show($"Xác nhận đóng đơn {chosen.ExternalOrderId}?", "Xác nhận đóng hàng", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            pack.Enabled = false;
            var r = await app.Api.PackOrderAsync(s, chosen);
            pack.Enabled = true;
            app.Db.Audit("FBS", r.Success ? "Đóng hàng" : "Lỗi đóng hàng", $"{chosen.ExternalOrderId}:{r.Message}");
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "Đã gửi" : "Lỗi", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (r.Success) await app.SyncOrdersAsync(s);
            Load();
        };

        Load();
        return page;
    }

    private TabPage BuildFbsLabelsTab()
    {
        var page = new TabPage("In nhãn") { BackColor = Color.FromArgb(245, 246, 248) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        var reload = Button("TẢI LẠI DANH SÁCH", 155);
        var download = Primary("TẢI NHÃN", 115);
        top.Controls.Add(reload); top.Controls.Add(download);
        page.Controls.Add(top);

        var grid = Grid("Mã đơn", "SKU", "Tên hàng", "Trạng thái");
        page.Controls.Add(grid); grid.BringToFront();

        void Load()
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            foreach (var o in app.Db.Orders(s.Id)) grid.Rows.Add(o.ExternalOrderId, o.Sku, o.Name, o.Status);
        }

        reload.Click += (_, _) => Load();
        download.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null || grid.SelectedRows.Count == 0) return;
            var orderId = grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            var r = await app.Api.DownloadLabelAsync(s, orderId);
            WriteLog(r.Message + (r.FilePath is null ? "" : " " + r.FilePath));
            if (r.Success && r.FilePath is not null)
            {
                if (MessageBox.Show("Đã lưu nhãn. Mở thư mục chứa nhãn?", "Nhãn FBS", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.FilePath}\"") { UseShellExecute = true });
            }
            else MessageBox.Show(r.Message, "Lỗi tải nhãn", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Load();
        return page;
    }

    private TabPage BuildFbsShipmentTab()
    {
        var page = new TabPage("Giao hàng") { BackColor = Color.FromArgb(245, 246, 248) };
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22) };
        p.Controls.Add(new Label
        {
            AutoSize = false, Width = 760, Height = 170, Left = 20, Top = 20, ForeColor = text, Font = new Font("Segoe UI", 10),
            Text = "Quản lý lô giao hàng\r\n\r\n• Wildberries: khi xác nhận đóng hàng, ứng dụng tạo supply và thêm đơn vào supply.\r\n• Ozon: đơn được chuyển sang trạng thái chờ giao sau khi ship FBS.\r\n• Yandex Market: đơn được chuyển READY_TO_SHIP.\r\n\r\nMã lô/QR được lấy theo khả năng API của từng sàn sau khi lô đủ điều kiện giao."
        });
        var refresh = Primary("ĐỒNG BỘ TRẠNG THÁI", 165); refresh.Left = 20; refresh.Top = 205; p.Controls.Add(refresh);
        refresh.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            var r = await app.SyncOrdersAsync(s);
            WriteLog(r.Message);
            MessageBox.Show(r.Message, "Đồng bộ FBS");
        };
        page.Controls.Add(p);
        return page;
    }

    private void ShowKiz()
    {
        Clear();
        work.Controls.Add(Heading("MÃ KIZ", "Quét DataMatrix, tách GTIN và quản lý kho KIZ cục bộ."));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        var input = new TextBox { Width = 470, PlaceholderText = "Dán hoặc quét DataMatrix...", Margin = new Padding(0, 7, 8, 0) };
        var save = Primary("KIỂM TRA / LƯU", 135);
        top.Controls.Add(input); top.Controls.Add(save);
        work.Controls.Add(top);

        var grid = Grid("GTIN", "Trạng thái", "Đơn đã gán", "Mã KIZ");
        work.Controls.Add(grid); grid.BringToFront();

        void Load()
        {
            grid.Rows.Clear();
            foreach (var k in app.Db.Kiz()) grid.Rows.Add(k.Gtin, TranslateKizStatus(k.Status), k.Assigned, k.Code);
        }

        save.Click += (_, _) =>
        {
            var p = AppServices.ParseKiz(input.Text);
            WriteLog(p.Message);
            if (!p.Ok) { MessageBox.Show(p.Message, "Mã KIZ không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            app.Db.UpsertKiz(input.Text.Trim(), p.Gtin, "AVAILABLE");
            input.Clear();
            Load();
        };
        Load();
    }

    private void ShowZnakRegistration()
    {
        Clear();
        work.Controls.Add(Heading("ĐĂNG KÝ ЧЕСТНЫЙ ЗНАК", "Kiểm tra điều kiện đăng ký và mở cổng đăng ký chính thức."));

        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22), AutoScroll = true };
        work.Controls.Add(p); p.BringToFront();

        var certs = AppServices.Certificates();
        var hasKey = certs.Any(x => x.HasPrivateKey && x.NotAfter > DateTime.Now);

        p.Controls.Add(new Label { Text = "1. Chữ ký điện tử УКЭП", Left = 20, Top = 20, AutoSize = true, ForeColor = text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });
        p.Controls.Add(new Label { Text = hasKey ? "✓ Đã tìm thấy chứng thư còn hiệu lực có khóa riêng." : "✕ Chưa tìm thấy chứng thư phù hợp.", Left = 20, Top = 52, AutoSize = true, ForeColor = hasKey ? Color.SeaGreen : Color.Firebrick });

        p.Controls.Add(new Label { Text = "2. CryptoPro / Rutoken", Left = 20, Top = 92, AutoSize = true, ForeColor = text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });
        p.Controls.Add(new Label { Text = "Ứng dụng sử dụng chứng thư trong Windows Certificate Store. Private key/PIN không được lưu trong ứng dụng.", Left = 20, Top = 124, AutoSize = true, ForeColor = Color.DimGray });

        p.Controls.Add(new Label { Text = "3. Đăng ký doanh nghiệp", Left = 20, Top = 164, AutoSize = true, ForeColor = text, Font = new Font("Segoe UI", 11, FontStyle.Bold) });
        p.Controls.Add(new Label { Text = "Nhấn nút dưới để mở cổng Честный ЗНАК và đăng nhập bằng УКЭП.", Left = 20, Top = 196, AutoSize = true, ForeColor = Color.DimGray });

        var open = Primary("MỞ CỔNG ĐĂNG KÝ", 165); open.Left = 20; open.Top = 232; p.Controls.Add(open);
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("https://markirovka.crpt.ru/") { UseShellExecute = true });

        var cert = Button("XEM CHỨNG THƯ", 145); cert.Left = 200; cert.Top = 232; p.Controls.Add(cert);
        cert.Click += (_, _) => ShowCertificates();
    }

    private void ShowZnakSettings()
    {
        Clear();
        work.Controls.Add(Heading("CẤU HÌNH ЧЕСТНЫЙ ЗНАК", "Lưu INN, môi trường, chứng thư ký và chế độ ký điện tử."));

        var z = app.Db.GetZnakConfig();
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22), AutoScroll = true };
        work.Controls.Add(p); p.BringToFront();

        var inn = LabeledBox(p, "INN doanh nghiệp", 20, 20, 320);
        inn.Text = z.Inn;

        p.Controls.Add(new Label { Text = "Môi trường", Left = 20, Top = 86, AutoSize = true, ForeColor = text });
        var env = new ComboBox { Left = 20, Top = 109, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        env.Items.AddRange(new object[] { "Production", "Test" });
        env.SelectedItem = z.Environment;
        if (env.SelectedIndex < 0) env.SelectedIndex = 0;
        p.Controls.Add(env);

        p.Controls.Add(new Label { Text = "Chứng thư ký điện tử", Left = 20, Top = 155, AutoSize = true, ForeColor = text });
        var certs = AppServices.Certificates().Where(x => x.HasPrivateKey).ToList();
        var certBox = new ComboBox { Left = 20, Top = 178, Width = 650, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var c in certs) certBox.Items.Add(new CertificateItem(c.Subject, c.Thumbprint, c.NotAfter, c.HasPrivateKey));
        certBox.DisplayMember = nameof(CertificateItem.Display);
        var certIndex = certBox.Items.Cast<CertificateItem>().ToList().FindIndex(x => x.Thumbprint.Equals(z.CertificateThumbprint, StringComparison.OrdinalIgnoreCase));
        if (certIndex >= 0) certBox.SelectedIndex = certIndex; else if (certBox.Items.Count > 0) certBox.SelectedIndex = 0;
        p.Controls.Add(certBox);

        p.Controls.Add(new Label { Text = "Chế độ ký", Left = 20, Top = 226, AutoSize = true, ForeColor = text });
        var auto = new ComboBox { Left = 20, Top = 249, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        auto.Items.AddRange(new object[] { "Thủ công", "Bán tự động", "Tự động theo danh sách cho phép" });
        auto.SelectedItem = z.AutoSignMode;
        if (auto.SelectedIndex < 0) auto.SelectedIndex = 0;
        p.Controls.Add(auto);

        var enabled = new CheckBox { Text = "Bật tích hợp Честный ЗНАК", Left = 20, Top = 300, Width = 260, Checked = z.Enabled, ForeColor = text };
        p.Controls.Add(enabled);

        var save = Primary("LƯU CẤU HÌNH", 135); save.Left = 20; save.Top = 342; p.Controls.Add(save);
        var test = Button("KIỂM TRA CẤU HÌNH", 160); test.Left = 170; test.Top = 342; p.Controls.Add(test);

        save.Click += (_, _) =>
        {
            var item = certBox.SelectedItem as CertificateItem;
            var cfg = new ZnakConfig(
                inn.Text.Trim(),
                env.SelectedItem?.ToString() ?? "Production",
                item?.Thumbprint ?? "",
                item?.Subject ?? "",
                auto.SelectedItem?.ToString() ?? "Thủ công",
                enabled.Checked);
            app.Db.SaveZnakConfig(cfg);
            WriteLog("Đã lưu cấu hình Честный ЗНАК.");
            MessageBox.Show("Đã lưu cấu hình.", "Честный ЗНАК");
        };

        test.Click += (_, _) =>
        {
            var item = certBox.SelectedItem as CertificateItem;
            if (string.IsNullOrWhiteSpace(inn.Text))
            {
                MessageBox.Show("Chưa nhập INN doanh nghiệp.", "Thiếu cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (item is null)
            {
                MessageBox.Show("Không có chứng thư có khóa riêng.", "Thiếu chứng thư", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (item.NotAfter <= DateTime.Now)
            {
                MessageBox.Show("Chứng thư đã hết hạn.", "Chứng thư không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show($"Cấu hình cục bộ hợp lệ.\nChứng thư: {item.Subject}\nHết hạn: {item.NotAfter:d}", "Kiểm tra thành công", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
    }

    private void ShowCertificates()
    {
        var list = AppServices.Certificates();
        var value = list.Count == 0 ? "Không tìm thấy chứng thư trong CurrentUser/My." :
            string.Join(Environment.NewLine + Environment.NewLine,
                list.Take(30).Select(x => $"{x.Subject}\nHết hạn: {x.NotAfter:d}\nCó khóa riêng: {(x.HasPrivateKey ? "Có" : "Không")}\n{x.Thumbprint}"));
        MessageBox.Show(value, "Chứng thư điện tử", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowStores()
    {
        Clear();
        work.Controls.Add(Heading("CỬA HÀNG / API", "Thông tin bí mật được mã hóa bằng Windows DPAPI theo tài khoản Windows hiện tại."));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 390 };
        work.Controls.Add(split); split.BringToFront();

        var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
        split.Panel1.Controls.Add(list);
        var stores = app.Db.Stores().ToList();
        foreach (var s in stores) list.Items.Add(s);

        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true, Padding = new Padding(20) };
        split.Panel2.Controls.Add(p);

        p.Controls.Add(new Label { Text = "Sàn", Left = 20, Top = 20, AutoSize = true, ForeColor = text });
        var market = new ComboBox { Left = 20, Top = 42, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<Marketplace>() };
        p.Controls.Add(market);
        var name = LabeledBox(p, "Tên cửa hàng", 20, 82, 340);
        var client = LabeledBox(p, "Ozon Client-Id", 20, 145, 340);
        var apiKey = LabeledBox(p, "API Key (Ozon/Yandex)", 20, 205, 520, true);
        var business = LabeledBox(p, "Yandex Business ID", 20, 265, 340);
        var campaign = LabeledBox(p, "Yandex Campaign ID", 20, 325, 340);
        var token = LabeledBox(p, "Wildberries Token", 20, 385, 520, true);
        var save = Primary("LƯU CỬA HÀNG", 135); save.Left = 20; save.Top = 455; p.Controls.Add(save);
        var test = Button("KIỂM TRA API", 120); test.Left = 165; test.Top = 455; p.Controls.Add(test);

        long editingId = 0;
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItem is not StoreProfile s) return;
            editingId = s.Id; market.SelectedItem = s.Marketplace; name.Text = s.Name; client.Text = s.ClientId;
            apiKey.Text = s.ApiKey; business.Text = s.BusinessId; campaign.Text = s.CampaignId; token.Text = s.Token;
        };

        StoreProfile Read() => new(editingId, (Marketplace)market.SelectedItem!, name.Text.Trim(), client.Text.Trim(), apiKey.Text, business.Text.Trim(), campaign.Text.Trim(), token.Text, true);

        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show("Cần nhập tên cửa hàng."); return; }
            var saved = app.Db.SaveStore(Read());
            WriteLog($"Đã lưu cửa hàng {saved.Marketplace}/{saved.Name}.");
            RefreshStores(saved.Id);
            ShowStores();
        };
        test.Click += async (_, _) =>
        {
            var r = await app.Api.TestAsync(Read());
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "API hoạt động" : "Lỗi API", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private void ShowHistory()
    {
        Clear();
        work.Controls.Add(Heading("LỊCH SỬ", "Lịch sử thao tác giá, FBS, KIZ, sao chép và cấu hình."));

        var grid = Grid("Thời gian", "Mục", "Thao tác", "Chi tiết");
        foreach (var x in app.Db.AuditRows())
            grid.Rows.Add(x.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), TranslateModule(x.Module), TranslateAction(x.Action), x.Detail);
        work.Controls.Add(grid);
        grid.BringToFront();
    }

    private void ShowSettings()
    {
        Clear();
        work.Controls.Add(Heading("CÀI ĐẶT", "Thông tin hệ thống và chẩn đoán."));

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 270,
            Font = new Font("Consolas", 10), BackColor = Color.White,
            Text = $"Phiên bản: 0.4.1{Environment.NewLine}Cơ sở dữ liệu: {app.Db.DbPath}{Environment.NewLine}Hệ điều hành: {Environment.OSVersion}{Environment.NewLine}.NET: {Environment.Version}{Environment.NewLine}Số cửa hàng: {app.Db.Stores().Count}{Environment.NewLine}Số chứng thư: {AppServices.Certificates().Count}"
        };
        work.Controls.Add(box);
        box.BringToFront();
    }

    private TextBox LabeledBox(Control parent, string label, int left, int top, int width, bool password = false)
    {
        parent.Controls.Add(new Label { Text = label, Left = left, Top = top, AutoSize = true, ForeColor = text });
        var t = new TextBox { Left = left, Top = top + 23, Width = width, UseSystemPasswordChar = password };
        parent.Controls.Add(t);
        return t;
    }

    private Label FieldLabel(string caption, int top, int left = 18) =>
        new() { Text = caption, AutoSize = true, Left = left, Top = top, ForeColor = text, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    private static string MarketName(Marketplace m) => m switch
    {
        Marketplace.Wildberries => "Wildberries",
        Marketplace.Ozon => "Ozon",
        Marketplace.Yandex => "Yandex Market",
        _ => m.ToString()
    };

    private static string TranslateKizStatus(string s) => s switch
    {
        "AVAILABLE" => "Sẵn sàng",
        "RESERVED" => "Đã giữ",
        "ASSIGNED" => "Đã gán",
        "SHIPPED" => "Đã giao",
        "INVALID" => "Không hợp lệ",
        "RETIRED" => "Đã ngừng",
        _ => s
    };

    private static string TranslateModule(string s) => s switch
    {
        "Stores" => "Cửa hàng",
        "Products" => "Sản phẩm",
        "Price" => "Giá",
        "Copy" => "Sao chép",
        "KIZ" => "Mã KIZ",
        "FBS" => "Đóng hàng FBS",
        "SelfTest" => "Tự kiểm tra",
        _ => s
    };

    private static string TranslateAction(string s) => s switch
    {
        "Save" => "Lưu",
        "Sync" => "Đồng bộ",
        "Accepted" => "Đã tiếp nhận",
        "Failed" => "Lỗi",
        "Upsert" => "Cập nhật",
        "Database" => "Cơ sở dữ liệu",
        _ => s
    };

    private sealed record CertificateItem(string Subject, string Thumbprint, DateTime NotAfter, bool HasPrivateKey)
    {
        public string Display => $"{Subject}  |  Hết hạn {NotAfter:dd/MM/yyyy}";
    }
}
