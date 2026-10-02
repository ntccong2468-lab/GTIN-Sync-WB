using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private readonly Dictionary<long,(int Offset,string Query,string Filter)> mappingView=new();

    private void ShowKizMapping()
    {
        ClearWork();activePage=ShowKizMapping;var store=CurrentStore();
        var state=store is not null&&mappingView.TryGetValue(store.Id,out var saved)?saved:(Offset:0,Query:"",Filter:"all");
        work.Controls.Add(Title("KIZ Mapping"));
        var search=DarkText("Tìm SKU, GTIN, size hoặc tên",310);search.Left=175;search.Top=0;search.Text=state.Query;work.Controls.Add(search);
        var filter=DarkCombo(145);filter.Left=498;filter.Top=0;
        filter.Items.AddRange(new object[]{"Tất cả","Đã xác nhận","Chưa xác nhận","KIZ sẵn sàng","Có lỗi"});
        var filters=new[]{"all","mapped","unmapped","available","error"};filter.SelectedIndex=Math.Max(0,Array.IndexOf(filters,state.Filter));work.Controls.Add(filter);
        var refresh=IconButton("↻");refresh.Left=656;refresh.Top=0;work.Controls.Add(refresh);
        var summary=new Label{Left=4,Top=55,AutoSize=true,ForeColor=C.Muted};work.Controls.Add(summary);
        var previous=ActionButton("← Trước",105);previous.Left=4;previous.Top=86;work.Controls.Add(previous);
        var next=ActionButton("Sau →",105);next.Name="nextGtinMappingPage";next.Left=122;next.Top=86;work.Controls.Add(next);
        var pageLabel=new Label{Name="gtinMappingPage",Left=245,Top=97,AutoSize=true,ForeColor=C.Text};work.Controls.Add(pageLabel);
        var catalog=ActionButton("Đồng bộ catalog",165);catalog.Left=485;catalog.Top=86;work.Controls.Add(catalog);
        var check=ActionButton("Kiểm tra / đồng bộ Znack",245);check.Left=662;check.Top=86;check.Click+=(_,_)=>ShowIntegrationTestCenter();work.Controls.Add(check);
        var card=CardPanel();card.Left=4;card.Top=145;work.Controls.Add(card);
        var grid=DarkGrid();grid.Name="gtinMappingGrid";grid.Dock=DockStyle.Fill;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
        foreach(var column in new[]{("gtin","GTIN",155),("name","Sản phẩm",210),("variant","SKU / Size / Biến thể",170),("mapping","Mapping",100),
            ("total","Tổng KIZ",80),("available","Sẵn sàng",80),("reserved","Đã giữ",75),("assigned","Đã gán",75),("stage","Znack / WB",155),("error","Lỗi gần nhất",230)})
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name=column.Item1,HeaderText=column.Item2,Width=column.Item3});
        foreach(var action in new[]{("edit","Sửa GTIN"),("export","Xuất KIZ"),("add","Thêm KIZ"),("archive","Lưu trữ")})
            grid.Columns.Add(new DataGridViewButtonColumn{Name=action.Item1,HeaderText="",Text=action.Item2,UseColumnTextForButtonValue=true,Width=90,FlatStyle=FlatStyle.Flat});
        grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;card.Controls.Add(grid);
        void Load(){
            if(store is null){summary.Text="Chọn cửa hàng để xem mapping.";return;}
            var result=app.Db.GetGtinMappingPage(store,state.Offset,50,search.Text.Trim(),filters[filter.SelectedIndex]);
            if(state.Offset>=result.Total&&state.Offset>0){state.Offset=Math.Max(0,(result.Total-1)/50*50);result=app.Db.GetGtinMappingPage(store,state.Offset,50,search.Text.Trim(),filters[filter.SelectedIndex]);}
            state.Query=search.Text.Trim();state.Filter=filters[filter.SelectedIndex];mappingView[store.Id]=state;grid.Rows.Clear();
            foreach(var item in result.Rows){var index=grid.Rows.Add(item.Gtin.Length>0?item.Gtin:"Chưa rõ GTIN",item.Name,$"{item.Sku}\nSize {item.Size} · {item.VariantId}",
                item.Confirmed?item.Source=="seller"?"Seller xác nhận":"Đã xác nhận":"Chưa xác nhận",item.TotalKiz,item.AvailableKiz,item.ReservedKiz,item.AssignedKiz,
                $"{item.ZnackStage}\n{item.WbStage}",item.LastError);grid.Rows[index].Tag=item;grid.Rows[index].Height=74;}
            summary.Text=$"{result.MappingRuleCount:N0} rule mapping · {result.TotalKiz:N0} KIZ · {result.AvailableKiz:N0} sẵn sàng · {result.ReservedKiz:N0} đã giữ · {result.AssignedKiz:N0} đã gán";
            pageLabel.Text=$"Trang {state.Offset/50+1}/{Math.Max(1,(result.Total+49)/50)} · {result.Total:N0} biến thể · 50 dòng/trang";
            previous.Enabled=state.Offset>0;next.Enabled=state.Offset+50<result.Total;
        }
        previous.Click+=(_,_)=>{state.Offset=Math.Max(0,state.Offset-50);Load();};next.Click+=(_,_)=>{state.Offset+=50;Load();};refresh.Click+=(_,_)=>Load();
        search.TextChanged+=(_,_)=>{state.Offset=0;Load();};filter.SelectedIndexChanged+=(_,_)=>{state.Offset=0;Load();};
        catalog.Click+=async(_,_)=>{if(store is null)return;catalog.Enabled=false;var token=pageCts.Token;try{var result=await app.SyncProductsAsync(store,token);if(!token.IsCancellationRequested){ShowInfo(result.Message);Load();}}catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)ShowInfo(ex.Message);}finally{if(!catalog.IsDisposed)catalog.Enabled=true;}};
        grid.CellContentClick+=(_,e)=>{
            if(store is null||e.RowIndex<0||e.ColumnIndex<0||grid.Rows[e.RowIndex].Tag is not GtinMappingRow row)return;
            try{switch(grid.Columns[e.ColumnIndex].Name){
                case "edit":
                    var gtin=PromptText("GTIN của biến thể",$"SKU {row.Sku} · size {row.Size} · biến thể {row.VariantId}\nGTIN 8/12/13/14 số (hiện tại: {row.Gtin}):");
                    if(!string.IsNullOrWhiteSpace(gtin)){app.Db.UpsertSellerGtinMapping(store,row.Sku,row.VariantId,gtin);Load();}break;
                case "add":
                    if(row.Gtin.Length==0)throw new InvalidOperationException("Xác nhận GTIN của biến thể trước khi thêm KIZ.");
                    var raw=PromptText("Thêm mã KIZ",$"SKU {row.Sku} · size {row.Size}\nNhập DataMatrix của GTIN {row.Gtin}:");
                    if(string.IsNullOrWhiteSpace(raw))return;var code=MarketplaceFbsPayloads.NormalizeCode(raw);var parsed=AppServices.ParseKiz(code);
                    if(!parsed.Ok||GtinCode.Normalize(parsed.Gtin)!=row.Gtin||!code.StartsWith("01"+row.Gtin+"21",StringComparison.Ordinal))throw new InvalidOperationException("KIZ thiếu cấu trúc AI(01)/AI(21) hoặc GTIN không khớp biến thể.");
                    app.Db.UpsertKiz(code,row.Gtin,"AVAILABLE");Load();break;
                case "export":
                    var codes=app.Db.Kiz().Where(x=>x.Gtin==row.Gtin&&x.Status=="AVAILABLE"&&x.Assigned.Length==0).Select(x=>x.Code).ToArray();
                    if(codes.Length==0)throw new InvalidOperationException("Không có KIZ sẵn sàng để xuất.");
                    using(var file=new SaveFileDialog{Filter="Danh sách KIZ (*.txt)|*.txt",FileName=$"{row.Gtin}-KIZ.txt"}){if(file.ShowDialog(this)==DialogResult.OK){File.WriteAllLines(file.FileName,codes);app.Db.Audit("KIZ","Xuất KIZ sẵn sàng",$"{row.Gtin}:{codes.Length}:{file.FileName}");}}break;
                case "archive":
                    if(MessageBox.Show(this,$"Lưu trữ mapping của {row.Sku} · size {row.Size}? KIZ và lịch sử được giữ lại.","Lưu trữ mapping",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){app.Db.ArchiveGtinMapping(store,row.Sku,row.VariantId);Load();}break;
            }}catch(Exception ex){ShowInfo(ex.Message);}
        };
        refreshActivePage=Load;SetWorkResize((_,_)=>{card.Width=Math.Max(500,work.ClientSize.Width-36);card.Height=Math.Max(200,work.ClientSize.Height-card.Top-28);});
        card.Width=work.ClientSize.Width-36;card.Height=work.ClientSize.Height-card.Top-28;Load();
    }
}
