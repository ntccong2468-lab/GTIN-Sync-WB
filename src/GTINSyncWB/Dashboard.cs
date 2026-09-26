using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GTINSyncWB;
public sealed class Dashboard : Form
{
    private readonly Storage disk=new();private Settings config;private readonly HttpClient media=new(){Timeout=TimeSpan.FromSeconds(8)};
    private readonly CatalogApi catalog;private readonly WbApi wb;
    private readonly Panel side=new(),body=new(),header=new();
    private readonly Label title=new(),state=new();private readonly ProgressBar bar=new();
    private readonly List<CatalogItem> goods=[];private readonly List<Listing> cards=[];private readonly List<MatchRow> matches=[];
    private CancellationTokenSource? running;private readonly ManualResetEventSlim resume=new(true);
    private bool demo,dark;private string page="Tổng quan";
    private readonly Color navy=Color.FromArgb(20,35,66),blue=Color.FromArgb(42,105,223);
    private static readonly Bitmap EmptyPhoto=new(1,1);
    public Dashboard()
    {
        config=disk.Load();var transport=new ApiTransport();catalog=new(transport);wb=new(transport);
        AutoScaleMode=AutoScaleMode.Dpi;
        Text="GTIN Sync WB 0.2.0";Width=1280;Height=800;MinimumSize=new Size(960,620);StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(245,247,251);
        side.Dock=DockStyle.Left;side.Width=260;side.BackColor=navy;Controls.Add(side);
        header.Dock=DockStyle.Top;header.Height=82;header.BackColor=Color.White;Controls.Add(header);
        title.Text="GTIN Sync WB";title.Font=new Font("Segoe UI Semibold",18);title.AutoSize=true;title.Location=new Point(26,11);header.Controls.Add(title);
        state.Text="Sẵn sàng";state.AutoSize=true;state.Location=new Point(29,53);header.Controls.Add(state);
        bar.Dock=DockStyle.Bottom;bar.Height=5;bar.Style=ProgressBarStyle.Continuous;header.Controls.Add(bar);
        body.Dock=DockStyle.Fill;body.AutoScroll=true;Controls.Add(body);body.BringToFront();
        var brand=new Label{Text="GTIN Sync WB",ForeColor=Color.White,Font=new Font("Segoe UI Semibold",15),Location=new Point(18,24),Size=new Size(230,38),AutoEllipsis=false};side.Controls.Add(brand);
        string[] pages=["Tổng quan","Kết nối API","Danh sách GTIN","Bài đăng WB","Ghép GTIN","Lịch sử"];
        for(var i=0;i<pages.Length;i++)
        {
            var p=pages[i];var b=new Button{Text=p,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.White,BackColor=navy,Location=new Point(14,88+i*50),Size=new Size(232,42),Cursor=Cursors.Hand};
            b.FlatAppearance.BorderSize=0;b.Click+=(_,_)=>ShowPage(p);side.Controls.Add(b);
        }
        var theme=new Button{Text="◐  Sáng / tối",ForeColor=Color.White,BackColor=navy,FlatStyle=FlatStyle.Flat,Location=new Point(14,420),Size=new Size(232,40)};
        theme.Click+=(_,_)=>{dark=!dark;BackColor=dark?Color.FromArgb(31,40,58):Color.FromArgb(245,247,251);body.BackColor=BackColor;header.BackColor=dark?Color.FromArgb(47,58,79):Color.White;title.ForeColor=dark?Color.White:navy;state.ForeColor=dark?Color.White:navy;ShowPage(page);};side.Controls.Add(theme);
        ShowPage(page);
    }
    private Color TextColor=>dark?Color.White:navy;
    private Panel Section(string heading,int height=0)
    {
        var p=new Panel{Dock=DockStyle.Top,Height=height==0?72:height,Padding=new Padding(20),BackColor=dark?Color.FromArgb(45,57,78):Color.White,Margin=new Padding(0,0,0,10)};
        var l=new Label{Text=heading,Font=new Font("Segoe UI Semibold",12),ForeColor=TextColor,Dock=DockStyle.Top,Height=33};p.Controls.Add(l);return p;
    }
    private void ShowPage(string next)
    {
        page=next;body.SuspendLayout();body.Controls.Clear();body.BackColor=BackColor;body.AutoScroll=next is not ("Tổng quan" or "Ghép GTIN");
        title.Text=next;switch(next){case "Tổng quan":Overview();break;case "Kết nối API":Connections();break;case "Danh sách GTIN":CatalogPage();break;case "Bài đăng WB":CardsPage();break;case "Ghép GTIN":MatchPage();break;case "Lịch sử":HistoryPage();break;}
        body.ResumeLayout();
    }
    private Button Action(string label,EventHandler handler)
    {
        var b=new Button{Text=label,AutoSize=true,Height=35,BackColor=blue,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Margin=new Padding(4),Cursor=Cursors.Hand};b.FlatAppearance.BorderSize=0;b.Click+=handler;return b;
    }
    private FlowLayoutPanel Strip(params Control[] controls)
    {
        var p=new FlowLayoutPanel{Dock=DockStyle.Top,Height=52,Padding=new Padding(16,6,0,0),BackColor=BackColor};p.Controls.AddRange(controls);return p;
    }
    private FlowLayoutPanel WrapActions(params Control[] controls)
    {
        var p=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,AutoScroll=false,Padding=new Padding(4),BackColor=BackColor};
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
        info.Controls.Add(new Label{Text=demo?"CHẾ ĐỘ MẪU • Dữ liệu chỉ nằm trên máy":"Dữ liệu thật chỉ được đọc khi bấm Đồng bộ",Dock=DockStyle.Fill,AutoSize=true,Font=new Font("Segoe UI Semibold",12),ForeColor=TextColor,Margin=new Padding(0,0,0,8)},0,0);
        info.Controls.Add(new Label{Text="Mỗi đợt ghi yêu cầu chọn dòng và bấm Xác nhận thêm GTIN. Khi mất kết nối, bản xem trước được giữ nhưng không thể ghi.",Dock=DockStyle.Fill,AutoSize=true,ForeColor=TextColor,Margin=Padding.Empty},0,1);
        var actions=WrapActions(Action("Nạp dữ liệu mẫu",(_,_)=>LoadDemo()),Action("Đồng bộ dữ liệu thật",async (_,_)=>await Sync()),Action("Hủy tác vụ",(_,_)=>running?.Cancel()));
        layout.Controls.Add(actions,0,0);layout.Controls.Add(stats,0,1);layout.Controls.Add(info,0,2);
        body.Controls.Add(layout);
    }
    private void Connections()
    {
        var p=Section("Wildberries • mỗi cửa hàng có token riêng",160);
        var name=new TextBox{PlaceholderText="Tên cửa hàng",Width=190};var token=new TextBox{PlaceholderText="WB Content API token mới",Width=290,UseSystemPasswordChar=true};
        var select=new ComboBox{Width=240,DropDownStyle=ComboBoxStyle.DropDownList};
        void Refresh(){select.Items.Clear();foreach(var s in config.Shops)select.Items.Add(s.Name+" ["+s.Id[..6]+"]");if(select.Items.Count>0)select.SelectedIndex=0;}
        Refresh();
        var row=Strip(name,token,Action("Lưu cửa hàng",(_,_)=>{if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(token.Text))return;config.Shops.Add(new Shop{Name=name.Text.Trim(),ProtectedToken=Secrets.Protect(token.Text.Trim())});disk.Save(config);token.Clear();Refresh();Notice("Đã lưu khóa WB bằng DPAPI");}));row.Dock=DockStyle.Bottom;p.Controls.Add(row);
        var actions=Strip(select,Action("Kiểm tra WB",async (_,_)=>{if(select.SelectedIndex<0)return;await Run(async ct=>{await wb.Check(Secrets.Reveal(config.Shops[select.SelectedIndex].ProtectedToken),ct);Notice("WB đã phản hồi");});}),Action("Xóa cửa hàng",(_,_)=>{if(select.SelectedIndex<0)return;config.Shops.RemoveAt(select.SelectedIndex);disk.Save(config);Refresh();}),Action("Thay token",(_,_)=>{if(select.SelectedIndex<0||token.TextLength==0)return;config.Shops[select.SelectedIndex].ProtectedToken=Secrets.Protect(token.Text.Trim());disk.Save(config);token.Clear();Notice("Đã thay token WB");}));actions.Dock=DockStyle.Bottom;p.Controls.Add(actions);
        body.Controls.Add(p);
        var nk=Section("Честный Знак / Национальный каталог • khóa riêng theo tổ chức",156);
        var org=new TextBox{PlaceholderText="Tên tổ chức",Text=config.Organization,Width=180};var key=new TextBox{PlaceholderText=config.ProtectedCatalogKey==""?"API Key NK":"Đã lưu ••••••",UseSystemPasswordChar=true,Width=260};var since=new TextBox{Text=config.Since,Width=110};
        nk.Controls.Add(Strip(org,key,new Label{Text="Từ ngày YYYY-MM-DD",AutoSize=true,Padding=new Padding(2,9,0,0)},since,Action("Lưu khóa",(_,_)=>{if(key.TextLength==0)return;config.Organization=org.Text;config.Since=since.Text;config.ProtectedCatalogKey=Secrets.Protect(key.Text.Trim());disk.Save(config);key.Clear();key.PlaceholderText="Đã lưu ••••••";})));
        var nkActions=Strip(Action("Kiểm tra NK",async (_,_)=>await Run(async ct=>{await catalog.Check(Secrets.Reveal(config.ProtectedCatalogKey),ct);Notice("NK đã phản hồi");})),Action("Xóa khóa NK",(_,_)=>{config.ProtectedCatalogKey="";disk.Save(config);key.PlaceholderText="API Key NK";}));nkActions.Dock=DockStyle.Bottom;nk.Controls.Add(nkActions);body.Controls.Add(nk);nk.BringToFront();
        var caution=Section("Khóa NK được gửi trong query theo yêu cầu tài liệu của NK. Ứng dụng không ghi URL, khóa hoặc nội dung phản hồi vào log.",72);body.Controls.Add(caution);caution.BringToFront();
    }
    private void CatalogPage()
    {
        var grid=Grid("GTIN","Mã mẫu","Tên","Thương hiệu","Màu","Size","Trạng thái","Đồng bộ");
        foreach(var x in goods)grid.Rows.Add(x.Gtin,x.Model,x.Name,x.Brand,x.Color,x.Size,x.Accessible?x.Status:"Không có quyền",x.SyncedAt.LocalDateTime.ToString("g"));
        body.Controls.Add(grid);body.Controls.Add(Strip(new Label{Text=demo?"DỮ LIỆU MẪU • KHÔNG PHẢI NK THẬT":$"{goods.Count} GTIN đã đọc",ForeColor=TextColor,AutoSize=true}));
    }
    private void CardsPage()
    {
        var grid=Grid("Ảnh","Cửa hàng","Tên","nmID","Mã seller","Màu","Size / chrtID / barcode");
        grid.Columns.RemoveAt(0);grid.Columns.Insert(0,new DataGridViewImageColumn{Name="Ảnh",HeaderText="Ảnh",ImageLayout=DataGridViewImageCellLayout.Zoom,Width=60});grid.RowTemplate.Height=54;
        foreach(var x in cards){var sizes=string.Join("; ",Json.A(x.Raw,"sizes").Select(s=>$"{Json.S(s,"techSize")} / {Json.S(s,"chrtID")} / {string.Join(',',Json.A(s,"skus").Select(z=>z?.ToString()))}"));var index=grid.Rows.Add(EmptyPhoto,x.ShopName,x.Title,x.NmId,x.VendorCode,Matching.Color(x),sizes);_ = LoadPhoto(grid,index,x.Photo);}
        body.Controls.Add(grid);body.Controls.Add(Strip(new Label{Text=demo?"DỮ LIỆU MẪU • KHÔNG PHẢI WB THẬT":$"{cards.Count} bài đăng",ForeColor=TextColor,AutoSize=true}));
    }
    private async Task LoadPhoto(DataGridView grid,int row,string url,int column=0)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https")return;
        try {var data=await media.GetByteArrayAsync(uri);using var stream=new MemoryStream(data);using var picture=Image.FromStream(stream);if(!grid.IsDisposed && row<grid.Rows.Count)grid.Rows[row].Cells[column].Value=new Bitmap(picture);}
        catch { /* Image unavailable; product data still usable. */ }
    }
    private void MatchPage()
    {
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(12,8,12,12),BackColor=BackColor};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,170));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,110));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var message=new Label{Text=(demo?"DỮ LIỆU MẪU • CẤM GHI WB. ":"")+"Thao tác chỉ thêm barcode vào size; WB không cho thay hoặc xóa mã cũ.",Dock=DockStyle.Fill,ForeColor=TextColor,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};
        var grid=Grid("Chọn","Ảnh","Cửa hàng","Tên","nmID","Mã seller","Màu","Size WB","chrtID","Barcode hiện có","GTIN đề xuất","Thẻ NK","Lý do khớp","Trạng thái");
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
            var i=grid.Rows.Add(x.Selected,EmptyPhoto,x.Shop,listing?.Title??"",x.NmId,x.VendorCode,x.Color,x.WbSize,x.ChrtId,barcodes,x.Gtin,catalogInfo,x.Detail,Status(x.Status));
            grid.Rows[i].Tag=x;
            if(x.Status is MatchStatus.Multiple or MatchStatus.Conflict or MatchStatus.Unpublished)grid.Rows[i].DefaultCellStyle.BackColor=dark?Color.FromArgb(98,69,45):Color.FromArgb(255,234,203);
            if(listing!=null)_=LoadPhoto(grid,i,listing.Photo,1);
        }
        grid.CellValueChanged+=(_,e)=>{if(e.RowIndex>=0&&e.ColumnIndex==0&&grid.Rows[e.RowIndex].Tag is MatchRow row)row.Selected=grid.Rows[e.RowIndex].Cells[0].Value is true;};
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        var filter=new ComboBox{Width=180,DropDownStyle=ComboBoxStyle.DropDownList};filter.Items.AddRange(new object[]{"Tất cả","Khớp chắc chắn","Đã có GTIN","Cần xác nhận","Xung đột","Không tìm thấy"});filter.SelectedIndex=0;
        filter.SelectedIndexChanged+=(_,_)=>{foreach(DataGridViewRow r in grid.Rows)if(r.Tag is MatchRow m)r.Visible=filter.SelectedIndex switch {0=>true,1=>m.Status==MatchStatus.Exact,2=>m.Status==MatchStatus.Existing,3=>m.Status is MatchStatus.Multiple or MatchStatus.AccessDenied or MatchStatus.Unpublished or MatchStatus.Stale,4=>m.Status==MatchStatus.Conflict,5=>m.Status==MatchStatus.Missing,_=>true};};
        var model=new TextBox{PlaceholderText="Mã mẫu NK",Width=140};var sizeFrom=new TextBox{PlaceholderText="Size WB (XL)",Width=120};var sizeTo=new TextBox{PlaceholderText="Size NK (48)",Width=120};
        var rules=ActionGroup("Ánh xạ mã hàng và size cho sản phẩm đang chọn",model,sizeFrom,sizeTo,Action("Lưu ánh xạ",(_,_)=>{if(grid.CurrentRow?.Tag is not MatchRow m)return;if(model.TextLength>0)config.ProductRules[m.ShopId+":"+m.VendorCode]=model.Text.Trim();var actual=config.ProductRules.GetValueOrDefault(m.ShopId+":"+m.VendorCode,m.VendorCode);if(sizeFrom.TextLength>0&&sizeTo.TextLength>0)config.SizeRules[Matching.Normalize(actual)+":"+Matching.Normalize(sizeFrom.Text)]=sizeTo.Text.Trim();disk.Save(config);Rebuild();ShowPage("Ghép GTIN");}));
        var actions=ActionGroup("Lọc và cập nhật",filter,Action("Chọn dòng khớp chắc chắn",(_,_)=>{foreach(var m in matches)m.Selected=m.Status==MatchStatus.Exact;ShowPage("Ghép GTIN");}),Action("Xác nhận thêm GTIN",async (_,_)=>await Commit()),Action("Tạm dừng",(_,_)=>{resume.Reset();Notice("Đã tạm dừng hàng đợi");}),Action("Tiếp tục",(_,_)=>{resume.Set();Notice("Đang tiếp tục");}),Action("Hủy",(_,_)=>running?.Cancel()),Action("Xuất CSV",(_,_)=>ExportMatches()));
        var area=new Panel{Dock=DockStyle.Fill,BackColor=BackColor};
        if(matches.Count==0)area.Controls.Add(new Label{Text="Chưa có dữ liệu đối chiếu.\nVào Tổng quan → Nạp dữ liệu mẫu để xem thử, hoặc kết nối hai API rồi chọn Đồng bộ dữ liệu thật.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=TextColor,Font=new Font("Segoe UI",12),Padding=new Padding(25)});
        else area.Controls.Add(grid);
        layout.Controls.Add(message,0,0);layout.Controls.Add(actions,0,1);layout.Controls.Add(rules,0,2);layout.Controls.Add(area,0,3);
        body.Controls.Add(layout);
    }
    private static string Status(MatchStatus x)=>x switch {MatchStatus.Exact=>"Khớp chắc chắn",MatchStatus.Existing=>"Đã có GTIN",MatchStatus.Missing=>"Không tìm thấy",MatchStatus.Multiple=>"Nhiều GTIN ứng viên",MatchStatus.Conflict=>"Xung đột",MatchStatus.Unpublished=>"Chưa công bố",MatchStatus.AccessDenied=>"Không có quyền",MatchStatus.Stale=>"Dữ liệu cũ",MatchStatus.Updated=>"Đã cập nhật",_=>"Lỗi"};
    private void HistoryPage()
    {
        var grid=Grid("Thời gian","Cửa hàng","nmID","chrtID","GTIN","Kết quả","Chi tiết");foreach(var x in disk.History().OrderByDescending(x=>x.At))grid.Rows.Add(x.At.LocalDateTime.ToString("g"),x.Shop,x.NmId,x.ChrtId,x.Gtin,x.Result,x.Detail);
        body.Controls.Add(grid);body.Controls.Add(Strip(Action("Xuất báo cáo CSV",(_,_)=>ExportHistory())));
    }
    private void Rebuild(){matches.Clear();matches.AddRange(Matching.Build(goods,cards,config));}
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
        dark=true;BackColor=Color.FromArgb(31,40,58);body.BackColor=BackColor;header.BackColor=Color.FromArgb(47,58,79);title.ForeColor=Color.White;state.ForeColor=Color.White;
        ShowPage("Tổng quan");Save("Tong-quan-toi-100");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-100");
        WindowState=FormWindowState.Maximized;Application.DoEvents();
        ShowPage("Tổng quan");Save("Tong-quan-toi-phong-to");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-phong-to");
        WindowState=FormWindowState.Normal;Application.DoEvents();
        Size=new Size(1280,800);Scale(new SizeF(1.25f,1.25f));ShowPage("Tổng quan");Save("Tong-quan-toi-scale125");
        ShowPage("Ghép GTIN");Save("Ghep-GTIN-toi-scale125");
    }
    private async Task Sync()
    {
        if(config.ProtectedCatalogKey==""||config.Shops.Count==0){Notice("Cần lưu khóa NK và ít nhất một cửa hàng WB");return;}
        await Run(async ct=>
        {
            Notice("Đang đọc dữ liệu");bar.Value=5;
            if(!DateTime.TryParse(config.Since,out var since))throw new InvalidOperationException("Ngày bắt đầu không hợp lệ");
            var freshGoods=await catalog.Read(Secrets.Reveal(config.ProtectedCatalogKey),since,DateTime.UtcNow.AddDays(1),new Progress<string>(Notice),ct);
            var freshCards=new List<Listing>();
            foreach(var shop in config.Shops){ct.ThrowIfCancellationRequested();freshCards.AddRange(await wb.Read(shop,Secrets.Reveal(shop.ProtectedToken),ct));Notice($"Đang đọc WB: {shop.Name} ({freshCards.Count} thẻ)");}
            goods.Clear();goods.AddRange(freshGoods);cards.Clear();cards.AddRange(freshCards);demo=false;bar.Value=80;
            Notice("Đang đối chiếu");Rebuild();bar.Value=100;ShowPage("Tổng quan");Notice($"Đã đối chiếu {matches.Count} dòng; cần seller xem và xác nhận");
        });
    }
    private async Task Commit()
    {
        if(demo){Notice("Dữ liệu mẫu không được ghi lên WB");return;}
        var selected=matches.Where(m=>m.Selected&&m.Status==MatchStatus.Exact).ToList();
        if(selected.Count==0){Notice("Chọn ít nhất một dòng Khớp chính xác");return;}
        if(MessageBox.Show(this,$"Thêm {selected.Count} GTIN vào {selected.Select(x=>x.ShopId).Distinct().Count()} cửa hàng WB?\nWB không thể xóa barcode cũ.","Xác nhận thêm GTIN",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        await Run(async ct=>
        {
            resume.Set();var done=0;
            foreach(var group in selected.GroupBy(x=>(x.ShopId,x.NmId)))
            {
                ct.ThrowIfCancellationRequested();while(!resume.IsSet){await Task.Delay(200,ct);}
                var shop=config.Shops.SingleOrDefault(s=>s.Id==group.Key.ShopId)??throw new InvalidOperationException("Cửa hàng không còn tồn tại");
                var token=Secrets.Reveal(shop.ProtectedToken);
                try
                {
                    Notice($"Đang cập nhật WB: {done}/{selected.Count}");
                    var fresh=await wb.ReadOne(shop,token,group.Key.NmId,ct);
                    // Invalidate a preview when identifiers, sizes, or color have drifted.
                    var original=cards.Single(c=>c.ShopId==shop.Id&&c.NmId==fresh.NmId);
                    if(Json.A(original.Raw,"sizes").ToJsonString()!=Json.A(fresh.Raw,"sizes").ToJsonString() || Matching.Normalize(Matching.Color(original))!=Matching.Normalize(Matching.Color(fresh)))throw new InvalidOperationException("Thẻ WB đã thay đổi; đồng bộ và duyệt lại");
                    var payload=CardPayload.Add(fresh.Raw,group);
                    await wb.Update(token,payload,ct);
                    foreach(var row in group)
                    {
                        var found=false;
                        for(var attempt=0;attempt<6;attempt++)
                        {
                            ct.ThrowIfCancellationRequested();await Task.Delay(TimeSpan.FromSeconds(attempt==0?2:6),ct);
                            var verify=await wb.ReadOne(shop,token,row.NmId,ct);
                            found=Json.A(verify.Raw,"sizes").Any(s=>Json.L(s,"chrtID")==row.ChrtId&&Json.A(s,"skus").Any(v=>v?.ToString()==row.Gtin));
                            if(found)break;
                        }
                        if(!found)throw new ApiFailure((await wb.Errors(token,row.NmId,ct)) is {Length:>0} error?error:"WB chưa xác nhận GTIN trong size; kiểm tra lại sau khi xử lý đồng bộ");
                        row.Status=MatchStatus.Updated;row.Detail="Đã đọc lại đúng chrtID";disk.Append(new(DateTimeOffset.UtcNow,shop.Name,row.NmId,row.ChrtId,row.Gtin,"Đã cập nhật",row.Detail));done++;
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(Exception e)
                {
                    foreach(var row in group.Where(r=>r.Status==MatchStatus.Exact)){row.Status=MatchStatus.Failed;row.Detail=SafeError(e);disk.Append(new(DateTimeOffset.UtcNow,shop.Name,row.NmId,row.ChrtId,row.Gtin,"Lỗi",row.Detail));done++;}
                }
                bar.Value=Math.Min(100,(int)(100.0*done/selected.Count));
                if(done<selected.Count)await Task.Delay(TimeSpan.FromSeconds(6),ct);
            }
            ShowPage("Ghép GTIN");Notice($"Hoàn tất {done}/{selected.Count}; kiểm tra từng dòng trong Lịch sử");
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
        var lines=new List<string>{"Cửa hàng,nmID,Mã seller,Màu,Size,chrtID,GTIN,Nguồn,Trạng thái,Chi tiết"};
        lines.AddRange(matches.Select(x=>string.Join(',',new[]{x.Shop,x.NmId.ToString(),x.VendorCode,x.Color,x.WbSize,x.ChrtId.ToString(),x.Gtin,x.Source,Status(x.Status),x.Detail}.Select(Csv))));SaveCsv(lines);
    }
    private void ExportHistory()
    {
        var lines=new List<string>{"Thời gian,Cửa hàng,nmID,chrtID,GTIN,Kết quả,Chi tiết"};lines.AddRange(disk.History().Select(x=>string.Join(',',new[]{x.At.ToString("O"),x.Shop,x.NmId.ToString(),x.ChrtId.ToString(),x.Gtin,x.Result,x.Detail}.Select(Csv))));SaveCsv(lines);
    }
    private static string Csv(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
    private void SaveCsv(IEnumerable<string> lines){using var d=new SaveFileDialog{Filter="CSV UTF-8|*.csv",FileName="GTIN-Sync-WB-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".csv"};if(d.ShowDialog(this)==DialogResult.OK)File.WriteAllLines(d.FileName,lines,new UTF8Encoding(true));}
}
