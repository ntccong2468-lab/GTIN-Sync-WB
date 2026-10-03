using MarketplaceHub.Core;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private void ShowProductSynchronization()
    {
        ClearWork();var token=pageCts.Token;activePage=ShowProductSynchronization;
        var store=CurrentStore();
        work.Controls.Add(Title("Đồng bộ sản phẩm · "+(store is null?"chọn cửa hàng":MarketplaceName(store.Marketplace))));
        if(store is null)return;
        var run=ActionButton("↻ Đồng bộ / tiếp tục",230,true);run.Top=50;run.Left=0;work.Controls.Add(run);
        var stop=ActionButton("Tạm dừng",120);stop.Top=50;stop.Left=240;stop.Enabled=false;work.Controls.Add(stop);
        var state=new Label{Left=0,Top=100,Height=55,ForeColor=C.Muted,AutoEllipsis=true};work.Controls.Add(state);
        var search=new TextBox{Left=0,Top=165,Width=390,PlaceholderText="Tìm SKU, size, barcode hoặc GTIN",BackColor=C.Card,ForeColor=C.Text};work.Controls.Add(search);
        var missing=new CheckBox{Left=415,Top=163,Text="Chỉ biến thể thiếu GTIN",AutoSize=true,ForeColor=C.Text};work.Controls.Add(missing);
        var card=CardPanel();card.Left=0;card.Top=210;work.Controls.Add(card);
        var grid=DarkGrid();grid.Name="productVariants";grid.Dock=DockStyle.Fill;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewImageColumn{Name="photo",HeaderText="Ảnh",Width=60,ImageLayout=DataGridViewImageCellLayout.Zoom,DefaultCellStyle=new DataGridViewCellStyle{NullValue=null}});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="sku",HeaderText="SKU / Article",Width=130});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="size",HeaderText="Size",Width=50});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="barcodes",HeaderText="Barcode của biến thể",Width=180});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="gtin",HeaderText="GTIN",Width=150});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="status",HeaderText="Trạng thái",MinimumWidth=160,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});card.Controls.Add(grid);
        var prev=ActionButton("‹",48);var next=ActionButton("›",48);var page=new Label{ForeColor=C.Text,AutoSize=true};work.Controls.Add(prev);work.Controls.Add(next);work.Controls.Add(page);
        IReadOnlyList<ProductVariantRow> variants=Array.Empty<ProductVariantRow>();var currentPage=0;CancellationTokenSource? operation=null;var loading=false;
        void Render()
        {
            if(token.IsCancellationRequested || grid.IsDisposed)return;
            DisposeImages(card);grid.Rows.Clear();
            var query=search.Text.Trim();var filtered=variants.Where(v=>(!missing.Checked || v.Gtin.Length==0)
                && (query.Length==0 || (v.Sku+" "+v.Size+" "+v.Gtin+" "+string.Join(" ",v.Barcodes)).Contains(query,StringComparison.OrdinalIgnoreCase))).ToArray();
            var pages=Math.Max(1,(filtered.Length+49)/50);currentPage=Math.Min(currentPage,pages-1);
            foreach(var variant in filtered.Skip(currentPage*50).Take(50)) {
                var row=grid.Rows.Add(null,variant.Sku,variant.Size,string.Join("\n",variant.Barcodes),variant.Gtin,variant.Gtin.Length==0?"Cần kiểm tra GTIN/barcode":"Đã có GTIN hợp lệ");
                grid.Rows[row].Height=85;grid.Rows[row].Tag=variant;grid.Rows[row].Cells["status"].Style.ForeColor=variant.Gtin.Length==0?C.Orange:C.Green;
                if(variant.ImageUrl.Length>0)_=LoadImageAsync(grid,row,0,variant.ImageUrl);
            }
            page.Text=$"Trang {currentPage+1}/{pages} · {filtered.Length} biến thể · 50 dòng/trang";prev.Enabled=currentPage>0;next.Enabled=currentPage+1<pages;
        }
        async Task Load()
        {
            if(loading)return;loading=true;
            try {
                var loaded=await Task.Run(()=> (Variants:app.Db.ProductVariants(store),Status:app.Db.ProductCatalogStatus(store)),token);
                if(token.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
                variants=loaded.Variants;var status=loaded.Status;
                state.Text=$"{status.Products} card · {status.Variants} biến thể · {status.MissingGtin} thiếu GTIN · {status.Pages} trang đã lưu. "+
                    (status.Pending?"Có tiến trình đang tạm dừng; bấm Tiếp tục.":"Sẵn sàng đồng bộ.")+"\n"+status.Error;
                pageProducts.Remove(store.Id);Render();
            }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)state.Text=ex.Message;}
            finally{loading=false;}
        }
        search.TextChanged+=(_,_)=>{currentPage=0;Render();};missing.CheckedChanged+=(_,_)=>{currentPage=0;Render();};
        prev.Click+=(_,_)=>{currentPage--;Render();};next.Click+=(_,_)=>{currentPage++;Render();};stop.Click+=(_,_)=>operation?.Cancel();
        run.Click+=async(_,_)=>
        {
            if(operation is not null)return;
            operation=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);run.Enabled=false;stop.Enabled=true;
            var progress=new Progress<string>(message=>{if(!token.IsCancellationRequested && !state.IsDisposed)state.Text=message;});
            try {
                var result=await app.RefreshProductCatalogAsync(store,operation.Token,progress);
                pageProducts.Remove(store.Id);
                if(token.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
                await Load();if(!state.IsDisposed)state.Text=result.Message+"\n"+state.Text;
            }finally {
                operation.Dispose();operation=null;
                if(!run.IsDisposed){run.Enabled=true;stop.Enabled=false;}
            }
        };
        SetWorkResize((_,_)=>{state.Width=work.ClientSize.Width-25;card.Width=work.ClientSize.Width-25;card.Height=Math.Max(230,work.ClientSize.Height-card.Top-70);
            prev.Left=0;prev.Top=card.Bottom+12;page.Left=65;page.Top=prev.Top+12;next.Left=440;next.Top=prev.Top;});
        refreshActivePage=async()=>await Load();_=Load();
    }
}
