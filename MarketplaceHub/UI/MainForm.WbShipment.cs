using MarketplaceHub.Core;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private IReadOnlyDictionary<string,string> ResolveWbGtins(StoreProfile store,IReadOnlyList<FbsOrderRow> orders)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var order in orders)
        {
            try {
                var product=FindProductForOrder(store,order);
                var identity=BuildWbPrintOrder(order,product,new LabelResult(true,"identity"),Array.Empty<string>(),true);
                result[order.ExternalOrderId]=ProductCatalog.NormalizeGtin(identity.Barcode);
            }
            catch(InvalidOperationException){result[order.ExternalOrderId]="";}
        }
        return result;
    }

    private async Task BeginWbShipmentAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> selected,bool useKiz,CancellationToken pageToken,bool preferExisting=false)
    {
        if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token)){ShowInfo("Đang có tác vụ shipment/KIZ. Hãy chờ hoàn tất.");return;}
        WbReceiveResult? result=null;
        try
        {
            var supplies=await app.Api.GetWbTodaySuppliesAsync(store,pageToken);
            if(pageToken.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
            using var choice=new WbShipmentDialog(selected.Select(x=>x.ExternalOrderId).Distinct().Count(),supplies,preferExisting);
            if(choice.ShowDialog(this)!=DialogResult.OK)return;
            if(pageToken.IsCancellationRequested || CurrentStore()?.Id!=store.Id)return;
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            using var dialog=new WbPrintProgressDialog(()=>stop.Cancel()){Text="WB · nhận đơn vào shipment"};
            dialog.Show(this);
            var progress=new Progress<string>(text=>{if(!dialog.IsDisposed)dialog.Report(text);});
            try
            {
                var ids=selected.Select(x=>x.ExternalOrderId).ToHashSet(StringComparer.Ordinal);
                var complete=app.Db.Orders(store.Id).Where(x=>ids.Contains(x.ExternalOrderId)).ToArray();
                result=await app.ReceiveWbOrdersAsync(store,complete,choice.Choice,stop.Token,progress);
            }
            finally {dialog.Close();}
        }
        catch(OperationCanceledException){return;}
        catch(Exception ex){if(!pageToken.IsCancellationRequested && !IsDisposed)ShowInfo(ex.Message);return;}
        finally {fbsOperations.Release();}
        if(result is null)return;
        app.Db.Audit("FBS WB",result.Success?"Đã nhận đơn vào shipment":"Shipment cần tiếp tục",result.Message);
        if(pageToken.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
        if(!string.IsNullOrWhiteSpace(result.SupplyId))
            ShowFbsWorkspace("new",result.Message);
        else ShowInfo(result.Message);
    }

    private async Task ExportWbSupplyAsync(StoreProfile store,string supplyId,CancellationToken originToken)
    {
        if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token)){ShowInfo("Đang xử lý shipment/KIZ.");return;}
        PriceUpdateResult? result=null;
        try {
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            using var dialog=new WbPrintProgressDialog(()=>stop.Cancel()){Text="WB · gắn KIZ trước khi xuất nhãn"};dialog.Show(this);
            var progress=new Progress<string>(text=>{if(!dialog.IsDisposed)dialog.Report(text);});
            try {
                var orders=await app.ReadWbSupplyOrdersAsync(store,supplyId,stop.Token);
                result=await app.EnsureWbSupplyKizAsync(store,orders,ResolveWbGtins(store,orders),true,stop.Token,progress);
            } finally {dialog.Close();}
        }
        catch(Exception ex){result=new(false,ex.Message);}
        finally{fbsOperations.Release();}
        if(originToken.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
        await OpenWbSupplyAsync(store,supplyId,originToken,result?.Success==true,result?.Success==true?null:result?.Message);
    }

    private async Task OpenTodayWbSupplyAsync(StoreProfile store,CancellationToken pageToken)
    {
        try {
            var today=await app.Api.GetWbTodaySuppliesAsync(store,pageToken);
            if(pageToken.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
            using var dialog=new WbShipmentDialog(0,today,true,false);
            if(dialog.ShowDialog(this)!=DialogResult.OK || dialog.Choice.SupplyId is null)return;
            await OpenWbSupplyAsync(store,dialog.Choice.SupplyId,pageToken);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!pageToken.IsCancellationRequested && !IsDisposed)ShowInfo(ex.Message);}
    }

    private async Task OpenWbSupplyAsync(StoreProfile store,string supplyId,CancellationToken originToken,bool export=false,string? warning=null,bool useKiz=true)
    {
        try
        {
            var supply=await app.Api.GetWbSupplyAsync(store,supplyId,originToken);
            var rows=await app.ReadWbSupplyOrdersAsync(store,supplyId,originToken);
            IReadOnlyDictionary<string,WbPrintKizMetadata> marking=new Dictionary<string,WbPrintKizMetadata>();
            try {marking=await app.Api.GetWbPrintKizAsync(store,rows.Select(x=>x.ExternalOrderId),originToken);}
            catch(OperationCanceledException){throw;}
            catch(Exception ex){warning=string.IsNullOrWhiteSpace(warning)?ex.Message:warning+"\n"+ex.Message;export=false;}
            if(originToken.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
            ShowWbSupplyDetail(store,supply,rows,marking,warning);
            if(export && rows.Count>0)await PrepareWbPrintAsync(store,rows,useKiz,pageCts.Token);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!originToken.IsCancellationRequested && !IsDisposed)ShowInfo($"Shipment {supplyId} được giữ lại. {ex.Message}");}
    }

    private void ShowWbSupplyDetail(StoreProfile store,WbSupply supply,IReadOnlyList<FbsOrderRow> orders,
        IReadOnlyDictionary<string,WbPrintKizMetadata> marking,string? warning)
    {
        ClearWork();var token=pageCts.Token;activePage=ShowFbs;
        if(orders.Any(x=>x.Quantity<=0))warning="Shipment còn đơn chưa tải được chi tiết sản phẩm. Đã hiển thị đủ mã đơn; đồng bộ lại trước khi gắn KIZ hoặc xuất nhãn.\n"+warning;
        var back=IconButton("←");back.Left=0;back.Top=0;back.Click+=(_,_)=>ShowFbsPacking();work.Controls.Add(back);
        var title=Title("Supply "+supply.Id+" · "+orders.Count+" đơn");title.Left=55;title.AutoSize=false;title.AutoEllipsis=true;title.Height=45;work.Controls.Add(title);
        var export=ActionButton("↓ Xuất nhãn dán",180,true);var deliver=ActionButton("Chuyển sang giao hàng",225,true);
        export.Top=0;deliver.Top=0;work.Controls.Add(export);work.Controls.Add(deliver);export.Enabled=orders.Count>0;deliver.Enabled=!supply.Done && orders.Count>0;
        var options=new FlowLayoutPanel{Left=0,Top=55,Height=40,Width=650,BackColor=C.Main,WrapContents=false};
        var fields=new Dictionary<string,CheckBox>();
        foreach(var text in new[]{"Danh mục","Article","Màu","Size"}) {var check=new CheckBox{Text=text,Checked=true,AutoSize=true,ForeColor=C.Text,Margin=new Padding(0,8,20,0)};fields[text]=check;options.Controls.Add(check);}
        work.Controls.Add(options);
        var state=new Label{Left=0,Top=98,Height=46,Width=900,ForeColor=string.IsNullOrWhiteSpace(warning)?C.Muted:C.Orange,
            Text=string.IsNullOrWhiteSpace(warning)?"KIZ xanh đã đọc lại từ WB, đúng GTIN và đúng chủ sở hữu. Xuất nhãn sẽ tự xử lý các đơn còn thiếu mã.":warning,AutoEllipsis=true};work.Controls.Add(state);
        var card=CardPanel();card.Left=0;card.Top=150;work.Controls.Add(card);
        var grid=DarkGrid();grid.Name="wbSupplyOrders";grid.Dock=DockStyle.Fill;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="number",HeaderText="STT",Width=50});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="order",HeaderText="Mã nhiệm vụ",Width=175});
        grid.Columns.Add(new DataGridViewImageColumn{Name="image",HeaderText="Ảnh",Width=100,ImageLayout=DataGridViewImageCellLayout.Zoom,DefaultCellStyle=new DataGridViewCellStyle{NullValue=null}});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="detail",HeaderText="Thông tin chi tiết",MinimumWidth=220,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="kiz",HeaderText="KIZ trên WB",Width=160});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="price",HeaderText="Giá",Width=100});card.Controls.Add(grid);
        void RenderRows()
        {
            DisposeImages(card);grid.Rows.Clear();var number=0;
            var errors=app.WbSupplyKizErrors(store,orders,ResolveWbGtins(store,orders),marking);
            foreach(var order in orders)
            {
                var product=FindProductForOrder(store,order);
                WbPrintOrder? identity=null;try {identity=BuildWbPrintOrder(order,product,new(true,"identity"),Array.Empty<string>(),true);}catch(InvalidOperationException){}
                var meta=product is null?new ProductMetaValue("","","","","","",order.Sku,""):ProductMeta(product);
                var parts=new List<string>{product?.Name??order.Name};
                if(fields["Article"].Checked)parts.Add("Article: "+(identity?.Article??meta.Article));
                if(fields["Màu"].Checked)parts.Add("Màu: "+(identity?.Color??meta.Color));
                if(fields["Size"].Checked)parts.Add("Size: "+(identity?.Size??"Chưa xác định"));
                if(fields["Danh mục"].Checked)parts.Add("Danh mục: "+meta.Category);
                marking.TryGetValue(order.ExternalOrderId,out var kiz);
                var required=order.NeedsKiz || kiz?.Required==true;
                var known=kiz is not null;var verified=known && kiz!.Codes.Count==1 && !errors.ContainsKey(order.ExternalOrderId);
                var status=verified?"✓ Đã tích KIZ":kiz?.Codes.Count>0?"Cần đối soát KIZ":required?"Chưa gắn KIZ":known?"Không yêu cầu KIZ":"Chưa xác nhận WB";
                var row=grid.Rows.Add(++number,order.ExternalOrderId+"\n"+FormatOrderTime(order),null,string.Join("\n",parts),status,product?.Price is null?"":$"{product.Price:0.##} ₽");
                grid.Rows[row].Height=135;grid.Rows[row].Tag=order;
                grid.Rows[row].Cells["kiz"].Style.ForeColor=verified?C.Green:C.Muted;
                var image=ProductImageUrl(product);if(!string.IsNullOrWhiteSpace(image))_=LoadImageAsync(grid,row,2,image);
            }
        }
        foreach(var check in fields.Values)check.CheckedChanged+=(_,_)=>RenderRows();RenderRows();
        export.Click+=async(_,_)=>
        {
            if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token)){ShowInfo("Đang xử lý shipment/KIZ.");return;}
            export.Enabled=false;deliver.Enabled=false;PriceUpdateResult? result=null;
            try {
                using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
                using var progressDialog=new WbPrintProgressDialog(()=>stop.Cancel()){Text="WB · tự gắn KIZ trước khi xuất nhãn"};progressDialog.Show(this);
                var progress=new Progress<string>(text=>{if(!progressDialog.IsDisposed)progressDialog.Report(text);});
                try {
                    var current=await app.ReadWbSupplyOrdersAsync(store,supply.Id,stop.Token);
                    result=await app.EnsureWbSupplyKizAsync(store,current,ResolveWbGtins(store,current),true,stop.Token,progress);
                } finally {progressDialog.Close();}
            }
            catch(Exception ex){result=new(false,ex.Message);}
            finally {fbsOperations.Release();}
            if(token.IsCancellationRequested || IsDisposed || CurrentStore()?.Id!=store.Id)return;
            try {await OpenWbSupplyAsync(store,supply.Id,token,result?.Success==true,result?.Success==true?null:result?.Message);}
            finally {if(!token.IsCancellationRequested && !export.IsDisposed){export.Enabled=orders.Count>0;deliver.Enabled=!supply.Done && orders.Count>0;}}
        };
        deliver.Click+=async(_,_)=>
        {
            if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token)){ShowInfo("Đang xử lý shipment/KIZ.");return;}
            deliver.Enabled=false;export.Enabled=false;
            try {
                var current=await app.ReadWbSupplyOrdersAsync(store,supply.Id,lifetimeCts.Token);
                var result=await app.DeliverVerifiedWbSupplyAsync(store,supply.Id,ResolveWbGtins(store,current),lifetimeCts.Token);
                app.Db.Audit("FBS WB",result.Success?"Giao shipment":"Lỗi giao shipment",result.Message);
                if(token.IsCancellationRequested || IsDisposed)return;
                if(!result.Success)throw new InvalidOperationException(result.Message);
                await OpenWbSupplyAsync(store,supply.Id,token);
            }
            catch(Exception ex){if(!token.IsCancellationRequested && !IsDisposed)ShowInfo(ex.Message);}
            finally {fbsOperations.Release();if(!deliver.IsDisposed){deliver.Enabled=!supply.Done;export.Enabled=true;}}
        };
        SetWorkResize((_,_)=> {
            var compact=work.ClientSize.Width<1000;
            export.Top=deliver.Top=compact?55:0;
            deliver.Left=compact?190:work.ClientSize.Width-deliver.Width-12;export.Left=compact?0:deliver.Left-export.Width-10;
            title.Width=compact?work.ClientSize.Width-title.Left-25:export.Left-title.Left-10;
            options.Top=compact?105:55;options.Width=work.ClientSize.Width-25;state.Top=options.Bottom+3;state.Width=work.ClientSize.Width-25;card.Top=state.Bottom+6;
            card.Width=work.ClientSize.Width-25;card.Height=Math.Max(240,work.ClientSize.Height-card.Top-20);
            grid.Columns["number"].Width=45;grid.Columns["order"].Width=compact?140:175;grid.Columns["image"].Width=compact?80:100;grid.Columns["kiz"].Width=compact?135:160;grid.Columns["price"].Width=compact?80:100;
        });
        refreshActivePage=null;
    }
}
