using MarketplaceHub.Core;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed class WbShipmentDialog : Form
{
    private readonly RadioButton create=new(){Text="Tạo shipment mới",AutoSize=true,Checked=true};
    private readonly RadioButton existing=new(){Text="Thêm vào shipment đã tạo hôm nay",AutoSize=true};
    private readonly TextBox name=new(){Width=560};
    private readonly ComboBox supplies=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=560};
    private readonly Button go=new(){Text="Thêm đơn → tự gắn KIZ → xuất nhãn",Width=360,Height=40,DialogResult=DialogResult.OK};
    public WbShipmentChoice Choice=>existing.Checked && supplies.SelectedItem is WbSupply supply ? new(supply.Id,supply.Name) : new(null,name.Text.Trim());

    public WbShipmentDialog(int orderCount,IReadOnlyList<WbSupply> today,bool preferExisting=false,bool allowCreate=true)
    {
        Text=allowCreate?"Chọn shipment WB":"Mở shipment WB hôm nay";
        Width=630;Height=365;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;MinimizeBox=false;BackColor=C.Main;ForeColor=C.Text;Font=new Font("Segoe UI",10);
        var body=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        var day=MarketplaceGateway.WbBusinessDay(DateTimeOffset.UtcNow);
        body.Controls.Add(new Label{AutoSize=true,Text=$"{orderCount} đơn đã chọn · {day:dd/MM/yyyy} · giờ Moscow"});
        name.Text="MarketplaceHub "+DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm");name.MaxLength=128;
        foreach(var supply in today.Where(x=>!x.Done && MarketplaceGateway.WbBusinessDay(x.CreatedAt)==day))supplies.Items.Add(supply);
        if(supplies.Items.Count>0)supplies.SelectedIndex=0;
        create.Visible=allowCreate;name.Visible=allowCreate;
        if(preferExisting || !allowCreate){create.Checked=false;existing.Checked=true;}
        body.Controls.Add(create);body.Controls.Add(name);body.Controls.Add(existing);body.Controls.Add(supplies);
        body.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(560,0),Text="Chỉ dùng shipment đang mở của cửa hàng này. KIZ và sticker được xử lý sau khi WB xác nhận đã thêm đơn."});
        if(!allowCreate)go.Text="Mở shipment";
        var buttons=new FlowLayoutPanel{Width=560,Height=45};buttons.Controls.Add(go);
        var cancel=new Button{Text="Hủy",DialogResult=DialogResult.Cancel,Width=100,Height=40};buttons.Controls.Add(cancel);body.Controls.Add(buttons);
        Controls.Add(body);AcceptButton=go;CancelButton=cancel;
        void UpdateChoice(){name.Enabled=create.Checked;supplies.Enabled=existing.Checked;go.Enabled=existing.Checked?supplies.SelectedItem is WbSupply:!string.IsNullOrWhiteSpace(name.Text);}
        create.CheckedChanged+=(_,_)=>UpdateChoice();existing.CheckedChanged+=(_,_)=>UpdateChoice();name.TextChanged+=(_,_)=>UpdateChoice();supplies.SelectedIndexChanged+=(_,_)=>UpdateChoice();UpdateChoice();
    }
}
