using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GTINSyncWB;
public sealed class Dashboard : Form
{
    private readonly Storage disk=new();private Settings config;private readonly HttpClient media=new(){Timeout=TimeSpan.FromSeconds(8)};
    private readonly CatalogApi catalog;private readonly WbApi wb;
    private readonly WriteProcessor processor;
    private readonly Panel side=new(),body=new(),header=new();
    private readonly Label title=new(),state=new();private readonly ProgressBar bar=new();
    private readonly List<CatalogItem> goods=[];private readonly List<Listing> cards=[];private readonly List<MatchRow> matches=[];
    private CancellationTokenSource? running;private readonly ManualResetEventSlim resume=new(true);
    private bool demo,dark,syncIncomplete;private DateTimeOffset? syncAt;private string page="Tổng quan";
    private DataGridView? progressGrid;private Label? progressSummary;private ProgressBar? progressBar;
    private HashSet<string>? activeJobIds;
    private int matchFilterIndex;private string? matchShopId;
    private readonly Color navy=Color.FromArgb(28,40,60),blue=Color.FromArgb(138,57,230);
    private static readonly Bitmap EmptyPhoto=FallbackPhoto();
    private static Bitmap FallbackPhoto(){var image=new Bitmap(64,64);using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(222,231,240));using var brush=new SolidBrush(Color.FromArgb(70,86,110));graphics.DrawString("Ảnh lỗi",new Font("Segoe UI",8),brush,new RectangleF(2,18,60,30));return image;}
    public Dashboard()
    {
        config=disk.Load();var transport=new ApiTransport();catalog=new(transport);wb=new(transport);
        processor=new WriteProcessor(disk,wb,tokenForShop:id=>Secrets.Reveal(config.Shops.Single(s=>s.Id==id).ProtectedToken),validate:ValidateFresh,changed:UpdateProgress);
        try
        {
            var snapshot=disk.LoadSnapshot();
            if(snapshot!=null){goods.AddRange(snapshot.Goods);cards.AddRange(snapshot.Cards);syncAt=snapshot.CompletedAt;Rebuild();foreach(var m in matches.Where(m=>m.Status==MatchStatus.Exact))m.Status=MatchStatus.Stale;}
        }
        catch{goods.Clear();cards.Clear();matches.Clear();syncAt=null;}
        AutoScaleMode=AutoScaleMode.Dpi;
        Text="GTIN Sync WB 0.4.1";Width=1280;Height=800;MinimumSize=new Size(960,620);StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(245,247,251);
        side.Dock=DockStyle.Left;side.Width=260;side.BackColor=navy;Controls.Add(side);
        header.Dock=DockStyle.Top;header.Height=82;header.BackColor=Color.White;Controls.Add(header);
        title.Text="GTIN Sync WB";title.Font=new Font("Segoe UI Semibold",18);title.AutoSize=true;title.Location=new Point(26,11);header.Controls.Add(title);
        state.Text="Sẵn sàng";state.AutoSize=true;state.Location=new Point(29,53);header.Controls.Add(state);
        bar.Dock=DockStyle.Bottom;bar.Height=5;bar.Style=ProgressBarStyle.Continuous;header.Controls.Add(bar);
        body.Dock=DockStyle.Fill;body.AutoScroll=true;Controls.Add(body);body.BringToFront();
        var brand=new Label{Text="GTIN Sync WB",ForeColor=Color.White,Font=new Font("Segoe UI Semibold",15),Location=new Point(18,24),Size=new Size(230,38),AutoEllipsis=false};side.Controls.Add(brand);
        string[] pages=["Tổng quan","Kết nối API","Danh sách GTIN","Bài đăng WB","Ghép GTIN","Lịch sử","Cài đặt"];
        for(var i=0;i<pages.Length;i++)
        {
            var p=pages[i];var b=new Button{Text=p=="Ghép GTIN"?"Thêm GTIN WB":p,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.White,BackColor=blue,Location=new Point(14,88+i*50),Size=new Size(232,42),Cursor=Cursors.Hand};
            b.FlatAppearance.BorderSize=0;b.Click+=(_,_)=>ShowPage(p);side.Controls.Add(b);
        }
        var theme=new Button{Text="◐  Sáng / tối",ForeColor=Color.White,BackColor=navy,FlatStyle=FlatStyle.Flat,Location=new Point(14,480),Size=new Size(232,40)};
        theme.Click+=(_,_)=>{dark=!dark;config.DarkMode=dark;disk.Save(config);ApplyTheme();ShowPage(page);};side.Controls.Add(theme);
        dark=config.DarkMode;ApplyTheme();
        ShowPage(page);
    }
    private void ApplyTheme(){BackColor=dark?Color.FromArgb(31,40,58):Color.FromArgb(245,247,251);body.BackColor=BackColor;header.BackColor=dark?Color.FromArgb(47,58,79):Color.White;title.ForeColor=dark?Color.White:navy;state.ForeColor=dark?Color.White:navy;}
    private Color TextColor=>dark?Color.White:navy;
    private Panel Section(string heading,int height=0)
    {
        var p=new Panel{Dock=DockStyle.Top,Height=height==0?72:height,Padding=new Padding(20),BackColor=dark?Color.FromArgb(45,57,78):Color.White,Margin=new Padding(0,0,0,10)};
        var l=new Label{Text=heading,Font=new Font("Segoe UI Semibold",12),ForeColor=TextColor,Dock=DockStyle.Top,Height=33};p.Controls.Add(l);return p;
    }
    private void ShowPage(string next)
    {
        page=next;body.SuspendLayout();body.Controls.Clear();body.BackColor=BackColor;body.AutoScroll=next is not ("Tổng quan" or "Ghép GTIN");
        title.Text=next=="Ghép GTIN"?"Thêm GTIN WB":next;switch(next){case "Tổng quan":Overview();break;case "Kết nối API":Connections();break;case "Danh sách GTIN":CatalogPage();break;case "Bài đăng WB":CardsPage();break;case "Ghép GTIN":MatchPage();break;case "Bắt đầu thêm GTIN":ProgressPage();break;case "Lịch sử":HistoryPage();break;case "Cài đặt":SettingsPage();break;}
        body.ResumeLayout();
    }
    private Button Action(string label,EventHandler handler)
    {
        var b=new Button{Text=label,AutoSize=true,Height=35,BackColor=blue,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Margin=new Padding(4),Cursor=Cursors.Hand};b.FlatAppearance.BorderSize=0;b.Click+=handler;return b;
    }
    private FlowLayoutPanel Strip(params Control[] controls)
    {
        var p=new FlowLayoutPanel{Dock=DockStyle.Top,Height=100,WrapContents=true,AutoScroll=true,Padding=new Padding(16,6,0,0),BackColor=BackColor};p.Controls.AddRange(controls);return p;
    }
    private FlowLayoutPanel WrapActions(params Control[] controls)
    {
        var p=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,AutoScroll=true,Padding=new Padding(4),BackColor=BackColor};
        p.Controls.AddRange(controls);return p;
    }
    private TableLayoutPanel ActionGroup(string heading,params Control[] controls)
    {
        var group=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(10,5,10,5),Margin=new Padding(0,5,0,5),BackColor=dark?Color.FromArgb(45,57,78):Color.White};
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        group.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
        group.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        group.Controls.Add(new Label{Text=heading,Dock=DockStyle.Fill,ForeColor=TextColor,Font=new Font("Segoe UI Semibold",10),TextAlign=ContentAlignment.MiddleLeft},0,0);
        group.Controls.Add(WrapActions(controls),0,1);return group;
    }
    private DataGridView Grid(params string[] names)
    {
        var g=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.AllCells,BackgroundColor=dark?Color.FromArgb(35,45,63):Color.White,BorderStyle=BorderStyle.None,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,ScrollBars=ScrollBars.Both,EnableHeadersVisualStyles=false};
        g.DefaultCellStyle.BackColor=dark?Color.FromArgb(45,57,78):Color.White;
        g.DefaultCellStyle.ForeColor=dark?Color.White:navy;
        g.DefaultCellStyle.SelectionBackColor=dark?Color.FromArgb(63,91,137):Color.FromArgb(215,230,255);
        g.DefaultCellStyle.SelectionForeColor=dark?Color.White:navy;
        g.AlternatingRowsDefaultCellStyle.BackColor=dark?Color.FromArgb(32,44,65):Color.FromArgb(244,247,252);
        g.AlternatingRowsDefaultCellStyle.ForeColor=dark?Color.White:navy;
        g.ColumnHeadersDefaultCellStyle.BackColor=dark?Color.FromArgb(31,40,58):Color.FromArgb(238,244,255);
        g.ColumnHeadersDefaultCellStyle.ForeColor=dark?Color.White:navy;
        foreach(var n in names){var column=g.Columns[g.Columns.Add(n,n)];column.MinimumWidth=90;}
        g.ColumnHeadersHeight=44;g.RowTemplate.Height=38;return g;
    }
    private void Overview()
    {
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(16,14,16,12),BackColor=BackColor};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,224));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var stats=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(14,12,14,14),BackColor=dark?Color.FromArgb(45,57,78):Color.White};
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        stats.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
        stats.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        stats.Controls.Add(new Label{Text="Tình trạng dữ liệu",Dock=DockStyle.Fill,ForeColor=TextColor,Font=new Font("Segoe UI Semibold",12),TextAlign=ContentAlignment.MiddleLeft},0,0);
        var labels=new[]{($"{cards.Count}","Bài đăng WB"),($"{goods.Count}","GTIN đã đọc"),($"{matches.Count(x=>x.Status==MatchStatus.Exact)}","Khớp chính xác"),($"{matches.Count(x=>x.Status is not (MatchStatus.Exact or MatchStatus.Existing or MatchStatus.Updated))}","Cần kiểm tra"),($"{matches.Count(x=>x.Status==MatchStatus.Updated)}","Đã cập nhật"),($"{matches.Count(x=>x.Status==MatchStatus.Failed)}","Lỗi")};
        var tiles=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=2};
        for(var col=0;col<3;col++)tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
        for(var row=0;row<2;row++)tiles.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        for(var i=0;i<labels.Length;i++)
        {
            var (value,label)=labels[i];
            var tile=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(4),Padding=new Padding(12,8,8,7),BackColor=dark?Color.FromArgb(58,72,97):Color.FromArgb(238,244,255)};
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            tile.RowStyles.Add(new RowStyle(SizeType.Percent,58));
            tile.RowStyles.Add(new RowStyle(SizeType.Percent,42));
            tile.Controls.Add(new Label{Text=value,Dock=DockStyle.Fill,Font=new Font("Segoe UI Semibold",19),ForeColor=dark?Color.FromArgb(141,187,255):blue,TextAlign=ContentAlignment.MiddleLeft},0,0);
            tile.Controls.Add(new Label{Text=label,Dock=DockStyle.Fill,ForeColor=TextColor,TextAlign=ContentAlignment.MiddleLeft},0,1);
            tiles.Controls.Add(tile,i%3,i/3);
        }
        stats.Controls.Add(tiles,0,1);
        var info=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Padding=new Padding(16),Margin=new Padding(0,12,0,0),BackColor=dark?Color.FromArgb(45,57,78):Color.White};
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.Controls.Add(new Label{Text=demo?"CHẾ ĐỘ MẪU • Dữ liệu chỉ nằm trên máy":syncAt==null?"Chưa đồng bộ dữ liệu thật":$"Lần đọc hoàn tất: {syncAt.Value.LocalDateTime:g} • Bản lưu có thể đã cũ; đồng bộ lại để ghi",Dock=DockStyle.Fill,AutoSize=true,Font=new Font("Segoe UI Semibold",12),ForeColor=TextColor,Margin=new Padding(0,0,0,8)},0,0);
        info.Controls.Add(new Label{Text="Mỗi đợt ghi yêu cầu chọn dòng và bấm Xác nhận thêm GTIN. Khi mất kết nối, bản xem trước được giữ nhưng không thể ghi.",Dock=DockStyle.Fill,AutoSize=true,ForeColor=TextColor,Margin=Padding.Empty},0,1);
        var actions=WrapActions(Action("Nạp dữ liệu mẫu",(_,_)=>LoadDemo()),Action("Đồng bộ và đối chiếu GTIN",async (_,_)=>await Sync()),Action("Hủy tác vụ",(_,_)=>running?.Cancel()));
        layout.Controls.Add(actions,0,0);layout.Controls.Add(stats,0,1);layout.Controls.Add(info,0,2);
        body.Controls.Add(layout);
    }
    private void Connections()
    {
        var p=Section("Wildberries • mỗi cửa hàng có token riêng",300);
        var name=new TextBox{PlaceholderText="Tên cửa hàng",Width=190};var token=new TextBox{PlaceholderText="WB Content API token mới",Width=290,UseSystemPasswordChar=true};
        var select=new ComboBox{Width=240,DropDownStyle=ComboBoxStyle.DropDownList};
        void Refresh(){select.Items.Clear();foreach(var s in config.Shops)select.Items.Add(s.Name+" ["+s.Id[..6]+"]");if(select.Items.Count>0)select.SelectedIndex=0;}
        Refresh();
        var row=Strip(name,token,Action("Lưu cửa hàng",(_,_)=>{if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(token.Text))return;config.Shops.Add(new Shop{Name=name.Text.Trim(),ProtectedToken=Secrets.Protect(token.Text.Trim())});disk.Save(config);token.Clear();Refresh();Notice("Đã lưu khóa WB bằng DPAPI");}));row.Dock=DockStyle.Bottom;p.Controls.Add(row);
        var actions=Strip(select,Action("Kiểm tra WB",async (_,_)=>{if(select.SelectedIndex<0)return;await Run(async ct=>{await wb.Check(Secrets.Reveal(config.Shops[select.SelectedIndex].ProtectedToken),ct);Notice("WB đã phản hồi");});}),Action("Xóa cửa hàng",(_,_)=>{if(select.SelectedIndex<0)return;config.Shops.RemoveAt(select.SelectedIndex);disk.Save(config);Refresh();}),Action("Thay token",(_,_)=>{if(select.SelectedIndex<0||token.TextLength==0)return;config.Shops[select.SelectedIndex].ProtectedToken=Secrets.Protect(token.Text.Trim());disk.Save(config);token.Clear();Notice("Đã thay token WB");}));actions.Dock=DockStyle.Bottom;p.Controls.Add(actions);
        body.Controls.Add(p);
        var nk=Section("Честный Знак / Национальный каталог • khóa riêng theo tổ chức",300);
        var org=new TextBox{PlaceholderText="Tên tổ chức",Text=config.Organization,Width=180};var key=new TextBox{PlaceholderText=config.ProtectedCatalogKey==""?"API Key NK":"Đã lưu ••••••",UseSystemPasswordChar=true,Width=260};var since=new TextBox{Text=config.Since,Width=110};
        nk.Controls.Add(Strip(org,key,new Label{Text="Từ ngày YYYY-MM-DD",AutoSize=true,ForeColor=TextColor,Padding=new Padding(2,9,0,0)},since,Action("Lưu khóa",(_,_)=>{if(key.TextLength==0)return;config.Organization=org.Text;config.Since=since.Text;config.ProtectedCatalogKey=Secrets.Protect(key.Text.Trim());disk.Save(config);key.Clear();key.PlaceholderText="Đã lưu ••••••";})));
        var nkActions=Strip(Action("Kiểm tra NK",async (_,_)=>await Run(async ct=>{await catalog.Check(Secrets.Reveal(config.ProtectedCatalogKey),ct);Notice("NK đã phản hồi");})),Action("Xóa khóa NK",(_,_)=>{config.ProtectedCatalogKey="";disk.Save(config);key.PlaceholderText="API Key NK";}));nkActions.Dock=DockStyle.Bottom;nk.Controls.Add(nkActions);body.Controls.Add(nk);nk.BringToFront();
        var caution=Section("Khóa NK được gửi trong query theo yêu cầu tài liệu của NK. Ứng dụng không ghi URL, khóa hoặc nội dung phản hồi vào log.",72);body.Controls.Add(caution);caution.BringToFront();
    }
    private void CatalogPage()
    {
        var grid=Grid("КОД ТОВАРА","НАИМЕНОВАНИЕ ТОВАРА","Mã mẫu / article","Thương hiệu","Màu","Size","Trạng thái thẻ","Đồng bộ");
        foreach(var x in goods)grid.Rows.Add(x.Gtin,x.Name,x.Model,x.Brand,x.Color,x.Size,x.Accessible?x.Status:"Không có quyền",x.SyncedAt.LocalDateTime.ToString("g"));
        body.Controls.Add(grid);body.Controls.Add(Strip(new Label{Text=demo?"DỮ LIỆU MẪU • KHÔNG PHẢI NK THẬT":$"{goods.Count} GTIN đã đọc",ForeColor=TextColor,AutoSize=true}));
    }
    private void CardsPage()
    {
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12),BackColor=BackColor};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,104));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,44));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var search=new TextBox{PlaceholderText="Tìm tên, nmID, mã seller hoặc barcode",Width=300};
        var shopFilter=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=180};shopFilter.Items.Add("Tất cả cửa hàng");foreach(var name in cards.Select(x=>x.ShopName).Distinct())shopFilter.Items.Add(name);shopFilter.SelectedIndex=0;
        var syncFilter=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=160};syncFilter.Items.AddRange(new object[]{"Tất cả trạng thái","Dữ liệu mới","Dữ liệu cũ"});syncFilter.SelectedIndex=0;
        var summary=new Label{Dock=DockStyle.Fill,ForeColor=TextColor,TextAlign=ContentAlignment.MiddleLeft};
        var grid=Grid("Chọn","Ảnh","Tên","Article seller (vendorCode)","Màu","Size WB","Barcode WB / GTIN (skus)","Trạng thái","Thao tác","Cửa hàng","nmID","chrtID");
        grid.Columns.RemoveAt(0);grid.Columns.Insert(0,new DataGridViewCheckBoxColumn{Name="Chọn",HeaderText="Chọn",Width=58,MinimumWidth=58});
        grid.Columns.RemoveAt(1);grid.Columns.Insert(1,new DataGridViewImageColumn{Name="Ảnh",HeaderText="Ảnh",ImageLayout=DataGridViewImageCellLayout.Zoom,Width=64,MinimumWidth=64});
        grid.Columns.RemoveAt(8);grid.Columns.Insert(8,new DataGridViewButtonColumn{Name="Thao tác",HeaderText="Thao tác",Text="Chi tiết",UseColumnTextForButtonValue=true,Width=95});
        grid.ReadOnly=false;foreach(DataGridViewColumn column in grid.Columns)column.ReadOnly=column.Index!=0;
        grid.RowTemplate.Height=70;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        grid.Columns[2].MinimumWidth=240;grid.Columns[6].MinimumWidth=220;grid.Columns[7].MinimumWidth=150;
        void Refresh()
        {
            grid.Rows.Clear();var query=search.Text.Trim();var list=cards.Where(x=>(shopFilter.SelectedIndex==0||x.ShopName==(string)shopFilter.SelectedItem!) && (syncFilter.SelectedIndex==0 || (syncFilter.SelectedIndex==1)==(DateTimeOffset.UtcNow-x.SyncedAt<TimeSpan.FromMinutes(30))) && (query=="" || new[]{x.Title,x.NmId.ToString(),x.VendorCode}.Any(v=>v.Contains(query,StringComparison.OrdinalIgnoreCase)) || Json.A(x.Raw,"sizes").Any(s=>Json.A(s,"skus").Any(v=>v?.ToString().Contains(query,StringComparison.OrdinalIgnoreCase)==true)))).ToList();
            foreach(var x in list)foreach(var size in Json.A(x.Raw,"sizes"))
            {
                var barcode=string.Join("\n",Json.A(size,"skus").Select(z=>z?.ToString()));
                var sizeName=Json.S(size,"techSize");if(sizeName=="")sizeName=Json.S(size,"wbSize");
                var state=string.IsNullOrWhiteSpace(x.Photo)?"Ảnh dự phòng":syncAt==null||DateTimeOffset.UtcNow-syncAt.Value>TimeSpan.FromMinutes(30)?"Dữ liệu cũ":"Đã đồng bộ";
                var index=grid.Rows.Add(false,EmptyPhoto,x.Title,x.VendorCode,Matching.Color(x),sizeName,barcode,state,"Chi tiết",x.ShopName,x.NmId,Json.L(size,"chrtID"));grid.Rows[index].Tag=x;
                if(x.Photo!="")_ = LoadPhoto(grid,index,x.Photo,1,x,7);
            }
            summary.Text=demo?$"DỮ LIỆU MẪU • {grid.Rows.Count} dòng size / {cards.Count} bài đăng":syncIncomplete?"Đồng bộ mới chưa hoàn tất; đang xem bản lưu cũ, không được ghi":syncAt==null?$"Chưa đồng bộ hoàn tất • bản lưu không dùng để ghi; đồng bộ từ Tổng quan":$"Đã tải đầy đủ {cards.Count} sản phẩm lúc {syncAt.Value.LocalDateTime:g} • Hiển thị {grid.Rows.Count} dòng size / {list.Count} bài đăng • {(DateTimeOffset.UtcNow-syncAt.Value>TimeSpan.FromMinutes(30)?"Dữ liệu cũ":"Dữ liệu mới")}";
        }
        search.TextChanged+=(_,_)=>Refresh();shopFilter.SelectedIndexChanged+=(_,_)=>Refresh();syncFilter.SelectedIndexChanged+=(_,_)=>Refresh();
        grid.CellContentClick+=(_,e)=>{if(e.RowIndex>=0&&e.ColumnIndex==8&&grid.Rows[e.RowIndex].Tag is Listing selected)ShowCardDetail(selected);};
        grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0&&grid.Rows[e.RowIndex].Tag is Listing selected)ShowCardDetail(selected);};
        panel.Controls.Add(WrapActions(search,shopFilter,syncFilter,Action("Đồng bộ lại",async (_,_)=>await Sync())),0,0);panel.Controls.Add(summary,0,1);
        if(cards.Count==0)panel.Controls.Add(new Label{Text="Chưa có bài đăng WB. Kết nối token Content, rồi chọn Đồng bộ và đối chiếu GTIN tại Tổng quan.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=TextColor},0,2);
        else panel.Controls.Add(grid,0,2);
        body.Controls.Add(panel);Refresh();
    }
    private void ShowCardDetail(Listing x)
    {
        using var dialog=new Form{Text=$"Bài đăng {x.NmId} • {x.VendorCode}",Width=920,Height=650,StartPosition=FormStartPosition.CenterParent,Font=Font};
        var sizes=Grid("chrtID","techSize","wbSize","skus / barcode của size");sizes.Height=220;sizes.Dock=DockStyle.Top;
        foreach(var size in Json.A(x.Raw,"sizes"))sizes.Rows.Add(Json.S(size,"chrtID"),Json.S(size,"techSize"),Json.S(size,"wbSize"),string.Join(", ",Json.A(size,"skus").Select(y=>y?.ToString())));
        var raw=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Font=new Font("Consolas",9),Text=x.Raw.ToJsonString(new JsonSerializerOptions{WriteIndented=true})};
        dialog.Controls.Add(raw);dialog.Controls.Add(sizes);dialog.Controls.Add(new Label{Text=$"{x.ShopName} • {x.Title} • {Json.S(x.Raw,"brand")} • {Matching.Color(x)}\nMã seller: vendorCode = {x.VendorCode}. skus là barcode của từng size.",Dock=DockStyle.Top,Height=72,Padding=new Padding(12),AutoEllipsis=false});dialog.ShowDialog(this);
    }
    private async Task LoadPhoto(DataGridView grid,int row,string url,int column=0,Listing? expected=null,int statusColumn=-1)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https")return;
        try {var data=await media.GetByteArrayAsync(uri);using var stream=new MemoryStream(data);using var picture=Image.FromStream(stream);if(!grid.IsDisposed && row<grid.Rows.Count && (expected==null||ReferenceEquals(grid.Rows[row].Tag,expected))){grid.Rows[row].Cells[column].Value=new Bitmap(picture);if(statusColumn>=0)grid.Rows[row].Cells[statusColumn].Value="Đã tải ảnh";}}
        catch {if(!grid.IsDisposed && row<grid.Rows.Count && statusColumn>=0 && ReferenceEquals(grid.Rows[row].Tag,expected))grid.Rows[row].Cells[statusColumn].Value="Ảnh lỗi • dùng ảnh dự phòng";}
    }
    private void MatchPage()
    {
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(12,8,12,12),BackColor=BackColor};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,215));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,225));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var message=new Label{Text=(demo?"DỮ LIỆU MẪU • CẤM GHI WB. ":syncIncomplete?"Đồng bộ mới chưa hoàn tất • bản cũ không được ghi. ":syncAt!=null?$"Lần đọc: {syncAt.Value.LocalDateTime:g}. ":"")+"Thao tác chỉ thêm barcode vào size; WB không cho thay hoặc xóa mã cũ.",Dock=DockStyle.Fill,ForeColor=TextColor,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};
        var grid=Grid("Chọn","Ảnh","Cửa hàng","Tên","nmID","Mã seller","Mã mẫu NK","Màu WB","Màu NK","Size WB","Size NK","chrtID","Barcode hiện có","GTIN đề xuất","Thẻ NK","Lý do cụ thể","Trạng thái");
        grid.ReadOnly=false;grid.Columns.RemoveAt(0);grid.Columns.Insert(0,new DataGridViewCheckBoxColumn{Name="Chọn",HeaderText="Chọn",Width=55,MinimumWidth=55});
        grid.Columns.RemoveAt(1);grid.Columns.Insert(1,new DataGridViewImageColumn{Name="Ảnh",HeaderText="Ảnh",ImageLayout=DataGridViewImageCellLayout.Zoom,Width=65,MinimumWidth=65});
        grid.RowTemplate.Height=52;grid.Columns[0].ReadOnly=false;
        for(var i=1;i<grid.Columns.Count;i++)grid.Columns[i].ReadOnly=true;
        foreach(var x in matches)
        {
            var listing=cards.FirstOrDefault(c=>c.ShopId==x.ShopId&&c.NmId==x.NmId);
            var item=goods.FirstOrDefault(g=>g.Gtin==x.Gtin);
            var size=Json.A(listing?.Raw,"sizes").FirstOrDefault(s=>Json.L(s,"chrtID")==x.ChrtId);
            var barcodes=string.Join(", ",Json.A(size,"skus").Select(s=>s?.ToString()));
            var catalogInfo=item==null?"":$"{item.Name} • {item.Status} • {item.Color} / {item.Size}";
            var i=grid.Rows.Add(x.Selected,EmptyPhoto,x.Shop,listing?.Title??"",x.NmId,x.VendorCode,x.CatalogModel,x.Color,x.CatalogColor,x.WbSize,x.CatalogSize,x.ChrtId,barcodes,x.Gtin,catalogInfo,x.Detail,Status(x.Status));
            grid.Rows[i].Tag=x;
            grid.Rows[i].Cells[0].ReadOnly=x.Status!=MatchStatus.Exact;
            if(x.Status is MatchStatus.Multiple or MatchStatus.Conflict or MatchStatus.Unpublished)grid.Rows[i].DefaultCellStyle.BackColor=dark?Color.FromArgb(98,69,45):Color.FromArgb(255,234,203);
            if(listing!=null)_=LoadPhoto(grid,i,listing.Photo,1);
        }
        var count=new Label{AutoSize=true,ForeColor=TextColor,Padding=new Padding(5,9,0,0)};
        void Count(){count.Text=$"Đã chọn: {matches.Count(x=>x.Selected && x.Status==MatchStatus.Exact)} dòng";}
        grid.CellValueChanged+=(_,e)=>{if(e.RowIndex>=0&&e.ColumnIndex==0&&grid.Rows[e.RowIndex].Tag is MatchRow row){row.Selected=row.Status==MatchStatus.Exact && grid.Rows[e.RowIndex].Cells[0].Value is true;Count();}};
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        var filter=new ComboBox{Width=180,DropDownStyle=ComboBoxStyle.DropDownList};filter.Items.AddRange(new object[]{"Tất cả","Khớp chắc chắn","Đã có GTIN","Cần seller xác nhận","Xung đột","Không tìm thấy"});filter.SelectedIndex=matchFilterIndex;
        var shopFilter=new ComboBox{Width=180,DropDownStyle=ComboBoxStyle.DropDownList};shopFilter.Items.Add("Tất cả cửa hàng");var shopIds=matches.Select(x=>(x.ShopId,x.Shop)).Distinct().ToList();foreach(var s in shopIds)shopFilter.Items.Add(s.Shop);shopFilter.SelectedIndex=matchShopId==null?0:Math.Max(0,shopIds.FindIndex(x=>x.ShopId==matchShopId)+1);
        bool Visible(MatchRow m)=> (matchShopId==null||m.ShopId==matchShopId) && (matchFilterIndex switch {0=>true,1=>m.Status==MatchStatus.Exact,2=>m.Status==MatchStatus.Existing,3=>m.Status is MatchStatus.NeedsConfirmation or MatchStatus.AccessDenied or MatchStatus.Unpublished or MatchStatus.Stale or MatchStatus.Review,4=>m.Status is MatchStatus.Conflict or MatchStatus.Multiple,5=>m.Status==MatchStatus.Missing,_=>true});
        void Filter(){foreach(DataGridViewRow r in grid.Rows)if(r.Tag is MatchRow m)r.Visible=Visible(m);}
        filter.SelectedIndexChanged+=(_,_)=>{matchFilterIndex=filter.SelectedIndex;Filter();};
        shopFilter.SelectedIndexChanged+=(_,_)=>{matchShopId=shopFilter.SelectedIndex==0?null:shopIds[shopFilter.SelectedIndex-1].ShopId;Filter();};Filter();Count();
        var model=new TextBox{PlaceholderText="Mã mẫu NK",Width=130};var sizeFrom=new TextBox{PlaceholderText="Size WB (XL)",Width=115};var sizeTo=new TextBox{PlaceholderText="Size NK (48)",Width=115};
        var colorFrom=new TextBox{PlaceholderText="Màu WB",Width=115};var colorTo=new TextBox{PlaceholderText="Màu NK",Width=115};
        var exceptionGtin=new TextBox{PlaceholderText="GTIN NK ngoại lệ",Width=175};
        var rules=ActionGroup("Ngoại lệ cho dòng đang chọn (chỉ cần khi không tự khớp)",model,sizeFrom,sizeTo,colorFrom,colorTo,Action("Lưu ánh xạ",(_,_)=>{if(grid.CurrentRow?.Tag is not MatchRow m)return;if(model.TextLength>0)config.ProductRules[m.ShopId+":"+m.VendorCode]=model.Text.Trim();var actual=config.ProductRules.GetValueOrDefault(m.ShopId+":"+m.VendorCode,m.VendorCode);var prefix=m.ShopId+":"+Matching.ExactKey(actual)+":";if(sizeFrom.TextLength>0&&sizeTo.TextLength>0)config.SizeRules[prefix+Matching.ExactKey(sizeFrom.Text)]=sizeTo.Text.Trim();if(colorFrom.TextLength>0&&colorTo.TextLength>0)config.ColorRules[prefix+Matching.ExactKey(colorFrom.Text)]=colorTo.Text.Trim();disk.Save(config);Rebuild();ShowPage("Ghép GTIN");}),exceptionGtin,Action("Xác nhận cặp ngoại lệ",(_,_)=>
        {
            if(grid.CurrentRow?.Tag is not MatchRow m)return;
            var item=goods.SingleOrDefault(x=>x.Gtin==exceptionGtin.Text.Trim());
            if(item==null||!item.Accessible||!item.TradeUnit||item.Status!="published"||!Gtin.IsValid(item.Gtin)){Notice("GTIN NK không tồn tại, không đủ quyền, chưa công bố hoặc không thuộc đơn vị hàng");return;}
            if(MessageBox.Show(this,$"Xác nhận ngoại lệ cho {m.Shop} • {m.VendorCode} • {m.Color} • {m.WbSize} • chrtID {m.ChrtId}\nNK: {item.Model} • {item.Color} • {item.Size} • GTIN {item.Gtin}\n\nThao tác này chỉ lưu cách đối chiếu; chưa gửi WB.","Xác nhận cặp ngoại lệ",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            config.ProductRules[m.ShopId+":"+m.VendorCode]=item.Model;
            var prefix=m.ShopId+":"+Matching.ExactKey(item.Model)+":";
            config.ColorRules[prefix+Matching.ExactKey(m.Color)]=item.Color;
            config.SizeRules[prefix+Matching.ExactKey(m.WbSize)]=item.Size;
            config.GtinRules[m.ShopId+":"+m.NmId+":"+m.ChrtId]=item.Gtin;
            disk.Save(config);Rebuild();ShowPage("Ghép GTIN");Notice("Đã lưu cặp ngoại lệ; xem lại trạng thái và lý do trước khi chọn");
        }));
        var actions=ActionGroup("Lọc, chọn và xem trước",shopFilter,filter,Action("Chọn tất cả dòng khớp chắc chắn",(_,_)=>{MatchSelection.SelectExact(matches,matchShopId,matchFilterIndex==0?null:matchFilterIndex==1?MatchStatus.Exact:MatchStatus.Missing);ShowPage("Ghép GTIN");}),Action("Bỏ chọn tất cả",(_,_)=>{MatchSelection.Clear(matches);ShowPage("Ghép GTIN");}),count,Action("Xem trước cập nhật",(_,_)=>ShowPreview()),Action("Tiếp tục tác vụ đã xác nhận",async (_,_)=>{resume.Set();ShowPage("Bắt đầu thêm GTIN");await RunJobs();}),Action("Xuất CSV / XLSX",(_,_)=>ExportMatches()));
        var area=new Panel{Dock=DockStyle.Fill,BackColor=BackColor};
        if(matches.Count==0)area.Controls.Add(new Label{Text="Chưa có dữ liệu đối chiếu.\nVào Tổng quan → Nạp dữ liệu mẫu để xem thử, hoặc kết nối hai API rồi chọn Đồng bộ dữ liệu thật.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=TextColor,Font=new Font("Segoe UI",12),Padding=new Padding(25)});
        else area.Controls.Add(grid);
        layout.Controls.Add(message,0,0);layout.Controls.Add(actions,0,1);layout.Controls.Add(rules,0,2);layout.Controls.Add(area,0,3);
        body.Controls.Add(layout);
    }
    private static string Status(MatchStatus x)=>x switch {MatchStatus.Exact=>"Khớp chắc chắn",MatchStatus.Existing=>"Đã có GTIN",MatchStatus.Missing=>"Không tìm thấy",MatchStatus.Multiple=>"Xung đột • nhiều GTIN",MatchStatus.Conflict=>"Xung đột",MatchStatus.NeedsConfirmation=>"Cần seller xác nhận",MatchStatus.Unpublished=>"Cần seller xác nhận • chưa công bố",MatchStatus.AccessDenied=>"Cần seller xác nhận • thiếu quyền",MatchStatus.Stale=>"Dữ liệu cũ",MatchStatus.Updated=>"Đã thêm xong",MatchStatus.Queued=>"Đang chờ",MatchStatus.Sending=>"Đang gửi lên WB",MatchStatus.Received or MatchStatus.Verifying=>"WB đã nhận, đang kiểm tra",MatchStatus.Unknown=>"Chưa rõ kết quả",MatchStatus.Review=>"Cần kiểm tra",_=>"Lỗi"};
    private void HistoryPage()
    {
        var grid=Grid("Thời gian","Cửa hàng","nmID","chrtID","GTIN","Kết quả","Chi tiết");foreach(var x in disk.History().OrderByDescending(x=>x.At))grid.Rows.Add(x.At.LocalDateTime.ToString("g"),x.Shop,x.NmId,x.ChrtId,x.Gtin,x.Result,x.Detail);
        foreach(var job in disk.LoadJobs().Where(j=>j.Status is not (WriteState.Success or WriteState.Review or WriteState.Failed)))foreach(var line in job.Lines)grid.Rows.Add(job.ConfirmedAt.LocalDateTime.ToString("g"),job.ShopName,job.NmId,line.ChrtId,line.Gtin,Status(job.Status switch{WriteState.Queued=>MatchStatus.Queued,WriteState.Sending=>MatchStatus.Sending,WriteState.Received=>MatchStatus.Received,WriteState.Verifying=>MatchStatus.Verifying,_=>MatchStatus.Unknown}),line.Detail);
        body.Controls.Add(grid);body.Controls.Add(Strip(Action("Xuất CSV / XLSX",(_,_)=>ExportHistory())));
    }
    private void SettingsPage()
    {
        var status=Section("Cấu hình cục bộ và trạng thái dữ liệu",160);
        status.Controls.Add(new Label{Text=$"Thư mục dữ liệu: {disk.Folder}\nLần đồng bộ hoàn tất: {(syncAt==null?"Chưa có":syncAt.Value.LocalDateTime.ToString("g"))}\nTác vụ đang chờ hoặc chưa rõ kết quả: {disk.LoadJobs().Count(j=>j.Status is WriteState.Queued or WriteState.Sending or WriteState.Received or WriteState.Verifying or WriteState.Unknown)}\nChế độ sáng/tối được lưu riêng trên máy này.",Dock=DockStyle.Fill,Padding=new Padding(20,40,10,0),ForeColor=TextColor});
        body.Controls.Add(status);
        body.Controls.Add(Strip(Action("Tiếp tục tác vụ đã xác nhận",async (_,_)=>await RunJobs())));
    }
    private void Rebuild(){matches.Clear();matches.AddRange(Matching.Build(goods,cards,config));ApplyJobs();}
    private void ApplyJobs()
    {
        foreach(var job in disk.LoadJobs())foreach(var line in job.Lines)
        {
            var row=matches.FirstOrDefault(m=>m.ShopId==job.ShopId&&m.NmId==job.NmId&&m.ChrtId==line.ChrtId&&m.Gtin==line.Gtin);
            if(row==null)continue;
            row.Status=job.Status switch{WriteState.Queued=>MatchStatus.Queued,WriteState.Sending=>MatchStatus.Sending,WriteState.Received=>MatchStatus.Received,WriteState.Verifying=>MatchStatus.Verifying,WriteState.Unknown=>MatchStatus.Unknown,WriteState.Success=>MatchStatus.Updated,WriteState.Review=>MatchStatus.Review,_=>MatchStatus.Failed};
            row.Detail=line.Detail;
        }
    }
    private void LoadDemo()
    {
        demo=true;goods.Clear();cards.Clear();
        // A valid check digit is computed for the local fixture.
        goods.Add(new("00000000000017","PANTS-01","Quần nam mẫu","Mẫu","đen","48","published",DateTimeOffset.UtcNow,"DỮ LIỆU MẪU"));
        var shop=new Shop{Name="Cửa hàng mẫu"};
        var raw=JsonNode.Parse("""{"nmID":123456,"vendorCode":"PANTS-01","title":"Quần nam mẫu","brand":"Mẫu","description":"Mẫu cục bộ","dimensions":{"length":30,"width":20,"height":4,"weightBrutto":0.5},"characteristics":[{"id":1,"name":"Цвет","value":["đen"]}],"sizes":[{"chrtID":456789,"techSize":"48","wbSize":"48","skus":["1234567890128"]}]}""")!.AsObject();
        cards.Add(new(shop.Id,shop.Name,123456,"PANTS-01","Quần nam mẫu","đen","",raw,DateTimeOffset.UtcNow));Rebuild();ShowPage("Tổng quan");
    }
    public void CaptureForCi(string folder)
    {
        Directory.CreateDirectory(folder);
        void Save(string name){PerformLayout();Refresh();using var image=new Bitmap(Width,Height);DrawToBitmap(image,new Rectangle(Point.Empty,Size));image.Save(Path.Combine(folder,name+".png"));}
        Size=new Size(1024,768);ShowPage("Tổng quan");Save("Tong-quan-sang-100");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-trong-100");
        LoadDemo();ShowPage("Ghép GTIN");Save("Ghep-GTIN-mau-100");
        ShowPage("Danh sách GTIN");Save("Danh-sach-GTIN-mau-100");
        dark=true;BackColor=Color.FromArgb(31,40,58);body.BackColor=BackColor;header.BackColor=Color.FromArgb(47,58,79);title.ForeColor=Color.White;state.ForeColor=Color.White;
        ShowPage("Tổng quan");Save("Tong-quan-toi-100");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-100");
        ShowPage("Bài đăng WB");Save("Bai-dang-WB-toi-100");
        WindowState=FormWindowState.Maximized;Application.DoEvents();
        ShowPage("Tổng quan");Save("Tong-quan-toi-phong-to");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-phong-to");
        ShowPage("Bài đăng WB");Save("Bai-dang-WB-toi-phong-to");
        WindowState=FormWindowState.Normal;Application.DoEvents();
        Size=new Size(1280,800);Scale(new SizeF(1.25f,1.25f));ShowPage("Tổng quan");Save("Tong-quan-toi-scale125");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-scale125");
        ShowPage("Bài đăng WB");Save("Bai-dang-WB-toi-scale125");
        ShowPage("Kết nối API");Save("Ket-noi-API-toi-scale125");
    }
    private async Task Sync()
    {
        if(config.ProtectedCatalogKey==""||config.Shops.Count==0){Notice("Cần lưu khóa NK và ít nhất một cửa hàng WB");return;}
        await Run(async ct=>
        {
            syncIncomplete=true;
            Notice("Đang đọc dữ liệu");bar.Value=5;
            if(!DateTime.TryParse(config.Since,out var since))throw new InvalidOperationException("Ngày bắt đầu không hợp lệ");
            var freshGoods=await catalog.Read(Secrets.Reveal(config.ProtectedCatalogKey),since,DateTime.UtcNow.AddDays(1),new Progress<string>(Notice),ct);
            var freshCards=new List<Listing>();
            foreach(var shop in config.Shops){ct.ThrowIfCancellationRequested();freshCards.AddRange(await wb.Read(shop,Secrets.Reveal(shop.ProtectedToken),ct,new Progress<int>(count=>Notice($"Đang đọc WB: {shop.Name} • đã tải {count} thẻ; chưa hoàn tất"))));Notice($"Đã đọc xong WB: {shop.Name} ({freshCards.Count} thẻ cộng dồn)");}
            var completed=DateTimeOffset.UtcNow;
            disk.SaveSnapshot(new ReadSnapshot{CompletedAt=completed,Goods=freshGoods,Cards=freshCards});
            goods.Clear();goods.AddRange(freshGoods);cards.Clear();cards.AddRange(freshCards);syncAt=completed;demo=false;syncIncomplete=false;bar.Value=80;
            Notice("Đang đối chiếu");Rebuild();bar.Value=100;ShowPage("Tổng quan");Notice($"Đã đối chiếu {matches.Count} dòng; cần seller xem và xác nhận");
        });
    }
    private void ShowPreview()
    {
        if(demo){Notice("Dữ liệu mẫu không được ghi lên WB");return;}
        if(syncIncomplete || syncAt==null || DateTimeOffset.UtcNow-syncAt.Value>TimeSpan.FromMinutes(30))
        {Notice("Bản xem trước đã cũ hoặc đồng bộ chưa hoàn tất; đồng bộ lại trước khi xác nhận");return;}
        var chosen=matches.Where(x=>x.Selected).ToList();
        var selected=MatchSelection.Revalidate(chosen,goods,cards,config);
        if(chosen.Count!=selected.Count)Notice($"Đã loại {chosen.Count-selected.Count} dòng không còn chắc chắn; xem lý do ở bảng đối chiếu");
        if(selected.Count==0){Notice("Không còn dòng Khớp chắc chắn được chọn");ShowPage("Ghép GTIN");return;}
        using var preview=new Form{Text="Xem trước cập nhật • chưa gửi WB",Width=1050,Height=620,MinimumSize=new Size(800,460),StartPosition=FormStartPosition.CenterParent,Font=Font};
        var grid=Grid("Ảnh nhỏ","Cửa hàng","Mã seller (vendorCode)","Màu","Size WB","chrtID","GTIN sẽ thêm","Trạng thái");
        grid.Columns.RemoveAt(0);grid.Columns.Insert(0,new DataGridViewImageColumn{Name="Ảnh",HeaderText="Ảnh nhỏ",ImageLayout=DataGridViewImageCellLayout.Zoom,Width=65});grid.RowTemplate.Height=52;
        foreach(var row in selected)
        {
            var listing=cards.Single(c=>c.ShopId==row.ShopId&&c.NmId==row.NmId);
            var index=grid.Rows.Add(EmptyPhoto,row.Shop,row.VendorCode,row.Color,row.WbSize,row.ChrtId,row.Gtin,Status(row.Status));grid.Rows[index].Tag=row;
            _=LoadPhoto(grid,index,listing.Photo);
        }
        grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0&&grid.Rows[e.RowIndex].Tag is MatchRow row)ShowCardDetail(cards.Single(c=>c.ShopId==row.ShopId&&c.NmId==row.NmId));};
        var count=new Label{Text=$"{selected.Select(x=>(x.ShopId,x.NmId)).Distinct().Count()} bài đăng • {selected.Count} size • {selected.Select(x=>x.Gtin).Distinct().Count()} GTIN. Chỉ thêm barcode; WB không cho thay hoặc xóa mã cũ.",Dock=DockStyle.Top,Height=52,Padding=new Padding(12),AutoEllipsis=false};
        Button? confirm=null;confirm=Action("Xác nhận thêm GTIN",async (_,_)=>{confirm!.Enabled=false;preview.Close();await Commit(selected);});
        var actions=Strip(confirm,Action("Quay lại đối chiếu",(_,_)=>preview.Close()));actions.Dock=DockStyle.Bottom;
        preview.Controls.Add(grid);preview.Controls.Add(count);preview.Controls.Add(actions);preview.ShowDialog(this);
        if(page=="Ghép GTIN")ShowPage("Ghép GTIN");
    }
    private async Task Commit(List<MatchRow> selected)
    {
        if(demo || syncIncomplete || syncAt==null || DateTimeOffset.UtcNow-syncAt.Value>TimeSpan.FromMinutes(30)){Notice("Dữ liệu đã cũ hoặc đồng bộ chưa hoàn tất; đồng bộ lại trước khi ghi");return;}
        var valid=MatchSelection.Revalidate(selected,goods,cards,config);
        if(valid.Count!=selected.Count){Notice($"Đã loại {selected.Count-valid.Count} dòng không còn chắc chắn; xem lại trước khi xác nhận");ShowPage("Ghép GTIN");return;}
        selected=valid;
        var pending=disk.LoadJobs();
        if(selected.Any(row=>pending.Any(job=>job.ShopId==row.ShopId&&job.NmId==row.NmId&&job.Status is not (WriteState.Success or WriteState.Review or WriteState.Failed))))
        {Notice("Bài đăng đã có tác vụ chờ; tiếp tục hoặc xử lý tác vụ đó trước");return;}
        var groups=selected.GroupBy(x=>(x.ShopId,x.NmId)).ToList();
        var created=new List<WriteJob>();
        foreach(var group in groups)
        {
            var listing=cards.Single(c=>c.ShopId==group.Key.ShopId&&c.NmId==group.Key.NmId);
            var job=WriteJob.FromRows(listing,group);pending.Add(job);created.Add(job);
        }
        disk.SaveJobs(pending);
        activeJobIds=created.Select(x=>x.Id).ToHashSet();ApplyJobs();ShowPage("Bắt đầu thêm GTIN");
        await RunJobs();
    }
    private string? ValidateFresh(Listing fresh,WriteJob job)
    {
        if(syncIncomplete || syncAt==null || demo || DateTimeOffset.UtcNow-syncAt.Value>TimeSpan.FromMinutes(30))return "Bản đồng bộ đã cũ hoặc chưa hoàn tất";
        var current=cards.Select(c=>c.ShopId==fresh.ShopId&&c.NmId==fresh.NmId?fresh:c).ToList();
        var match=Matching.Build(goods,current,config);
        foreach(var line in job.Lines)
        {
            var candidate=match.SingleOrDefault(m=>m.ShopId==job.ShopId&&m.NmId==job.NmId&&m.ChrtId==line.ChrtId);
            if(candidate?.Status!=MatchStatus.Exact||candidate.Gtin!=line.Gtin || Matching.ExactKey(candidate.Color)!=Matching.ExactKey(line.Color) || Matching.ExactKey(candidate.WbSize)!=Matching.ExactKey(line.WbSize))return candidate?.Detail??"Không còn đúng nmID, chrtID, màu, size và GTIN";
        }
        return null;
    }
    private void ProgressPage()
    {
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(12),BackColor=BackColor};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,72));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        progressSummary=new Label{Dock=DockStyle.Fill,ForeColor=TextColor,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(8),Font=new Font("Segoe UI Semibold",11)};
        progressBar=new ProgressBar{Dock=DockStyle.Fill,Margin=new Padding(8,14,8,14)};
        progressGrid=Grid("Mã seller","Màu","Size","GTIN","Kết quả","Nguyên nhân");progressGrid.RowTemplate.Height=42;
        var actions=WrapActions(Action("Tạm dừng",(_,_)=>{resume.Reset();Notice("Đã tạm dừng sau yêu cầu đang xử lý");}),Action("Tiếp tục",(_,_)=>resume.Set()),Action("Lịch sử",(_,_)=>ShowPage("Lịch sử")),Action("Xuất báo cáo",(_,_)=>ExportHistory()));
        layout.Controls.Add(progressSummary,0,0);layout.Controls.Add(progressBar,0,1);layout.Controls.Add(actions,0,2);layout.Controls.Add(progressGrid,0,3);body.Controls.Add(layout);RefreshProgress();
    }
    private void UpdateProgress(WriteJob job){if(page=="Bắt đầu thêm GTIN"&&IsHandleCreated)BeginInvoke(new Action(RefreshProgress));}
    private void RefreshProgress()
    {
        if(progressGrid==null||progressGrid.IsDisposed||progressSummary==null||progressBar==null)return;
        var jobs=disk.LoadJobs().Where(x=>activeJobIds==null?x.Status is not (WriteState.Success or WriteState.Review or WriteState.Failed):activeJobIds.Contains(x.Id)).ToList();
        var lines=jobs.SelectMany(j=>j.Lines.Select(l=>(Job:j,Line:l))).ToList();progressGrid.Rows.Clear();
        foreach(var (job,line) in lines)
        {
            var index=progressGrid.Rows.Add(job.VendorCode,line.Color,line.WbSize,line.Gtin,ProgressStatus(line.Status),line.Detail);
            if(line.Status==WriteState.Success)progressGrid.Rows[index].DefaultCellStyle.BackColor=dark?Color.FromArgb(38,100,70):Color.FromArgb(218,246,226);
            if(line.Status is WriteState.Failed or WriteState.Review or WriteState.Unknown)progressGrid.Rows[index].DefaultCellStyle.BackColor=dark?Color.FromArgb(105,62,55):Color.FromArgb(255,228,218);
        }
        var done=lines.Count(x=>x.Line.Status is WriteState.Success or WriteState.Review or WriteState.Failed);
        progressBar.Value=lines.Count==0?0:Math.Min(100,100*done/lines.Count);
        var prior=lines.Count(x=>x.Job.SubmittedAt==null&&x.Line.Status==WriteState.Success);
        progressSummary.Text=$"Bắt đầu thêm GTIN • {progressBar.Value}% • {done}/{lines.Count} dòng\nĐã thêm xong: {lines.Count(x=>x.Line.Status==WriteState.Success)-prior} • Đã có từ trước: {prior} • Cần kiểm tra: {lines.Count(x=>x.Line.Status is WriteState.Review or WriteState.Unknown)} • Lỗi: {lines.Count(x=>x.Line.Status==WriteState.Failed)}";
    }
    private static string ProgressStatus(WriteState status)=>status switch{WriteState.Queued=>"Đang chờ",WriteState.Sending=>"Đang gửi lên WB",WriteState.Received or WriteState.Verifying=>"WB đã nhận, đang kiểm tra",WriteState.Success=>"Đã thêm xong",WriteState.Review or WriteState.Unknown=>"Cần kiểm tra",_=>"Lỗi"};
    private async Task RunJobs()
    {
        await Run(async ct=>
        {
            var jobs=disk.LoadJobs().Where(j=>j.Status is WriteState.Queued or WriteState.Sending or WriteState.Received or WriteState.Verifying or WriteState.Unknown).ToList();
            if(jobs.Count==0){Notice("Không có tác vụ đã xác nhận cần tiếp tục");return;}
            var completed=0;
            foreach(var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                while(!resume.IsSet)await Task.Delay(200,ct);
                if(!config.Shops.Any(s=>s.Id==job.ShopId)){job.Mark(WriteState.Review,"Cửa hàng đã bị xóa; cần kiểm tra lại");disk.SaveJobs(disk.LoadJobs().Select(x=>x.Id==job.Id?job:x));continue;}
                Notice($"Đang cập nhật WB: {completed}/{jobs.Count} bài đăng");
                var before=job.Status;
                await processor.Process(job,ct);
                if(before!=job.Status && job.Status is WriteState.Success or WriteState.Review or WriteState.Failed)
                    foreach(var line in job.Lines)disk.Append(new(DateTimeOffset.UtcNow,job.ShopName,job.NmId,line.ChrtId,line.Gtin,job.Status.ToString(),line.Detail));
                completed++;bar.Value=Math.Min(100,completed*100/jobs.Count);ApplyJobs();RefreshProgress();
            }
            Notice($"Đã xử lý {completed}/{jobs.Count} bài đăng; xem trạng thái từng dòng");
        });
    }
    private async Task Run(Func<CancellationToken,Task> work)
    {
        if(running!=null){Notice("Một tác vụ đang chạy");return;}
        running=new CancellationTokenSource();try{await work(running.Token);}catch(OperationCanceledException){Notice("Đã hủy. Dữ liệu đọc trước đó được giữ; cần đồng bộ lại trước khi ghi");foreach(var m in matches.Where(m=>m.Status==MatchStatus.Exact))m.Status=MatchStatus.Stale;}
        catch(Exception ex){Notice(SafeError(ex));foreach(var m in matches.Where(m=>m.Status==MatchStatus.Exact))m.Status=MatchStatus.Stale;}
        finally{running.Dispose();running=null;resume.Set();}
    }
    private static string SafeError(Exception e)=>e is ApiFailure or InvalidOperationException?e.Message:"Lỗi kết nối hoặc xử lý; thử lại, không ghi khóa vào báo cáo";
    private void Notice(string message){if(InvokeRequired){BeginInvoke(new Action(()=>Notice(message)));return;}state.Text=message;}
    private void ExportMatches()
    {
        string[] headers=["Cửa hàng","nmID","Mã seller","Màu","Size","chrtID","GTIN","Nguồn","Trạng thái","Chi tiết"];
        var rows=matches.Select(x=>(IReadOnlyList<string>)new[]{x.Shop,x.NmId.ToString(),x.VendorCode,x.Color,x.WbSize,x.ChrtId.ToString(),x.Gtin,x.Source,Status(x.Status),x.Detail}).ToList();SaveReport(headers,rows);
    }
    private void ExportHistory()
    {
        string[] headers=["Thời gian","Cửa hàng","nmID","chrtID","GTIN","Kết quả","Chi tiết"];
        var rows=disk.History().Select(x=>(IReadOnlyList<string>)new[]{x.At.ToString("O"),x.Shop,x.NmId.ToString(),x.ChrtId.ToString(),x.Gtin,x.Result,x.Detail}).ToList();
        rows.AddRange(disk.LoadJobs().Where(x=>x.Status is not (WriteState.Success or WriteState.Review or WriteState.Failed)).SelectMany(job=>job.Lines.Select(line=>(IReadOnlyList<string>)new[]{job.ConfirmedAt.ToString("O"),job.ShopName,job.NmId.ToString(),line.ChrtId.ToString(),line.Gtin,job.Status.ToString(),line.Detail})));
        SaveReport(headers,rows);
    }
    private static string Csv(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
    private void SaveReport(IReadOnlyList<string> headers,IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var d=new SaveFileDialog{Filter="Excel XLSX|*.xlsx|CSV UTF-8|*.csv",FileName="GTIN-Sync-WB-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".xlsx"};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        if(d.FilterIndex==1)ReportWriter.Xlsx(d.FileName,headers,rows);
        else File.WriteAllLines(d.FileName,new[]{headers}.Concat(rows).Select(r=>string.Join(',',r.Select(Csv))),new UTF8Encoding(true));
    }
}
