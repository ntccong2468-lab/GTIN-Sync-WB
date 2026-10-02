using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private string ResolveMarketplaceGtin(StoreProfile store,MarketplaceFbsItem item)
    {
        var product=app.Db.Products(store.Id).SingleOrDefault(x=>x.Marketplace==store.Marketplace && x.Sku==item.Offer);
        if(product is null)return "";
        var variants=ProductCatalog.Entry(product).Variants;
        var explicitCodes=ProductCatalog.Strings(item.Raw["barcodes"]).Concat(item.Raw["barcode"] is JsonValue b?new[]{b.ToString()}:Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var matched=(explicitCodes.Count>0?variants.Where(v=>v.Barcodes.Any(explicitCodes.Contains)):variants).ToArray();
        if(matched.Length==1&&app.Db.ConfirmedGtinMappings(store).TryGetValue((product.Sku,matched[0].VariantId),out var confirmed))return confirmed;
        var gtins=matched.SelectMany(v=>v.Barcodes).Where(b=>explicitCodes.Count==0 || explicitCodes.Contains(b)).Select(ProductCatalog.NormalizeGtin).Where(g=>g.Length>0).Distinct(StringComparer.Ordinal).ToArray();
        return gtins.Length==1?gtins[0]:"";
    }

    private async Task BeginMarketplaceFbsAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> selected,CancellationToken originToken,bool useKiz=true)
    {
        if(selected.Count==0){ShowInfo("Hãy chọn đơn cần nhận.");return;}
        if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token)){ShowInfo("Đang xử lý một lượt nhận đơn.");return;}
        try {
            using var chooser=new MarketplaceBatchDialog(store,selected.Select(x=>x.ExternalOrderId).Distinct().Count(),app.Db.TodayMarketplaceFbsBatches(store));
            if(chooser.ShowDialog(this)!=DialogResult.OK||originToken.IsCancellationRequested)return;
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            using var dialog=new WbPrintProgressDialog(()=>stop.Cancel()){Text=store.Marketplace+" · nhận đơn vào shipment"};dialog.Show(this);
            PriceUpdateResult result;try {result=await app.ReceiveMarketplaceFbsAsync(store,selected.Select(x=>x.ExternalOrderId),stop.Token,new Progress<string>(text=>{if(!dialog.IsDisposed)dialog.Report(text);}),chooser.ExistingId);}finally{dialog.Close();}
            if(!originToken.IsCancellationRequested&&!IsDisposed&&CurrentStore()?.Id==store.Id)ShowFbsWorkspace("new",result.Message);
        }catch(OperationCanceledException){}catch(Exception ex){if(!originToken.IsCancellationRequested)ShowInfo(ex.Message);}finally{fbsOperations.Release();}
    }

    private async Task<Dictionary<string,JsonArray>?> ReadMarketplaceLayoutsAsync(StoreProfile store,string batchId,CancellationToken token)
    {
        var layouts=new Dictionary<string,JsonArray>(StringComparer.Ordinal);
        if(store.Marketplace!=Marketplace.Yandex)return layouts;
        foreach(var order in app.Db.MarketplaceFbsBatchOrders(store,batchId)) {
            if(order.Layout.Length>0)continue;
            var fresh=await app.ReadFreshMarketplaceFbsAsync(store,order.Id,token);
            if(token.IsCancellationRequested || CurrentStore()?.Id!=store.Id)return null;
            var boxes=(fresh.Raw["delivery"]?["shipments"] as JsonArray??new()).SelectMany(x=>x?["boxes"] as JsonArray??new()).ToArray();
            if(fresh.CanPack && boxes.Any(x=>x?["items"] is not JsonArray)) {
                using var layout=new MarketplaceLayoutDialog(fresh);
                if(layout.ShowDialog(this)!=DialogResult.OK)return null;
                layouts[order.Id]=layout.BoxLayout;
            }
        }
        return layouts;
    }

    private Task ExportMarketplaceFbsLabelsAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> selected,CancellationToken token)
        => BeginMarketplaceFbsAsync(store,selected,token);

    private void ShowMarketplaceFbsBatch(StoreProfile store,string batchId,string? message=null)
    {
        ClearWork();var token=pageCts.Token;activePage=ShowFbs;
        var back=IconButton("←");back.Click+=(_,_)=>ShowFbsPacking();work.Controls.Add(back);
        var title=Title(store.Marketplace+" · lượt đóng hàng");title.Left=55;work.Controls.Add(title);
        var retry=ActionButton("Chuẩn bị KIZ + đóng gói",240,true);retry.Left=0;retry.Top=55;work.Controls.Add(retry);
        var export=ActionButton("↓ Xuất nhãn sàn + KIZ",245,true);export.Left=250;export.Top=55;work.Controls.Add(export);
        var state=new Label{Left=0,Top=108,Height=55,ForeColor=C.Muted,AutoEllipsis=true,Text=message??"Đọc lại trạng thái và từng KIZ trước khi xuất nhãn. Nhãn sàn giữ nguyên PDF chính thức."};work.Controls.Add(state);
        var card=CardPanel();card.Left=0;card.Top=180;work.Controls.Add(card);var grid=DarkGrid();grid.Name="marketplaceBatchOrders";grid.Dock=DockStyle.Fill;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="order",HeaderText="Posting / đơn",Width=190});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="items",HeaderText="Toàn bộ hàng trong đơn",MinimumWidth=220,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="state",HeaderText="Đóng gói / KIZ",Width=260});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="error",HeaderText="Cần xử lý",Width=240});card.Controls.Add(grid);
        foreach(var member in app.Db.MarketplaceFbsBatchOrders(store,batchId)) {
            var rows=app.Db.Orders(store.Id).Where(x=>x.ExternalOrderId==member.Id).ToArray();var marks=app.Db.MarketplaceKizReservations(store,member.Id);var assigned=marks.Count(x=>x.Status=="ASSIGNED");
            var row=grid.Rows.Add(member.Id,string.Join("\n",rows.Select(x=>x.Sku+" · "+x.Name+" × "+x.Quantity)),member.Status is "PACKED" or "LABELS_READY"?$"Sàn đã xác nhận đóng · {assigned} KIZ":member.Status=="AMBIGUOUS"?"Đang đối soát lệnh đóng hàng":"Chưa xác nhận đóng · "+marks.Count+" KIZ đã giữ",member.Error);grid.Rows[row].Height=110;
        }
        retry.Click+=async(_,_)=>await ShowFbsLabelJobAsync(new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,batchId),token);
        export.Click+=async(_,_)=>await ExportMarketplaceBatchCoreAsync(store,batchId,token,true);
        SetWorkResize((_,_)=>{
            var available=work.ClientSize.Width-25;var open=work.Controls.OfType<Button>().FirstOrDefault(b=>b.Name=="openMarketplaceLabels");
            if(open is not null){open.Left=available<810?0:510;open.Top=available<810?105:55;}
            state.Top=open is not null && available<810?158:108;state.Width=available;card.Top=state.Bottom+17;card.Width=available;card.Height=Math.Max(230,work.ClientSize.Height-card.Top-25);
            grid.Columns["order"].Width=Math.Clamp((int)(available*.18),120,180);grid.Columns["state"].Width=Math.Clamp((int)(available*.23),170,240);grid.Columns["error"].Width=Math.Clamp((int)(available*.25),180,260);
        });refreshActivePage=null;
    }

    private async Task ExportMarketplaceBatchAsync(StoreProfile store,string batchId,CancellationToken token,bool reprint=false)
    {
        if(!await fbsOperations.WaitAsync(0,lifetimeCts.Token))return;
        try{await ExportMarketplaceBatchCoreAsync(store,batchId,token,reprint);}finally{fbsOperations.Release();}
    }
    private Task ExportMarketplaceBatchCoreAsync(StoreProfile store,string batchId,CancellationToken token,bool reprint=false)
        =>ShowFbsLabelJobAsync(new(store.Id,store.Marketplace,LabelTargetKind.MarketplaceBatch,batchId),token);

}

internal sealed class MarketplaceBatchDialog:Form
{
    private readonly RadioButton existing=new(){Text="Thêm vào lượt hôm nay",AutoSize=true};private readonly ComboBox batches=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=500};
    public string? ExistingId=>existing.Checked && batches.SelectedItem is MarketplaceFbsBatch b?b.Id:null;
    public MarketplaceBatchDialog(StoreProfile store,int count,IReadOnlyList<MarketplaceFbsBatch> rows,bool browse=false)
    {
        Text=store.Marketplace+" · nhận "+count+" đơn FBS";ClientSize=new Size(600,250);BackColor=C.Main;ForeColor=C.Text;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        var create=new RadioButton{Left=25,Top=30,Text="Tạo lượt đóng hàng mới · hôm nay (Moscow)",Checked=true,AutoSize=true};create.Enabled=!browse;create.Checked=!browse;Controls.Add(create);existing.Checked=browse;existing.Text=browse?"Mở lượt đã lưu / tiếp tục lượt chưa xong":"Thêm vào lượt hôm nay";existing.Left=25;existing.Top=75;existing.Enabled=rows.Count>0;Controls.Add(existing);batches.Left=25;batches.Top=110;batches.DisplayMember="Name";foreach(var b in rows)batches.Items.Add(b);if(rows.Count>0)batches.SelectedIndex=0;Controls.Add(batches);
        var go=new Button{Left=25,Top=180,Width=360,Text=browse?"Mở lượt đã lưu":"Nhận đơn vào shipment",DialogResult=DialogResult.OK};Controls.Add(go);var cancel=new Button{Left=410,Top=180,Width=100,Text="Hủy",DialogResult=DialogResult.Cancel};Controls.Add(cancel);AcceptButton=go;CancelButton=cancel;
    }
}
internal sealed class MarketplaceLayoutDialog:Form
{
    private readonly TextBox text=new(){Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false};public JsonArray BoxLayout=>JsonNode.Parse(text.Text) as JsonArray??throw new InvalidOperationException("Phân bổ phải là mảng boxes JSON.");
    public MarketplaceLayoutDialog(MarketplaceFbsSnapshot snapshot)
    {
        Text="Yandex · xác nhận các hộp của đơn "+snapshot.OrderId;ClientSize=new Size(760,520);BackColor=C.Main;ForeColor=C.Text;StartPosition=FormStartPosition.CenterParent;
        var info=new Label{Left=20,Top=15,Width=710,Height=70,Text="Yandex đã cấp mã hộp nhưng chưa trả phân bố hàng. Kiểm tra/phân bổ đúng các hộp thực tế trước khi gửi KIZ. Mỗi item phải giữ đủ số lượng; ứng dụng không xóa hàng."};Controls.Add(info);
        text.SetBounds(20,95,710,345);text.Text=new JsonArray(new JsonObject{["items"]=new JsonArray(snapshot.Items.Select(x=>(JsonNode)new JsonObject{["id"]=long.Parse(x.Id),["fullCount"]=x.Quantity,["instances"]=x.Raw["instances"]?.DeepClone()}).ToArray())}).ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true});Controls.Add(text);
        var ok=new Button{Left=20,Top=465,Width=300,Text="Xác nhận phân bổ đầy đủ"};ok.Click+=(_,_)=>{try{_=BoxLayout;DialogResult=DialogResult.OK;Close();}catch(Exception ex){MessageBox.Show(this,ex.Message);}};Controls.Add(ok);var cancel=new Button{Left=350,Top=465,Width=100,Text="Hủy",DialogResult=DialogResult.Cancel};Controls.Add(cancel);CancelButton=cancel;
    }
}
