using MarketplaceHub.Core;
using MarketplaceHub.Services;
using MarketplaceHub.Services.Suz;
namespace MarketplaceHub.UI;
public sealed class KizWorkflowProfileDialog:Form
{
    public KizWorkflowProfileDialog(AppServices app,StoreProfile store)
    {
        var old=app.Db.SuzProfileForStore(store.Id);Text="Profile KIZ · "+store.Name;Size=new(760,540);MinimumSize=new(640,480);StartPosition=FormStartPosition.CenterParent;BackColor=C.Main;ForeColor=C.Text;Font=new("Segoe UI",10);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(20),ColumnCount=1,RowCount=9};Controls.Add(layout);for(var i=0;i<8;i++)layout.RowStyles.Add(new(SizeType.Absolute,i==7?84:40));layout.RowStyles.Add(new(SizeType.Percent,100));
        layout.Controls.Add(new Label{Text="Pháp nhân sở hữu KIZ · INN 10 hoặc 12 chữ số",AutoSize=true},0,0);var owner=new TextBox{Name="kizProfileOwner",Text=old?.OwnerInn??"",Dock=DockStyle.Fill};layout.Controls.Add(owner,0,1);
        var business=new ComboBox{Name="kizProfileBusiness",DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};business.Items.AddRange(new object[]{"Chọn nghiệp vụ","Sản xuất · lp / UNIT / template 10","Nhập khẩu / bán lại · chưa hỗ trợ mua tự động"});business.SelectedIndex=old is null?0:old.ReleaseMethodType=="PRODUCTION"?1:2;layout.Controls.Add(business,0,2);
        var environment=new ComboBox{Name="kizProfileEnvironment",DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};environment.Items.AddRange(new object[]{"Chọn môi trường","Production","Sandbox · chưa có contract xác minh"});environment.SelectedIndex=old is null?0:old.Environment=="Production"?1:2;layout.Controls.Add(environment,0,3);
        var buy=new CheckBox{Name="kizProfileAutoPurchase",Text="Cho phép mua KIZ còn thiếu khi xuất nhãn (có chi phí)",AutoSize=true,Checked=old?.AutoPurchaseEnabled==true};layout.Controls.Add(buy,0,4);
        var capability=new Label{Name="kizProfileCapability",Dock=DockStyle.Fill,Text=old?.ContractEnabled==true?"Capability đã xác minh cho profile này":"Chưa xác minh capability. Chạy ReadOnly và phép thử một mã trong Test Center."};layout.Controls.Add(capability,0,5);
        var info=new Label{Text="Thông tin chứng thư / OMS dùng cấu hình Честный ЗНАК hiện có. Trạng thái pháp lý được đọc từ API; xác nhận dán tem không sửa trạng thái pháp lý.",Dock=DockStyle.Fill};layout.Controls.Add(info,0,7);
        var save=new Button{Text="Lưu profile",AutoSize=true};layout.Controls.Add(save,0,8);
        void RefreshCapability(){buy.Enabled=business.SelectedIndex==1&&environment.SelectedIndex==1&&old?.ContractEnabled==true&&old.CredentialVersion==app.Db.ZnakCredentialVersion();if(!buy.Enabled)buy.Checked=false;capability.Text=business.SelectedIndex==2||environment.SelectedIndex==2?"Unsupported: nghiệp vụ/môi trường chưa có contract xác minh.":old?.ContractEnabled==true?"Capability đã xác minh · kiểm tra lại sau thay đổi scope/credential":"Chưa xác minh capability · mua tự động đang khóa.";}
        business.SelectedIndexChanged+=(_,_)=>RefreshCapability();environment.SelectedIndexChanged+=(_,_)=>RefreshCapability();RefreshCapability();
        save.Click+=(_,_)=>{
            if(owner.Text.Length is not(10 or 12)||!owner.Text.All(char.IsAsciiDigit)||business.SelectedIndex==0||environment.SelectedIndex==0){capability.Text="Chọn rõ INN, nghiệp vụ và môi trường.";return;}
            var same=old is not null&&old.OwnerInn==owner.Text&&old.Environment==(environment.SelectedIndex==1?"Production":"Sandbox")&&old.ReleaseMethodType==(business.SelectedIndex==1?"PRODUCTION":"IMPORT")&&old.CredentialVersion==app.Db.ZnakCredentialVersion();
            var profile=new SuzProfile(same?old!.Id:Guid.NewGuid().ToString("N"),store.Id,same?old!.Version+1:1,owner.Text,environment.SelectedIndex==1?"Production":"Sandbox","lp",business.SelectedIndex==1?"PRODUCTION":"IMPORT","UNIT",10,app.Db.ZnakCredentialVersion(),same&&buy.Checked,same&&old!.ContractEnabled,new("https://suzgrid.crpt.ru"),new("https://markirovka.crpt.ru"));
            if(!SuzProfileCatalog.Supported(profile))profile=profile with{AutoPurchaseEnabled=false,ContractEnabled=false};app.Db.SaveSuzProfile(profile);DialogResult=DialogResult.OK;Close();
        };
    }
}
