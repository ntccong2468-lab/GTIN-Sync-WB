using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private void ShowFboPacking()
    {
        ClearWork();activePage=ShowFboPacking;
        var token=pageCts.Token;var store=CurrentStore();
        work.Controls.Add(Title("Đóng hàng FBO"));
        var hint=new Label{Left=4,Top=49,AutoSize=true,ForeColor=C.Muted,
            Text="Chọn đúng biến thể và số lượng. Nhãn chuẩn bị sản phẩm/KIZ 58×40 mm · Phiếu nhặt A4."};work.Controls.Add(hint);
        var search=DarkText("Tìm SKU, size hoặc tên sản phẩm…",350);search.Left=4;search.Top=81;work.Controls.Add(search);
        var export=ActionButton("Xuất nhãn đã chọn",210,true);export.Name="exportFboPreparation";export.Left=370;export.Top=81;work.Controls.Add(export);
        var info=new Label{Left=4,Top=130,AutoSize=true,ForeColor=C.Muted};work.Controls.Add(info);
        var card=CardPanel();card.Left=4;card.Top=165;work.Controls.Add(card);
        var grid=DarkGrid();grid.Name="fboPreparation";grid.Dock=DockStyle.Fill;grid.ReadOnly=false;
        grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="selected",HeaderText="☐",Width=40});
        grid.Columns.Add(new DataGridViewImageColumn{Name="image",HeaderText="Ảnh",Width=65,ReadOnly=true,ImageLayout=DataGridViewImageCellLayout.Zoom});
        foreach(var col in new[]{("name","Sản phẩm",210),("sku","SKU",130),("color","Màu",95),("size","Size",60),("barcode","GTIN / Barcode",155)})
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name=col.Item1,HeaderText=col.Item2,Width=col.Item3,ReadOnly=true});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="quantity",HeaderText="Số lượng",Width=85});
        grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="marked",HeaderText="Cần KIZ",Width=75});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="available",HeaderText="KIZ sẵn sàng",Width=110,ReadOnly=true});card.Controls.Add(grid);
        void Load(){
            var keep=grid.Rows.Cast<DataGridViewRow>().Where(x=>x.Tag is FboPreparationRow).ToDictionary(x=>((FboPreparationRow)x.Tag).Sku+"\n"+((FboPreparationRow)x.Tag).VariantId,
                x=>(Selected:x.Cells["selected"].Value is true,Quantity:x.Cells["quantity"].Value,Marked:x.Cells["marked"].Value));
            grid.Rows.Clear();if(store is null)return;
            var products=app.Db.Products(store.Id).ToDictionary(x=>x.Sku,StringComparer.Ordinal);var pool=app.Db.Kiz();var pending=app.Db.PendingFboPreparation(store);var q=search.Text.Trim();
            var source=app.Db.ProductVariants(store).Select(v=>{
                products.TryGetValue(v.Sku,out var product);var meta=product is null?null:ProductMeta(product);
                var raw=JsonNode.Parse(product?.RawJson??"{}");var marked=raw?["needsKiz"]?.ToString()=="true"||raw?["isKizRequired"]?.ToString()=="true"||raw?["isNeedMark"]?.ToString()=="true";
                var barcode=v.Gtin.Length>0?v.Gtin:v.Barcodes.Count==1?v.Barcodes[0]:"";
                var variant=JsonNode.Parse(v.RawJson);var color=variant?["color"]?.ToString()??meta?.Color??"";
                return (Row:new FboPreparationRow(v.VariantId,v.Sku,product?.Name??v.Sku,color,v.Size,meta?.Brand??"",barcode,1,marked,Array.Empty<string>()),v.ImageUrl);
            }).ToArray();
            if(pending is not null){source=pending.Rows.Select(x=>(Row:x,ImageUrl:"" )).ToArray();info.Text="Có lượt FBO chưa hoàn tất. Tiếp tục giữ đúng biến thể và KIZ của lượt trước.";}
            else info.Text=$"{source.Length} biến thể · Chọn ‘Cần KIZ’ theo yêu cầu sản phẩm của bạn.";
            foreach(var item in source){var p=item.Row;if(pending is null&&q.Length>0&&!$"{p.Sku} {p.Name} {p.Size}".Contains(q,StringComparison.OrdinalIgnoreCase))continue;
                var key=p.Sku+"\n"+p.VariantId;keep.TryGetValue(key,out var previous);
                var index=grid.Rows.Add(pending is not null||previous.Selected,null,p.Name,p.Sku,p.Color,p.Size,p.Barcode,previous.Quantity??p.Quantity,(object?)previous.Marked??p.NeedsKiz,
                    pool.Count(x=>x.Gtin==GtinCode.Normalize(p.Barcode)&&x.Status=="AVAILABLE"&&x.Assigned.Length==0));
                var row=grid.Rows[index];row.Tag=p;row.Height=72;if(pending is not null)row.ReadOnly=true;
                if(item.ImageUrl.Length>0)_=LoadImageAsync(grid,index,1,item.ImageUrl);
            }
        }
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        grid.DataError+=(_,e)=>{e.ThrowException=false;};
        grid.ColumnHeaderMouseClick+=(_,e)=>{if(e.ColumnIndex!=0)return;var selected=grid.Rows.Cast<DataGridViewRow>().Where(x=>!x.ReadOnly).ToArray();var select=selected.Any(x=>x.Cells[0].Value is not true);foreach(var row in selected)row.Cells[0].Value=select;};
        export.Click+=async(_,_)=>{
            if(store is null)return;if(!await printOperations.WaitAsync(0,token))return;export.Enabled=false;
            try{
                grid.EndEdit();var pending=app.Db.PendingFboPreparation(store);
                var rows=pending?.Rows??grid.Rows.Cast<DataGridViewRow>().Where(x=>x.Cells[0].Value is true).Select(x=>{
                    if(!int.TryParse(x.Cells["quantity"].Value?.ToString(),out var quantity)||quantity is < 1 or > 10000)throw new InvalidOperationException("Số lượng phải từ 1 đến 10000.");
                    return ((FboPreparationRow)x.Tag) with{Quantity=quantity,NeedsKiz=x.Cells["marked"].Value is true};}).ToArray();
                if(rows.Count==0)throw new InvalidOperationException("Chưa chọn biến thể.");
                if(rows.Any(x=>x.Barcode.Length==0||x.NeedsKiz&&GtinCode.Normalize(x.Barcode).Length==0))throw new InvalidOperationException("Barcode mơ hồ hoặc thiếu GTIN hợp lệ. Sửa mapping đúng biến thể trước khi in.");
                var bundle=await Task.Run(()=>{
                    var retained=pending is null?Array.Empty<MarketplaceHub.Infrastructure.MarketplaceUnitKiz>():app.Db.MarketplaceKizReservations(store,pending.Id).ToArray();
                    foreach(var group in rows.Where(x=>x.NeedsKiz).GroupBy(x=>GtinCode.Normalize(x.Barcode))){
                        var available=app.Db.Kiz().Count(x=>x.Gtin==group.Key&&x.Status=="AVAILABLE"&&x.Assigned.Length==0)+retained.Count(x=>x.Gtin==group.Key);
                        if(available<group.Sum(x=>x.Quantity))throw new InvalidOperationException($"GTIN {group.Key}: cần {group.Sum(x=>x.Quantity)} KIZ, hiện có {available} mã khả dụng.");
                    }
                    var draft=app.Db.BeginFboPreparation(store,rows);
                    var prepared=draft.Rows.Select(row=>{
                        var codes=new List<string>();if(row.NeedsKiz)for(var unit=0;unit<row.Quantity;unit++){
                            token.ThrowIfCancellationRequested();codes.Add(app.Db.ReserveMarketplaceKiz(store,draft.Id,row.Sku+"\n"+row.VariantId,unit,GtinCode.Normalize(row.Barcode))??throw new InvalidOperationException("KIZ vừa hết; lượt này được giữ để tiếp tục."));
                        }return row with{KizCodes=codes};}).ToArray();
                    var output=new WbPrintBundleService().BuildFboPreparationLabels(store.Name,store.Marketplace,prepared,null,token);
                    app.Db.CompleteFboPreparation(store,draft.Id,output.Directory);return output;
                },token);
                if(token.IsCancellationRequested||IsDisposed)return;
                using var preview=new WbPrintPreviewDialog(bundle,detail=>app.Db.Audit("FBO","Gửi in",detail),$"{MarketplaceName(store.Marketplace)} · Nhãn chuẩn bị FBO");preview.ShowDialog(this);Load();
            }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested){ShowInfo(ex.Message);Load();}}finally{printOperations.Release();if(!export.IsDisposed)export.Enabled=true;}
        };
        search.TextChanged+=(_,_)=>Load();refreshActivePage=Load;
        SetWorkResize((_,_)=>{card.Width=Math.Max(500,work.ClientSize.Width-36);card.Height=Math.Max(200,work.ClientSize.Height-card.Top-28);});
        card.Width=work.ClientSize.Width-36;card.Height=work.ClientSize.Height-card.Top-28;Load();
    }
}
