using System.Drawing.Drawing2D;

namespace MarketplaceHub.UI;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(242, 246, 250);
    private static readonly Color Surface = Color.White;
    private static readonly Color Border = Color.FromArgb(223, 230, 238);
    private static readonly Color TextMain = Color.FromArgb(42, 52, 69);
    private static readonly Color TextMuted = Color.FromArgb(128, 141, 160);
    private static readonly Color Accent = Color.FromArgb(17, 161, 146);
    private static readonly Color AccentSoft = Color.FromArgb(232, 248, 245);
    private static readonly Color Danger = Color.FromArgb(231, 62, 91);
    private static readonly Color DangerSoft = Color.FromArgb(255, 237, 240);
    private static readonly Color Warning = Color.FromArgb(240, 163, 35);
    private static readonly Color WarningSoft = Color.FromArgb(255, 246, 229);

    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(24) };
    private readonly Label pageTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = TextMain };
    private readonly Label pageSubtitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f), ForeColor = TextMuted };
    private readonly List<Button> navButtons = new();
    private string activeNav = "Hôm nay";

    public MainForm()
    {
        Text = "Marketplace Hub";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1220, 760);
        Size = new Size(1500, 900);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        shell.Controls.Add(BuildSidebar(), 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(BuildTopBar(), 0, 0);
        right.Controls.Add(content, 0, 1);
        shell.Controls.Add(right, 1, 0);

        ShowToday();
    }

    private Control BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        panel.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Border), panel.Width - 1, 0, panel.Width - 1, panel.Height);

        var brand = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Surface };
        var icon = new RoundedPanel { Radius = 11, BackColor = Accent, Size = new Size(40, 40), Location = new Point(13, 18) };
        icon.Controls.Add(new Label { Text = "⚡", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI Symbol", 19, FontStyle.Bold) });
        brand.Controls.Add(icon);
        brand.Controls.Add(new Label { Text = "Marketplace Hub", AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(61, 18) });
        brand.Controls.Add(new Label { Text = "SELLER HUB", AutoSize = true, ForeColor = TextMuted, Font = new Font("Segoe UI", 8, FontStyle.Bold), Location = new Point(63, 44) });
        panel.Controls.Add(brand);

        var storeSelect = new RoundedPanel { Radius = 10, BackColor = Surface, BorderColor = Border, BorderWidth = 1, Height = 38, Width = 211, Location = new Point(12, 90) };
        storeSelect.Controls.Add(new Label { Text = "OZ", AutoSize = false, Size = new Size(26, 26), Location = new Point(7, 6), BackColor = Color.FromArgb(16, 97, 220), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 8, FontStyle.Bold) });
        storeSelect.Controls.Add(new Label { Text = "PMP Store Man", AutoSize = true, Location = new Point(42, 10), ForeColor = TextMain, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        storeSelect.Controls.Add(new Label { Text = "⌄", AutoSize = true, Location = new Point(187, 10), ForeColor = TextMuted });
        panel.Controls.Add(storeSelect);

        var menu = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Location = new Point(12, 140),
            Size = new Size(211, 570),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface
        };
        panel.Controls.Add(menu);

        AddSection(menu, "TỔNG QUAN");
        AddNav(menu, "◉  Hôm nay", "Hôm nay", ShowToday);
        AddNav(menu, "▧  Tổng quan", "Tổng quan", ShowDashboard);

        AddSection(menu, "BÁN HÀNG");
        AddNav(menu, "▤  Đơn hàng", "Đơn hàng", ShowFbs);
        AddNav(menu, "🛒  Sản phẩm", "Sản phẩm", ShowProducts);
        AddNav(menu, "▣  Giá sản phẩm", "Giá sản phẩm", ShowPrices);
        AddNav(menu, "⇄  Sao chép sản phẩm", "Sao chép sản phẩm", ShowCopy);

        AddSection(menu, "CÔNG VIỆC");
        AddNav(menu, "◆  Честный ЗНАК / KIZ", "Честный ЗНАК / KIZ", ShowZnak);
        AddNav(menu, "◫  Jobs", "Jobs", ShowJobs);
        AddNav(menu, "▦  Lịch sử", "Lịch sử", ShowHistory);

        AddSection(menu, "CẤU HÌNH");
        AddNav(menu, "⚙  Cửa hàng / API", "Cửa hàng / API", ShowStores);
        AddNav(menu, "⌁  Thiết bị", "Thiết bị", ShowDevices);
        AddNav(menu, "☷  Cài đặt", "Cài đặt", ShowSettings);

        var footer = new RoundedPanel
        {
            Radius = 10, BackColor = Color.FromArgb(238, 250, 248), BorderColor = Color.FromArgb(205, 239, 234),
            BorderWidth = 1, Size = new Size(205, 76), Location = new Point(14, 725),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom
        };
        footer.Controls.Add(new Label { Text = "STORES", AutoSize = true, ForeColor = TextMuted, Font = new Font("Segoe UI", 8, FontStyle.Bold), Location = new Point(80, 15) });
        footer.Controls.Add(new Label { Text = "3", AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 18, FontStyle.Bold), Location = new Point(95, 35) });
        panel.Controls.Add(footer);

        return panel;
    }

    private Control BuildTopBar()
    {
        var top = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        top.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Border), 0, top.Height - 1, top.Width, top.Height - 1);

        top.Controls.Add(new Label { Text = "●", AutoSize = true, ForeColor = Warning, Font = new Font("Segoe UI", 14), Location = new Point(20, 17) });
        top.Controls.Add(new Label { Text = "PMP Store Man", AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(41, 18) });

        var plan = new RoundedPanel { Radius = 14, BackColor = WarningSoft, Size = new Size(86, 27), Location = new Point(170, 17) };
        plan.Controls.Add(new Label { Text = "HẾT HẠN GÓI", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(190, 116, 17), Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) });
        top.Controls.Add(plan);

        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Surface };
        actions.Controls.Add(TopPill("🔔  1", 74));
        actions.Controls.Add(TopPill("VN  Tiếng Việt ⌄", 122));
        actions.Controls.Add(TopPill("⚙  Cài đặt cửa hàng", 154, ShowStores));
        actions.Controls.Add(TopPill("👤  Thanhcong2468", 150));
        top.Controls.Add(actions);
        top.Resize += (_, _) => actions.Location = new Point(top.ClientSize.Width - actions.Width - 18, 13);

        return top;
    }

    private Control TopPill(string text, int width, Action? action = null)
    {
        var b = new Button { Text = text, Width = width, Height = 35, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = TextMain, Font = new Font("Segoe UI", 9), Margin = new Padding(5, 0, 0, 0), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        if (action != null) b.Click += (_, _) => action();
        return b;
    }

    private void AddSection(FlowLayoutPanel menu, string text)
    {
        menu.Controls.Add(new Label { Text = text, Width = 190, Height = 30, TextAlign = ContentAlignment.BottomLeft, ForeColor = Color.FromArgb(151, 164, 184), Font = new Font("Segoe UI", 8, FontStyle.Bold), Margin = new Padding(6, 8, 0, 2) });
    }

    private void AddNav(FlowLayoutPanel menu, string caption, string key, Action action)
    {
        var b = new Button
        {
            Text = caption, Width = 198, Height = 36, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain, BackColor = Surface, Padding = new Padding(8, 0, 0, 0), Margin = new Padding(0, 1, 0, 1), Cursor = Cursors.Hand,
            Tag = key, Font = new Font("Segoe UI", 9)
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(246, 249, 252);
        b.Click += (_, _) => { activeNav = key; ApplyNavState(); action(); };
        navButtons.Add(b);
        menu.Controls.Add(b);
    }

    private void ApplyNavState()
    {
        foreach (var b in navButtons)
        {
            var active = Equals(b.Tag, activeNav);
            b.BackColor = active ? AccentSoft : Surface;
            b.ForeColor = active ? Accent : TextMain;
            b.Font = new Font("Segoe UI", 9, active ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private void SetPage(string title, string subtitle)
    {
        pageTitle.Text = title;
        pageSubtitle.Text = subtitle;
        content.Controls.Clear();
        ApplyNavState();
    }

    private Control BuildPageHeader(string title, string subtitle, string? statusText = null)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Bg };
        pageTitle.Text = title; pageTitle.Location = new Point(0, 7);
        pageSubtitle.Text = subtitle; pageSubtitle.Location = new Point(2, 40);
        p.Controls.Add(pageTitle); p.Controls.Add(pageSubtitle);

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            var status = new RoundedPanel { Radius = 13, BackColor = WarningSoft, Size = new Size(112, 28), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            status.Controls.Add(new Label { Text = statusText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(188, 112, 16), Font = new Font("Segoe UI", 8, FontStyle.Bold) });
            p.Controls.Add(status);
            p.Resize += (_, _) => status.Location = new Point(p.ClientSize.Width - status.Width, 9);
        }
        return p;
    }

    private void ShowToday()
    {
        SetPage("Hôm nay", "Tình trạng cửa hàng và công việc cần xử lý");
        content.Controls.Add(BuildPageHeader("PMP Store Man", "Tạo lúc: 05-08-2026", "● HẾT HẠN GÓI"));

        var storeCard = new RoundedPanel { Dock = DockStyle.Top, Height = 52, Radius = 14, BackColor = Surface, BorderColor = Color.FromArgb(235, 239, 244), BorderWidth = 1, Padding = new Padding(18, 10, 18, 10), Margin = new Padding(0, 0, 0, 14) };
        storeCard.Controls.Add(new Label { Text = "●  HẾT HẠN GÓI", AutoSize = true, ForeColor = Color.FromArgb(190, 116, 17), Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(18, 16) });
        storeCard.Controls.Add(new Label { Text = "Tạo lúc: 05-08-2026", AutoSize = true, ForeColor = TextMuted, Location = new Point(146, 17) });
        var del = OutlineButton("🗑  Xóa cửa hàng", Danger, 124);
        del.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        storeCard.Controls.Add(del);
        storeCard.Resize += (_, _) => del.Location = new Point(storeCard.ClientSize.Width - del.Width - 16, 9);

        var verify = new RoundedPanel { Dock = DockStyle.Top, Height = 34, Radius = 12, BackColor = Bg };
        var verifyLabel = new Label { Text = "◷  Không xác minh được", AutoSize = true, ForeColor = TextMuted, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(0, 6) };
        verify.Controls.Add(verifyLabel);
        verify.Resize += (_, _) => verifyLabel.Location = new Point(verify.ClientSize.Width - verifyLabel.Width - 4, 7);

        var alert = new RoundedPanel { Dock = DockStyle.Top, Height = 94, Radius = 14, BackColor = DangerSoft, BorderColor = Color.FromArgb(255, 128, 147), BorderWidth = 1, Padding = new Padding(22, 15, 22, 15) };
        alert.Controls.Add(new Label { Text = "×", AutoSize = false, Size = new Size(26, 26), Location = new Point(22, 16), TextAlign = ContentAlignment.MiddleCenter, BackColor = Danger, ForeColor = Color.White, Font = new Font("Segoe UI", 14, FontStyle.Bold) });
        alert.Controls.Add(new Label { Text = "Không tải được dữ liệu Today", AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 10), Location = new Point(57, 17) });
        alert.Controls.Add(new Label { Text = "Cửa hàng hoặc gói sử dụng chưa hoạt động. Hãy kiểm tra gói và trạng thái API key.", AutoSize = true, ForeColor = Color.FromArgb(83, 91, 105), Location = new Point(57, 48) });
        var retry = FilledButton("Thử tải lại", Surface, TextMain, 98);
        retry.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        alert.Controls.Add(retry);
        alert.Resize += (_, _) => retry.Location = new Point(alert.ClientSize.Width - retry.Width - 22, 20);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Bg };
        content.Controls.Add(alert);
        content.Controls.Add(spacer);
        content.Controls.Add(verify);
        content.Controls.Add(storeCard);
        alert.BringToFront();
    }

    private void ShowDashboard()
    {
        SetPage("Tổng quan", "WB • Ozon • Yandex Market • Честный ЗНАК");
        content.Controls.Add(BuildPageHeader("Tổng quan", "Dữ liệu tổng hợp nhiều sàn"));

        var cards = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 132, WrapContents = false, BackColor = Bg };
        cards.Controls.Add(MetricCard("Sản phẩm", "1 248", "Đã đồng bộ"));
        cards.Controls.Add(MetricCard("Đơn FBS chờ", "54", "WB 28 • Ozon 17 • YM 9"));
        cards.Controls.Add(MetricCard("KIZ hợp lệ", "3 187", "63 mã cần kiểm tra"));
        cards.Controls.Add(MetricCard("Job đang chạy", "4", "Không có lỗi nghiêm trọng"));
        content.Controls.Add(cards);
    }

    private Control MetricCard(string caption, string value, string detail)
    {
        var p = new RoundedPanel { Radius = 14, BorderColor = Border, BorderWidth = 1, BackColor = Surface, Width = 260, Height = 112, Margin = new Padding(0, 0, 14, 0) };
        p.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = TextMuted, Location = new Point(18, 15) });
        p.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 22, FontStyle.Bold), Location = new Point(16, 37) });
        p.Controls.Add(new Label { Text = detail, AutoSize = true, ForeColor = Accent, Location = new Point(18, 84) });
        return p;
    }

    private DataGridView BuildGrid(params string[] columns)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = false,
            ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, GridColor = Color.FromArgb(238, 242, 246),
            ColumnHeadersHeight = 42, RowTemplate = { Height = 40 }, EnableHeadersVisualStyles = false
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 253);
        g.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Surface;
        g.DefaultCellStyle.ForeColor = TextMain;
        g.DefaultCellStyle.SelectionBackColor = AccentSoft;
        g.DefaultCellStyle.SelectionForeColor = TextMain;
        foreach (var c in columns) g.Columns.Add(Guid.NewGuid().ToString(), c);
        return g;
    }

    private void ShowTablePage(string title, string subtitle, DataGridView grid, params string[] actions)
    {
        SetPage(title, subtitle);
        content.Controls.Add(BuildPageHeader(title, subtitle));

        var card = new RoundedPanel { Dock = DockStyle.Fill, Radius = 14, BackColor = Surface, BorderColor = Border, BorderWidth = 1, Padding = new Padding(16) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, BackColor = Surface };
        foreach (var action in actions) toolbar.Controls.Add(OutlineButton(action, Color.FromArgb(96, 108, 125), Math.Max(96, action.Length * 8 + 30)));
        card.Controls.Add(grid);
        card.Controls.Add(toolbar);
        toolbar.BringToFront();
        content.Controls.Add(card);
        card.BringToFront();
    }

    private void ShowProducts()
    {
        var g = BuildGrid("Marketplace", "Shop", "SKU", "Tên", "Giá", "Trạng thái");
        g.Rows.Add("WB", "WB Shop 01", "BLACK-XL", "Quần nam đen XL", "3 200 ₽", "OK");
        g.Rows.Add("Ozon", "Ozon Store 01", "BLACK-XL", "Брюки мужские", "3 550 ₽", "OK");
        g.Rows.Add("Yandex", "YM Store 01", "BLACK-XL", "Брюки мужские", "3 680 ₽", "OK");
        ShowTablePage("Sản phẩm", "Catalog chuẩn hóa dùng chung cho ba marketplace", g, "Đồng bộ", "Tìm SKU", "Xuất Excel");
    }

    private void ShowPrices()
    {
        var g = BuildGrid("Chọn", "Sàn", "SKU", "Giá hiện tại", "Giá mới", "%", "Trạng thái");
        g.Rows.Add("✓", "WB", "BLACK-XL", "3 200 ₽", "3 520 ₽", "+10%", "Sẵn sàng");
        g.Rows.Add("✓", "Ozon", "BLACK-2XL", "3 300 ₽", "3 630 ₽", "+10%", "Sẵn sàng");
        ShowTablePage("Giá sản phẩm", "Preview • batch • verify • rollback", g, "Tăng %", "Giảm %", "Giá cố định", "Import Excel", "Xem trước", "Cập nhật giá");
    }

    private void ShowCopy()
    {
        var g = BuildGrid("Nguồn", "SKU", "Đích", "Category", "Attributes", "Ảnh", "Kết quả");
        g.Rows.Add("WB Shop 01", "BLACK-XL", "Ozon Store 02", "Matched", "18/20", "7", "Thiếu 2 thuộc tính");
        g.Rows.Add("Ozon Store 01", "NAVY-XL", "Yandex Store 01", "Matched", "20/20", "6", "Sẵn sàng");
        ShowTablePage("Sao chép sản phẩm", "Source → normalized catalog → mapper → destination", g, "Chọn nguồn", "Chọn đích", "Phân tích tương thích", "Sao chép hàng loạt");
    }

    private void ShowFbs()
    {
        var g = BuildGrid("Ưu tiên", "Sàn", "Đơn", "SKU", "SL", "KIZ", "Nhãn", "Trạng thái");
        g.Rows.Add("P1", "Ozon", "0139282-0034-1", "BLACK-XL", "1/1", "Required", "Chưa in", "Chờ đóng");
        g.Rows.Add("P2", "WB", "18473920193", "NAVY-2XL", "1/1", "OK", "Ready", "Đang đóng");
        g.Rows.Add("P4", "Yandex", "YM-920183", "BLACK-3XL", "2/2", "N/A", "Ready", "Sẵn sàng giao");
        ShowTablePage("Đơn hàng FBS", "Packing Station: scan → KIZ → pack → label → shipment", g, "Quét barcode / DataMatrix", "Nhận đơn", "Xác nhận đóng", "In nhãn", "Tạo / giao lô");
    }

    private void ShowZnak()
    {
        var g = BuildGrid("KIZ", "GTIN", "Status", "Crypto", "Owner", "Blocked", "Kết quả");
        g.Rows.Add("010460...21ABC", "04607144353175", "INTRODUCED", "✓", "✓", "Không", "Hợp lệ");
        g.Rows.Add("010460...21XYZ", "04607144353175", "RETIRED", "✓", "✓", "Không", "Không dùng");
        ShowTablePage("Честный ЗНАК / KIZ", "True API + УКЭП qua CryptoPro/Rutoken; không lưu private key/PIN", g, "Kiểm tra kết nối", "Chọn УКЭП", "Quét KIZ", "Kiểm tra hàng loạt", "Kho KIZ", "Tài liệu");
    }

    private void ShowJobs()
    {
        var g = BuildGrid("Job", "Loại", "Sàn", "Tiến độ", "Trạng thái", "Chi tiết");
        g.Rows.Add("#1042", "PRICE_UPDATE", "WB", "483/500", "VERIFYING", "17 item đang chờ");
        g.Rows.Add("#1043", "COPY_PRODUCT", "Ozon", "31/37", "PARTIAL_SUCCESS", "6 item cần retry");
        ShowTablePage("Jobs", "Persisted state machine cho mọi batch/write", g, "Retry lỗi", "Tạm dừng", "Tiếp tục", "Xuất log");
    }

    private void ShowHistory()
    {
        var g = BuildGrid("Thời gian", "Module", "Đối tượng", "Thao tác", "Kết quả");
        g.Rows.Add(DateTime.Now.AddMinutes(-15).ToString("g"), "Giá", "WB / BLACK-XL", "3200 → 3520", "OK");
        g.Rows.Add(DateTime.Now.AddMinutes(-9).ToString("g"), "FBS", "WB order 18473920193", "Sticker printed", "OK");
        ShowTablePage("Lịch sử", "Audit giá, copy, KIZ, packing, chữ ký và shipment", g, "Lọc", "Xuất CSV", "Rollback giá");
    }

    private void ShowStores()
    {
        activeNav = "Cửa hàng / API";
        SetPage("Cài đặt cửa hàng", "Thêm hoặc cập nhật kết nối marketplace");
        content.Controls.Add(BuildPageHeader("Cài đặt cửa hàng", "Kết nối API cho từng cửa hàng"));

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true };
        var card = new RoundedPanel { Radius = 14, BackColor = Surface, BorderColor = Border, BorderWidth = 1, Width = 650, Height = 600, Location = new Point(8, 0), Padding = new Padding(22) };
        host.Controls.Add(card);

        var y = 18;
        card.Controls.Add(LabelAt("Sàn", 18, y, true));
        y += 30;

        var platforms = new FlowLayoutPanel { Location = new Point(18, y), Size = new Size(610, 52), BackColor = Surface, WrapContents = false };
        platforms.Controls.Add(PlatformButton("WB", "Wildberries", Color.FromArgb(170, 20, 173), false));
        platforms.Controls.Add(PlatformButton("OZ", "Ozon", Color.FromArgb(16, 97, 220), true));
        platforms.Controls.Add(PlatformButton("Я", "Yandex Market", Color.FromArgb(255, 215, 0), false));
        card.Controls.Add(platforms);
        y += 76;

        AddFormField(card, ref y, "Tên cửa hàng", "PMP Store Man");
        AddFormField(card, ref y, "Ozon Client-Id", "4319641", "Lấy ở Ozon Seller → Cài đặt → API → Client-Id (chuỗi số)");
        AddFormField(card, ref y, "Ozon Api-Key", "********", "UUID v4 — Ozon Seller → Cài đặt → API → Api-Key");

        var info = new RoundedPanel { Radius = 12, BackColor = Color.FromArgb(242, 247, 255), BorderColor = Color.FromArgb(153, 195, 255), BorderWidth = 1, Location = new Point(18, y), Size = new Size(610, 92) };
        info.Controls.Add(new Label { Text = "Kết nối quảng cáo (tùy chọn)", AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(16, 15) });
        info.Controls.Add(new Label { Text = "Kết nối Ozon Performance để đồng bộ và quản lý chiến dịch quảng cáo.", AutoSize = false, Size = new Size(570, 42), ForeColor = Color.FromArgb(91, 108, 132), Location = new Point(16, 39) });
        card.Controls.Add(info);
        y += 115;

        AddFormField(card, ref y, "Mã kết nối quảng cáo", "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx");

        var save = FilledButton("Lưu kết nối", Accent, Color.White, 126);
        save.Location = new Point(502, y + 6);
        card.Controls.Add(save);

        content.Controls.Add(host);
        host.BringToFront();
    }

    private Button PlatformButton(string badge, string name, Color badgeColor, bool selected)
    {
        var b = new Button
        {
            Text = "   " + badge + "   " + name, Width = 190, Height = 48, FlatStyle = FlatStyle.Flat,
            BackColor = selected ? Color.FromArgb(235, 238, 242) : Color.FromArgb(249, 251, 253),
            ForeColor = selected ? TextMain : Color.FromArgb(168, 176, 189), Margin = new Padding(0, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9)
        };
        b.FlatAppearance.BorderColor = selected ? Color.FromArgb(210, 216, 224) : Border;
        return b;
    }

    private void AddFormField(Control parent, ref int y, string label, string value, string? hint = null)
    {
        parent.Controls.Add(LabelAt(label, 18, y, true));
        y += 29;
        var box = new TextBox { Text = value, Location = new Point(18, y), Width = 610, Height = 36, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10), ForeColor = TextMain };
        parent.Controls.Add(box);
        y += 42;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            parent.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = TextMuted, Location = new Point(18, y), Font = new Font("Segoe UI", 8.5f) });
            y += 31;
        }
        else y += 18;
    }

    private Label LabelAt(string text, int x, int y, bool bold = false) =>
        new() { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = TextMain, Font = new Font("Segoe UI", 9, bold ? FontStyle.Bold : FontStyle.Regular) };

    private Button OutlineButton(string text, Color color, int width)
    {
        var b = new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = color, Margin = new Padding(0, 7, 8, 0), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = color == Danger ? Color.FromArgb(255, 145, 160) : Border;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    private Button FilledButton(string text, Color bg, Color fg, int width)
    {
        var b = new Button { Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = fg, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private void ShowDevices()
    {
        var g = BuildGrid("Thiết bị", "Tên", "Profile", "Trạng thái");
        g.Rows.Add("Scanner", "USB HID Scanner", "Auto detect", "Demo");
        g.Rows.Add("Printer", "XPrinter 58×40", "Label default", "Demo");
        g.Rows.Add("Scale", "COM device", "Optional", "Chưa cấu hình");
        ShowTablePage("Thiết bị", "Scanner, printer 58×40/ZPL/PDF và cân", g, "Quét thiết bị", "In thử", "Lưu station");
    }

    private void ShowSettings()
    {
        SetPage("Cài đặt", "Theme, auto-sign, sync, job limits, API environment");
        content.Controls.Add(BuildPageHeader("Cài đặt", "Thiết lập ứng dụng"));

        var card = new RoundedPanel { Dock = DockStyle.Top, Height = 320, Radius = 14, BackColor = Surface, BorderColor = Border, BorderWidth = 1, Padding = new Padding(24) };
        card.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = TextMain, Font = new Font("Segoe UI", 10.5f),
            Text = "Production checklist\n\n• SQLite + migrations\n• DPAPI secrets\n• WB/Ozon/Yandex adapters\n• True API challenge signing qua CryptoPro\n• Auto-sign whitelist, không lưu PIN/private key\n• Idempotency + verify-after-write\n• Rate limiter riêng từng marketplace\n• Printer/ZPL/PDF pipeline\n• Structured audit log + rollback",
            Location = new Point(24, 24)
        });
        content.Controls.Add(card);
        card.BringToFront();
    }
}

internal sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.Transparent;
    public int BorderWidth { get; set; }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        Resize += (_, _) => UpdateRegion();
    }

    private void UpdateRegion()
    {
        using var path = CreatePath(ClientRectangle, Radius);
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        using var path = CreatePath(rect, Radius);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreatePath(Rectangle r, int radius)
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
