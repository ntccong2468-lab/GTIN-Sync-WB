using MarketplaceHub.Core;

namespace MarketplaceHub.UI;

public sealed class WbReceiveResultDialog : Form
{
    public WbReceiveResultDialog(WbReceiveResult result)
    {
        Text="WB · Kết quả nhận đơn";StartPosition=FormStartPosition.CenterParent;
        Size=new(850,540);MinimumSize=new(650,400);BackColor=C.Main;ForeColor=C.Text;
        Font=new("Segoe UI",10);MinimizeBox=false;MaximizeBox=false;
        var header=new Panel{Dock=DockStyle.Top,Height=100,Padding=new(16)};
        header.Controls.Add(new Label{Name="wbReceiveSupplyId",Text="Shipment: "+(result.SupplyId??"Chưa tạo"),Dock=DockStyle.Top,Height=30,ForeColor=C.Text,Font=new("Segoe UI",11,FontStyle.Bold)});
        header.Controls.Add(new Label{Text=result.Message,Dock=DockStyle.Bottom,Height=45,ForeColor=result.Success?C.Green:C.Orange,AutoEllipsis=true});
        var grid=new DataGridView{Name="wbReceiveOutcomes",Dock=DockStyle.Fill,ReadOnly=true,
            AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=C.Card,
            BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
        grid.DefaultCellStyle.BackColor=C.Card;grid.DefaultCellStyle.ForeColor=C.Text;
        grid.DefaultCellStyle.SelectionBackColor=C.Border;grid.DefaultCellStyle.SelectionForeColor=C.Text;
        grid.ColumnHeadersDefaultCellStyle.BackColor=C.Main;grid.ColumnHeadersDefaultCellStyle.ForeColor=C.Muted;
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="order",HeaderText="ORDER ID",FillWeight=20});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="outcome",HeaderText="KẾT QUẢ",FillWeight=30});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="detail",HeaderText="CHI TIẾT",FillWeight=50});
        foreach(var order in result.Orders) {
            var status=order.Disposition switch {
                WbReceiveDisposition.Cancelled=>"Khách đã hủy",
                WbReceiveDisposition.AlreadyMember when order.Verified=>"Đã có trong shipment",
                WbReceiveDisposition.EligibleNew when order.Verified=>"Đã thêm",
                WbReceiveDisposition.EligibleNew=>"Cần tiếp tục",
                _=>"Bị từ chối"};
            var index=grid.Rows.Add(order.OrderId,status,order.Message);grid.Rows[index].Height=38;
            grid.Rows[index].DefaultCellStyle.ForeColor=order.Verified?C.Green:C.Orange;
        }
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60,Padding=new(12),FlowDirection=FlowDirection.RightToLeft};
        var close=new Button{Text="Đóng",Width=110,Height=34,DialogResult=DialogResult.OK};footer.Controls.Add(close);
        footer.Controls.Add(new Label{Text="Mở shipment trong Đang đóng gói để tích KIZ và xuất nhãn.",AutoSize=true,Padding=new(0,8,14,0),ForeColor=C.Muted});
        Controls.Add(grid);Controls.Add(header);Controls.Add(footer);AcceptButton=close;CancelButton=close;
    }
}
