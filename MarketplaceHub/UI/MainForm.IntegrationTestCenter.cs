using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private void ShowIntegrationTestCenter()
    {
        var store=CurrentStore();if(store is null){ShowInfo("Chọn cửa hàng trước khi mở bảng kiểm tra API.");return;}
        autoSync.Stop();var token=pageCts.Token;
        try{using var dialog=new IntegrationTestCenterDialog(app,store);dialog.ShowDialog(this);}
        finally{if(!IsDisposed&&!lifetimeCts.IsCancellationRequested){autoSync.Start();if(!token.IsCancellationRequested)refreshActivePage?.Invoke();}}
    }
}

public sealed class IntegrationTestCenterDialog : Form
{
    private sealed record ProbeTarget(string OrderId,GtinSyncTarget? Variant)
    {public override string ToString()=>Variant is { } v?$"{v.Sku} · size {v.Size} · {v.VariantId} · {v.Gtin}":"ORDER ID "+OrderId;}
    private readonly AppServices app;
    private readonly StoreProfile store;
    private readonly TextBox credential=new(){Name="liveWbToken",UseSystemPasswordChar=true,Dock=DockStyle.Fill};
    private readonly TextBox nationalKey=new(){Name="liveNationalCatalogKey",UseSystemPasswordChar=true,Dock=DockStyle.Fill};
    private readonly CheckBox sandbox=new(){Text="National Catalog sandbox",AutoSize=true};
    private readonly ComboBox kind=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=210};
    private readonly TextBox search=new(){Width=240,PlaceholderText="Tìm SKU hoặc ORDER ID"};
    private readonly ComboBox target=new(){Name="liveWriteTarget",DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly CheckBox confirmed=new(){Name="liveMutationConfirmed",Text="Tôi xác nhận thay đổi chỉ mục tiêu đã chọn của cửa hàng này",AutoSize=true};
    private readonly Button read=new(){Name="runReadOnlyIntegrationTest",Text="Kiểm tra chỉ đọc",Width=185,Height=38};
    private readonly Button mutate=new(){Name="runLiveMutation",Text="Chạy thử mục tiêu đã xác nhận",Width=270,Height=38,Enabled=false};
    private readonly TextBox report=new(){Name="liveReadOnlyReport",Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,WordWrap=false};
    private readonly CancellationTokenSource lifetime=new();
    private string verifiedSignature="";
    private bool busy;

    public IntegrationTestCenterDialog(AppServices services,StoreProfile profile)
    {
        app=services;store=profile;Text=$"Kiểm tra tích hợp · {profile.Name}";
        Width=1010;Height=780;MinimumSize=new Size(850,650);StartPosition=FormStartPosition.CenterParent;BackColor=C.Main;ForeColor=C.Text;Font=new Font("Segoe UI",10);
        credential.Text=profile.Marketplace==Marketplace.Wildberries?profile.Token:profile.ApiKey;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=8};
        foreach(var height in new[]{72f,82f,42f,42f,40f,50f})layout.RowStyles.Add(new(SizeType.Absolute,height));
        layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,48));Controls.Add(layout);
        layout.Controls.Add(new Label{Name="readOnlyIntegrationWarning",Dock=DockStyle.Fill,
            Text="Mặc định chỉ đọc API: kết nối, sản phẩm và trạng thái. Credential được che và không đưa vào báo cáo.\nThao tác ghi cần chọn một mục tiêu, kiểm tra chỉ đọc thành công và đánh dấu xác nhận."},0,0);
        var fields=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};fields.ColumnStyles.Add(new(SizeType.Absolute,260));fields.ColumnStyles.Add(new(SizeType.Percent,100));
        fields.Controls.Add(new Label{Text=profile.Marketplace+" · Token / API key",AutoSize=true},0,0);fields.Controls.Add(credential,1,0);
        fields.Controls.Add(new Label{Text="National Catalog · API key",AutoSize=true},0,1);fields.Controls.Add(nationalKey,1,1);layout.Controls.Add(fields,0,1);
        var tools=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};kind.Items.AddRange(new object[]{"GTIN một biến thể WB","Nhận một đơn WB"});kind.SelectedIndex=0;
        tools.Controls.Add(kind);tools.Controls.Add(search);tools.Controls.Add(sandbox);layout.Controls.Add(tools,0,2);layout.Controls.Add(target,0,3);layout.Controls.Add(confirmed,0,4);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};actions.Controls.Add(read);actions.Controls.Add(mutate);layout.Controls.Add(actions,0,5);layout.Controls.Add(report,0,6);
        var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var copy=new Button{Name="copyRedactedLiveReport",Text="Sao chép báo cáo đã che",Width=210,Height=35};footer.Controls.Add(copy);layout.Controls.Add(footer,0,7);
        foreach(var box in new[]{credential,nationalKey,search,report}){box.BackColor=C.Card;box.ForeColor=C.Text;box.BorderStyle=BorderStyle.FixedSingle;}
        target.BackColor=kind.BackColor=C.Card;target.ForeColor=kind.ForeColor=C.Text;
        void Reset(){verifiedSignature="";confirmed.Checked=false;UpdateGate();}
        credential.TextChanged+=(_,_)=>Reset();nationalKey.TextChanged+=(_,_)=>Reset();sandbox.CheckedChanged+=(_,_)=>Reset();
        target.SelectedIndexChanged+=(_,_)=>Reset();confirmed.CheckedChanged+=(_,_)=>UpdateGate();kind.SelectedIndexChanged+=(_,_)=>{Reset();LoadTargets();};search.TextChanged+=(_,_)=>{Reset();LoadTargets();};
        read.Click+=async(_,_)=>await RunReadOnlyAsync();mutate.Click+=async(_,_)=>await RunMutationAsync();
        copy.Click+=(_,_)=>{if(report.Text.Length>0)Clipboard.SetText(Redact(report.Text));};
        FormClosing+=(_,_)=>lifetime.Cancel();FormClosed+=(_,_)=>lifetime.Dispose();LoadTargets();
    }

    private StoreProfile ReadProfile()=>store.Marketplace==Marketplace.Wildberries?store with{Token=credential.Text}:store with{ApiKey=credential.Text};
    private string Signature()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{store.Id}\n{store.Marketplace}\n{credential.Text}\n{nationalKey.Text}\n{sandbox.Checked}\n{kind.SelectedIndex}\n{target.SelectedItem}")));
    private void UpdateGate()=>mutate.Enabled=!busy&&store.Marketplace==Marketplace.Wildberries&&target.SelectedItem is ProbeTarget&&confirmed.Checked&&verifiedSignature.Length>0&&verifiedSignature==Signature();
    private void SetBusy(bool value){busy=value;read.Enabled=!value;credential.Enabled=nationalKey.Enabled=sandbox.Enabled=kind.Enabled=search.Enabled=target.Enabled=confirmed.Enabled=!value;UpdateGate();}
    private string Redact(string value)
    {
        foreach(var secret in new[]{credential.Text,nationalKey.Text,store.Token,store.ApiKey}.Where(x=>x.Length>0).OrderByDescending(x=>x.Length)){
            value=value.Replace(secret,"[đã che]",StringComparison.Ordinal).Replace(Uri.EscapeDataString(secret),"[đã che]",StringComparison.Ordinal);
        }return value;
    }
    private void Log(string message){if(!IsDisposed)report.AppendText(Redact(message)+Environment.NewLine);}

    private void LoadTargets()
    {
        target.Items.Clear();var q=search.Text.Trim();
        if(store.Marketplace!=Marketplace.Wildberries){Log("Cửa hàng này hỗ trợ kiểm tra chỉ đọc và đồng bộ National Catalog. Thao tác ghi thử hiện dành cho WB.");return;}
        if(kind.SelectedIndex==0){
            foreach(var row in app.Db.GetGtinMappingPage(store,0,50,q,"mapped").Rows.Where(x=>GtinCode.Normalize(x.Gtin)==x.Gtin&&x.Gtin.Length>0))
                target.Items.Add(new ProbeTarget("",new(row.Sku,row.VariantId,row.ExternalId,row.Size,row.Gtin)));
        }else{
            var truth=new OrderTruthService().Build(store,app.Db.Orders(store.Id),app.Db.WbReceivedOrderIds(store.Id),app.Db.OrderRemoteStates(store.Id));
            foreach(var row in truth.Orders.Where(x=>x.State==OrderTruthState.New&&(q.Length==0||x.ExternalOrderId.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Lines.Any(y=>y.Sku.Contains(q,StringComparison.OrdinalIgnoreCase)))).Take(50))target.Items.Add(new ProbeTarget(row.ExternalOrderId,null));
        }
        UpdateGate();
    }

    private async Task RunReadOnlyAsync()
    {
        if(busy)return;SetBusy(true);verifiedSignature="";confirmed.Checked=false;var signature=Signature();var profile=ReadProfile();var chosen=target.SelectedItem as ProbeTarget;var clock=Stopwatch.StartNew();
        try{
            var connected=await app.Api.TestAsync(profile,lifetime.Token);Log($"{profile.Marketplace} · kết nối · {clock.ElapsedMilliseconds} ms · {connected.Message}");
            var ready=connected.Success;
            if(ready&&chosen?.Variant is { } variant){
                var card=await app.Api.ReadWbGtinCardAsync(profile,variant.ExternalId,lifetime.Token);
                ready=card["vendorCode"]?.ToString()==variant.Sku&&(card["sizes"] as System.Text.Json.Nodes.JsonArray)?.Count(x=>x?["chrtID"]?.ToString()==variant.VariantId)==1;
                Log(ready?$"WB · đúng SKU/size của nmID {variant.ExternalId}, chrtID {variant.VariantId}.":"WB · định danh biến thể không khớp; giữ khóa thao tác ghi.");
            }else if(ready&&chosen is {OrderId.Length:>0}){
                var statuses=await app.Api.GetWbOrderStatusesAsync(profile,new[]{chosen.OrderId},lifetime.Token);var status=statuses[chosen.OrderId];
                ready=status.SupplierStatus=="new"&&!status.WbStatus.Contains("cancel",StringComparison.OrdinalIgnoreCase);Log($"WB · đơn {chosen.OrderId} · {(ready?"đủ điều kiện nhận":"không đủ điều kiện nhận")}");
            }else if(ready){var page=await app.Api.ReadProductCatalogPageAsync(profile,"",lifetime.Token);Log($"Catalog · đọc {page.Entries.Count} sản phẩm / {page.Entries.Sum(x=>x.Variants.Count)} biến thể ở trang đầu.");}
            if(nationalKey.Text.Length>0){
                var result=await app.SyncZnackGtinAsync(profile,new(nationalKey.Text,"",sandbox.Checked),lifetime.Token);Log("National Catalog · "+result.Message);
                if(chosen?.Variant is not null)ready&=result.Success;
            }else if(chosen?.Variant is not null){ready=false;Log("Nhập API key National Catalog để xác minh GTIN trước thao tác ghi thử.");}
            lifetime.Token.ThrowIfCancellationRequested();if(ready&&signature==Signature())verifiedSignature=signature;
            Log(verifiedSignature.Length>0?"Kiểm tra chỉ đọc đạt. Chọn xác nhận để mở đúng thao tác thử đã chọn.":"Chưa mở thao tác ghi. Kiểm tra thông tin cửa hàng, mapping hoặc credential rồi thử lại.");
        }catch(OperationCanceledException){Log("Đã dừng kiểm tra; không gửi thao tác ghi.");}catch(Exception){Log("Chưa đọc đủ dữ liệu API. Kiểm tra quyền credential, kết nối và quota rồi thử lại.");}
        finally{if(!IsDisposed)SetBusy(false);}
    }

    private async Task RunMutationAsync()
    {
        if(!mutate.Enabled||target.SelectedItem is not ProbeTarget chosen||verifiedSignature!=Signature())return;
        var profile=ReadProfile();SetBusy(true);
        try{
            if(chosen.Variant is { } variant){
                if(!app.Db.ConfirmedGtinMappings(profile).TryGetValue((variant.Sku,variant.VariantId),out var currentGtin)||currentGtin!=variant.Gtin)
                    throw new InvalidOperationException("Mapping đã thay đổi sau khi kiểm tra. Xác minh lại trước khi ghi.");
                app.QueueWbGtinWritebackForVariant(profile,variant.Sku,variant.VariantId);
                var result=await app.ResumeWbGtinWritebackAsync(profile,lifetime.Token);Log("WB · một biến thể · "+result.Message);
            }else{
                var supplies=await app.Api.GetWbTodaySuppliesAsync(profile,lifetime.Token);
                using var chooser=new WbShipmentDialog(1,supplies);if(chooser.ShowDialog(this)!=DialogResult.OK)return;
                var rows=app.Db.Orders(store.Id).Where(x=>x.ExternalOrderId==chosen.OrderId&&x.Marketplace==Marketplace.Wildberries).ToArray();
                var result=await app.ReceiveWbOrdersAsync(profile,rows,chooser.Choice,lifetime.Token);Log($"WB · nhận đơn {chosen.OrderId} · Supply ID {result.SupplyId} · {result.Message}");
                Log("Mở Đang đóng gói → shipment này để tự gán KIZ và xuất nhãn.");
            }
        }catch(OperationCanceledException){Log("Đã dừng; checkpoint được giữ để đối soát trước khi thử lại.");}catch(Exception){Log("Thao tác chưa được xác minh. Đối soát checkpoint hiện tại trước khi chọn lượt thử khác.");}
        finally{verifiedSignature="";if(!IsDisposed){confirmed.Checked=false;SetBusy(false);}}
    }
}
