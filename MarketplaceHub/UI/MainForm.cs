using MarketplaceHub.Core;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed class MainForm : Form
{
    private readonly AppServices app;
    private readonly Panel work = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 246, 249), Padding = new Padding(12) };
    private readonly RichTextBox log = new()
    {
        Dock = DockStyle.Bottom, Height = 92, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(27, 31, 38), ForeColor = Color.FromArgb(210, 218, 228),
        Font = new Font("Consolas", 9), DetectUrls = false
    };
    private readonly ComboBox storePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly Label connection = new() { AutoSize = true, ForeColor = Color.FromArgb(110, 120, 132) };

    private readonly Color side = Color.FromArgb(39, 43, 50);
    private readonly Color side2 = Color.FromArgb(51, 56, 64);
    private readonly Color accent = Color.FromArgb(26, 160, 137);
    private readonly Color border = Color.FromArgb(215, 220, 227);
    private readonly Color text = Color.FromArgb(48, 55, 66);

    public MainForm(AppServices services)
    {
        app = services;
        Text = "Marketplace Hub 0.3.0";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        Size = new Size(1440, 900);
        Font = new Font("Segoe UI", 9);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 218));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildSidebar(), 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        right.Controls.Add(BuildTopbar(), 0, 0);
        right.Controls.Add(work, 0, 1);
        right.Controls.Add(log, 0, 2);
        root.Controls.Add(right, 1, 0);

        RefreshStores();
        ShowFbs();
        WriteLog("Marketplace Hub 0.3.0 started. Database: " + app.Db.DbPath);
    }

    private Control BuildSidebar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = side };
        p.Controls.Add(new Label
        {
            Text = "WCODE STYLE\nMARKETPLACE HUB",
            AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 13, FontStyle.Bold),
            Location = new Point(18, 18)
        });
        p.Controls.Add(new Label
        {
            Text = "WB  •  OZON  •  YANDEX",
            AutoSize = true, ForeColor = Color.FromArgb(155, 165, 178),
            Location = new Point(18, 62)
        });

        var menu = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Location = new Point(0, 94), Size = new Size(218, 690),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = side
        };
        p.Controls.Add(menu);

        AddSection(menu, "WORK");
        AddNav(menu, "FBS PACKING", ShowFbs);
        AddNav(menu, "PRODUCTS", ShowProducts);
        AddNav(menu, "PRICE", ShowPrices);
        AddNav(menu, "COPY LISTING", ShowCopy);

        AddSection(menu, "MARKING");
        AddNav(menu, "KIZ / ЧЕСТНЫЙ ЗНАК", ShowKiz);

        AddSection(menu, "SYSTEM");
        AddNav(menu, "STORES / API", ShowStores);
        AddNav(menu, "OVERVIEW", ShowOverview);
        AddNav(menu, "SETTINGS", ShowSettings);

        return p;
    }

    private Control BuildTopbar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        p.Paint += (_, e) => e.Graphics.DrawLine(new Pen(border), 0, p.Height - 1, p.Width, p.Height - 1);

        p.Controls.Add(new Label { Text = "Store:", AutoSize = true, ForeColor = text, Location = new Point(15, 19) });
        storePicker.Location = new Point(60, 14);
        storePicker.SelectedIndexChanged += (_, _) => connection.Text = "";
        p.Controls.Add(storePicker);

        var test = Button("TEST API", 100);
        test.Location = new Point(320, 10);
        test.Click += async (_, _) => await TestCurrentStore();
        p.Controls.Add(test);

        connection.Location = new Point(433, 19);
        p.Controls.Add(connection);

        var db = new Label
        {
            Text = "LOCAL DB / DPAPI",
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
            Text = title, Width = 205, Height = 30, ForeColor = Color.FromArgb(132, 143, 157),
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(14, 0, 0, 4),
            Font = new Font("Segoe UI", 8, FontStyle.Bold), Margin = Padding.Empty
        });
    }

    private void AddNav(FlowLayoutPanel p, string caption, Action action)
    {
        var b = new Button
        {
            Text = caption, Width = 218, Height = 42, FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0),
            BackColor = side, ForeColor = Color.FromArgb(220, 225, 232),
            Margin = Padding.Empty, Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = side2;
        b.Click += (_, _) =>
        {
            foreach (var x in p.Controls.OfType<Button>()) x.BackColor = side;
            b.BackColor = side2;
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
        if (s is null) { WriteLog("No store configured."); return; }
        connection.Text = "Testing...";
        var r = await app.Api.TestAsync(s);
        connection.Text = r.Success ? "CONNECTED" : "ERROR";
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

    private void ShowOverview()
    {
        Clear();
        work.Controls.Add(Heading("OVERVIEW", "Local database state. No demo data."));
        var stores = app.Db.Stores();
        var grid = Grid("Marketplace", "Store", "Products", "FBS orders");
        foreach (var s in stores)
            grid.Rows.Add(s.Marketplace, s.Name, app.Db.Products(s.Id).Count, app.Db.Orders(s.Id).Count);
        work.Controls.Add(grid);
        grid.BringToFront();
    }

    private void ShowProducts()
    {
        Clear();
        var header = Heading("PRODUCTS", "Sync real catalog from current marketplace API.");
        work.Controls.Add(header);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(244, 246, 249) };
        var sync = Primary("SYNC PRODUCTS", 135);
        toolbar.Controls.Add(sync);
        var filter = new TextBox { Width = 250, PlaceholderText = "Search SKU / name", Margin = new Padding(10, 7, 0, 0) };
        toolbar.Controls.Add(filter);
        work.Controls.Add(toolbar);

        var grid = Grid("External ID", "SKU", "Name", "Price", "Image");
        work.Controls.Add(grid);
        grid.BringToFront();

        void LoadRows()
        {
            grid.Rows.Clear();
            var s = CurrentStore();
            if (s is null) return;
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
        work.Controls.Add(Heading("PRICE", "Select product, calculate new price and send to marketplace."));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 770, BackColor = Color.FromArgb(244, 246, 249) };
        work.Controls.Add(split);
        split.BringToFront();

        var grid = Grid("External ID", "SKU", "Name", "Current price");
        split.Panel1.Controls.Add(grid);

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18) };
        split.Panel2.Controls.Add(panel);

        var y = 15;
        panel.Controls.Add(FieldLabel("Selected SKU", y)); y += 25;
        var sku = new TextBox { Left = 18, Top = y, Width = 300, ReadOnly = true }; panel.Controls.Add(sku); y += 48;
        panel.Controls.Add(FieldLabel("New price (RUB)", y)); y += 25;
        var price = new NumericUpDown { Left = 18, Top = y, Width = 180, DecimalPlaces = 2, Maximum = 100000000, Minimum = 1 };
        panel.Controls.Add(price);
        var percent = new NumericUpDown { Left = 210, Top = y, Width = 108, DecimalPlaces = 2, Minimum = -99, Maximum = 500, Value = 10 };
        panel.Controls.Add(percent);
        y += 36;
        var calc = Button("APPLY %", 100); calc.Left = 210; calc.Top = y; panel.Controls.Add(calc);
        var preview = new Label { Left = 18, Top = y + 7, Width = 180, ForeColor = Color.FromArgb(92, 113, 138), Text = "Preview: -" };
        panel.Controls.Add(preview);
        y += 50;
        var apply = Primary("UPDATE PRICE", 145); apply.Left = 18; apply.Top = y; panel.Controls.Add(apply);

        calc.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            if (!decimal.TryParse(grid.SelectedRows[0].Cells[3].Value?.ToString(), out var current) || current <= 0) return;
            var next = Math.Max(1, Math.Round(current * (1 + percent.Value / 100m), 2));
            price.Value = Math.Min(next, price.Maximum);
            preview.Text = $"{current:0.##} → {next:0.##} RUB";
        };

        var s = CurrentStore();
        if (s != null)
            foreach (var p in app.Db.Products(s.Id)) grid.Rows.Add(p.ExternalId, p.Sku, p.Name, p.Price?.ToString("0.##") ?? "");

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            sku.Text = grid.SelectedRows[0].Cells[1].Value?.ToString() ?? "";
            if (decimal.TryParse(grid.SelectedRows[0].Cells[3].Value?.ToString(), out var v) && v > 0) price.Value = Math.Min(v, price.Maximum);
        };

        apply.Click += async (_, _) =>
        {
            var store = CurrentStore(); if (store is null || string.IsNullOrWhiteSpace(sku.Text)) return;
            var product = app.Db.Product(store.Id, sku.Text); if (product is null) { WriteLog("Product not found in local sync."); return; }
            var confirm = MessageBox.Show($"Update {store.Marketplace} / {product.Sku} to {price.Value:0.##} RUB?", "Confirm price", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            apply.Enabled = false;
            var r = await app.ChangePriceAsync(store, product, price.Value);
            apply.Enabled = true;
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "Accepted" : "Error", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private void ShowCopy()
    {
        Clear();
        work.Controls.Add(Heading("COPY LISTING", "Working same-marketplace copy for WB and Yandex. Cross-market copy is blocked until category mapping is complete."));

        var panel = new Panel { Dock = DockStyle.Top, Height = 190, BackColor = Color.White, Padding = new Padding(16) };
        work.Controls.Add(panel);
        panel.BringToFront();

        var stores = app.Db.Stores().ToList();
        panel.Controls.Add(FieldLabel("Source store", 12));
        var source = new ComboBox { Left = 16, Top = 38, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = stores.ToList(), DisplayMember = nameof(StoreProfile.Name) };
        panel.Controls.Add(source);
        panel.Controls.Add(FieldLabel("Destination store", 72));
        var dest = new ComboBox { Left = 16, Top = 98, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = stores.ToList(), DisplayMember = nameof(StoreProfile.Name) };
        panel.Controls.Add(dest);

        panel.Controls.Add(FieldLabel("Source SKU", 12, 300));
        var sourceSku = new TextBox { Left = 300, Top = 38, Width = 240 }; panel.Controls.Add(sourceSku);
        panel.Controls.Add(FieldLabel("Destination SKU", 72, 300));
        var destSku = new TextBox { Left = 300, Top = 98, Width = 240 }; panel.Controls.Add(destSku);

        var copy = Primary("COPY PRODUCT", 145); copy.Left = 570; copy.Top = 96; panel.Controls.Add(copy);
        copy.Click += async (_, _) =>
        {
            if (source.SelectedItem is not StoreProfile src || dest.SelectedItem is not StoreProfile dst) return;
            var p = app.Db.Product(src.Id, sourceSku.Text.Trim());
            if (p is null) { WriteLog("Source SKU not found. Sync products first."); return; }
            if (string.IsNullOrWhiteSpace(destSku.Text)) { WriteLog("Destination SKU is required."); return; }
            if (MessageBox.Show($"Copy {p.Sku} from {src.Name} to {dst.Name}?", "Confirm copy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            copy.Enabled = false;
            var r = await app.Api.CopySameMarketplaceAsync(src, dst, p, destSku.Text.Trim());
            copy.Enabled = true;
            app.Db.Audit("Copy", r.Success ? "Accepted" : "Failed", $"{src.Name}:{p.Sku}->{dst.Name}:{destSku.Text}:{r.Message}");
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "Copy accepted" : "Copy error", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private void ShowFbs()
    {
        Clear();
        work.Controls.Add(Heading("FBS PACKING", "Scanner-first order packing. Sync real FBS queue and download marketplace labels."));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(244, 246, 249) };
        var sync = Primary("SYNC FBS", 110);
        var scan = new TextBox { Width = 330, Height = 30, PlaceholderText = "Scan order / SKU / DataMatrix...", Margin = new Padding(10, 7, 0, 0) };
        var packBtn = Primary("PACK / READY", 125);
        var labelBtn = Button("DOWNLOAD LABEL", 145);
        toolbar.Controls.Add(sync); toolbar.Controls.Add(scan); toolbar.Controls.Add(packBtn); toolbar.Controls.Add(labelBtn);
        work.Controls.Add(toolbar);

        var grid = Grid("Order", "SKU", "Name", "Qty", "Status", "KIZ");
        work.Controls.Add(grid);
        grid.BringToFront();

        void LoadRows(string q = "")
        {
            grid.Rows.Clear();
            var s = CurrentStore(); if (s is null) return;
            foreach (var o in app.Db.Orders(s.Id))
            {
                if (!string.IsNullOrWhiteSpace(q) &&
                    !o.ExternalOrderId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !o.Sku.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                grid.Rows.Add(o.ExternalOrderId, o.Sku, o.Name, o.Quantity, o.Status, o.NeedsKiz ? "YES" : "");
            }
        }

        sync.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null) return;
            sync.Enabled = false;
            var r = await app.SyncOrdersAsync(s);
            sync.Enabled = true;
            WriteLog(r.Message);
            if (r.Ok) LoadRows();
        };
        scan.TextChanged += (_, _) => LoadRows(scan.Text.Trim());
        scan.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            var p = AppServices.ParseKiz(scan.Text);
            if (p.Ok)
            {
                app.Db.UpsertKiz(scan.Text.Trim(), p.Gtin, "AVAILABLE");
                WriteLog("KIZ scanned: " + p.Gtin);
                scan.Clear();
                e.SuppressKeyPress = true;
            }
        };
        packBtn.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null || grid.SelectedRows.Count == 0) return;
            var orderId = grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            var sku = grid.SelectedRows[0].Cells[1].Value?.ToString() ?? "";
            var order = app.Db.Orders(s.Id).FirstOrDefault(x =>
                x.ExternalOrderId.Equals(orderId, StringComparison.OrdinalIgnoreCase) &&
                x.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase));
            if (order is null) { WriteLog("Selected FBS order not found in local database."); return; }

            if (MessageBox.Show($"Send PACK/READY action for {s.Marketplace} order {order.ExternalOrderId}?", "Confirm FBS action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            packBtn.Enabled = false;
            var result = await app.Api.PackOrderAsync(s, order);
            packBtn.Enabled = true;
            app.Db.Audit("FBS", result.Success ? "PackAccepted" : "PackFailed", $"{s.Marketplace}:{order.ExternalOrderId}:{result.Message}");
            WriteLog(result.Message);
            MessageBox.Show(result.Message, result.Success ? "FBS accepted" : "FBS error", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (result.Success)
            {
                var syncResult = await app.SyncOrdersAsync(s);
                WriteLog(syncResult.Message);
                LoadRows();
            }
        };

        labelBtn.Click += async (_, _) =>
        {
            var s = CurrentStore(); if (s is null || grid.SelectedRows.Count == 0) return;
            var orderId = grid.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            var r = await app.Api.DownloadLabelAsync(s, orderId);
            WriteLog(r.Message + (r.FilePath is null ? "" : " " + r.FilePath));
            if (r.Success && r.FilePath is not null) MessageBox.Show("Saved: " + r.FilePath);
        };
        LoadRows();
        Shown += (_, _) => scan.Focus();
    }

    private void ShowKiz()
    {
        Clear();
        work.Controls.Add(Heading("KIZ / ЧЕСТНЫЙ ЗНАК", "Local KIZ pool + Windows certificate health. No private key or PIN is stored."));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        var input = new TextBox { Width = 470, PlaceholderText = "Paste or scan DataMatrix...", Margin = new Padding(0, 7, 8, 0) };
        var check = Primary("CHECK / SAVE", 125);
        var cert = Button("CERTIFICATES", 125);
        top.Controls.Add(input); top.Controls.Add(check); top.Controls.Add(cert);
        work.Controls.Add(top);

        var grid = Grid("GTIN", "Status", "Assigned", "KIZ code");
        work.Controls.Add(grid);
        grid.BringToFront();

        void LoadRows()
        {
            grid.Rows.Clear();
            foreach (var k in app.Db.Kiz()) grid.Rows.Add(k.Gtin, k.Status, k.Assigned, k.Code);
        }

        check.Click += (_, _) =>
        {
            var p = AppServices.ParseKiz(input.Text);
            WriteLog(p.Message);
            if (!p.Ok) { MessageBox.Show(p.Message, "Invalid KIZ", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            app.Db.UpsertKiz(input.Text.Trim(), p.Gtin, "AVAILABLE");
            input.Clear();
            LoadRows();
        };
        cert.Click += (_, _) =>
        {
            var list = AppServices.Certificates();
            var textCert = list.Count == 0 ? "No certificate in CurrentUser/My." :
                string.Join(Environment.NewLine + Environment.NewLine, list.Take(20).Select(x => $"{x.Subject}\nExpires: {x.NotAfter:d}\nPrivate key: {x.HasPrivateKey}\n{x.Thumbprint}"));
            MessageBox.Show(textCert, "Electronic certificates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        LoadRows();
    }

    private void ShowStores()
    {
        Clear();
        work.Controls.Add(Heading("STORES / API", "Credentials are encrypted with Windows DPAPI CurrentUser."));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 390 };
        work.Controls.Add(split);
        split.BringToFront();

        var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
        split.Panel1.Controls.Add(list);
        var stores = app.Db.Stores().ToList();
        foreach (var s in stores) list.Items.Add(s);

        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true, Padding = new Padding(20) };
        split.Panel2.Controls.Add(p);

        var market = new ComboBox { Left = 20, Top = 42, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<Marketplace>() };
        var name = LabeledBox(p, "Store name", 20, 82, 340);
        p.Controls.Add(new Label { Text = "Marketplace", Left = 20, Top = 20, AutoSize = true, ForeColor = text });
        p.Controls.Add(market);
        name.Top = 93;

        var client = LabeledBox(p, "Ozon Client-Id", 20, 145, 340);
        var apiKey = LabeledBox(p, "API Key (Ozon/Yandex)", 20, 205, 520, true);
        var business = LabeledBox(p, "Yandex Business ID", 20, 265, 340);
        var campaign = LabeledBox(p, "Yandex Campaign ID", 20, 325, 340);
        var token = LabeledBox(p, "Wildberries Token", 20, 385, 520, true);
        var save = Primary("SAVE STORE", 125); save.Left = 20; save.Top = 455; p.Controls.Add(save);
        var test = Button("TEST API", 110); test.Left = 155; test.Top = 455; p.Controls.Add(test);

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
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show("Store name is required."); return; }
            var saved = app.Db.SaveStore(Read());
            WriteLog($"Saved store {saved.Marketplace}/{saved.Name}.");
            RefreshStores(saved.Id);
            ShowStores();
        };
        test.Click += async (_, _) =>
        {
            var temp = Read();
            var r = await app.Api.TestAsync(temp);
            WriteLog(r.Message);
            MessageBox.Show(r.Message, r.Success ? "API OK" : "API error", MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        };
    }

    private TextBox Box(Control parent, string label, int left, int labelTop, int boxTop, int width)
    {
        var t = new TextBox { Left = left, Top = boxTop, Width = width };
        parent.Controls.Add(t);
        return t;
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

    private void ShowSettings()
    {
        Clear();
        work.Controls.Add(Heading("SETTINGS", "Runtime diagnostics."));
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 250,
            Font = new Font("Consolas", 10), BackColor = Color.White,
            Text = $"Version: 0.3.0{Environment.NewLine}Database: {app.Db.DbPath}{Environment.NewLine}OS: {Environment.OSVersion}{Environment.NewLine}.NET: {Environment.Version}{Environment.NewLine}Stores: {app.Db.Stores().Count}{Environment.NewLine}Certificates: {AppServices.Certificates().Count}"
        };
        work.Controls.Add(box);
        box.BringToFront();
    }
}
