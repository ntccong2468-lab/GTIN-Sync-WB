using MarketplaceHub.Services;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Text.Json;

namespace MarketplaceHub.UI;

public sealed class WbPrintOptionsDialog : Form
{
    private readonly CheckBox product = new() {Text="In nhãn sản phẩm (barcode, article, màu, size)",Checked=true,AutoSize=true};
    private readonly CheckBox kiz = new() {Text="Kèm KIZ đã gắn vào đơn",AutoSize=true};
    private readonly ComboBox order = new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=400};
    private readonly NumericUpDown copies = new() {Minimum=1,Maximum=20,Value=1,Width=100};
    public WbPrintOptions Options => new(product.Checked,kiz.Checked,order.SelectedIndex==1,(int)copies.Value);
    public WbPrintOptionsDialog(bool includeKiz)
    {
        Text="Chuẩn bị bộ nhãn WB"; Width=520; Height=320; StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false;
        BackColor=C.Main; ForeColor=C.Text; Font=new Font("Segoe UI",10);
        kiz.Checked=includeKiz;
        var body=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(18),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        order.Items.AddRange(new object[] {"Nhãn sản phẩm/KIZ → Sticker WB","Sticker WB → Nhãn sản phẩm/KIZ"}); order.SelectedIndex=0;
        body.Controls.Add(product);body.Controls.Add(kiz);body.Controls.Add(order);
        body.Controls.Add(new Label {Text="Số bản nhãn sản phẩm/KIZ cho mỗi mã",AutoSize=true});body.Controls.Add(copies);
        body.Controls.Add(new Label {Text="Sticker WB: 1 bản/đơn · PDF 58×40 mm · Phiếu nhặt A4",AutoSize=true});
        var go=new Button {Text="Tạo PDF / xem trước",Width=220,Height=35,DialogResult=DialogResult.OK};
        body.Controls.Add(go);Controls.Add(body);AcceptButton=go;
    }
}

public sealed class WbPrintProgressDialog : Form
{
    private readonly Label message=new() {Dock=DockStyle.Top,Height=65,Padding=new Padding(12),Text="Đang kiểm tra trạng thái WB…"};
    public WbPrintProgressDialog(Action cancel)
    {
        Text="Chuẩn bị nhãn WB";Width=530;Height=185;StartPosition=FormStartPosition.CenterParent;
        BackColor=C.Main;ForeColor=C.Text;Font=new Font("Segoe UI",10);MaximizeBox=false;MinimizeBox=false;
        var bar=new ProgressBar {Dock=DockStyle.Top,Style=ProgressBarStyle.Marquee,Height=15};
        var stop=new Button {Text="Dừng chuẩn bị",Dock=DockStyle.Bottom,Height=32};stop.Click+=(_,_)=>cancel();
        Controls.Add(bar);Controls.Add(message);Controls.Add(stop);FormClosing+=(_,_)=>cancel();
    }
    public void Report(string text) {if (!IsDisposed) message.Text=text;}
}

public sealed class WbPrintPreviewDialog : Form
{
    private readonly PictureBox preview=new() {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White};
    private readonly Label status=new() {AutoSize=true};
    private readonly ComboBox printers=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=260};
    private readonly Button print=new() {Text="In toàn bộ nhãn",Width=150,Height=35};
    private readonly WbPrintBundle bundle;
    private readonly Action<string> audit;
    private readonly CancellationTokenSource printing=new();
    private int index;
    private bool busy;
    public WbPrintPreviewDialog(WbPrintBundle prepared,Action<string> record,string title="WB · Bộ nhãn đã chuẩn bị")
    {
        bundle=prepared;audit=record;
        Text=title;Width=950;Height=750;MinimumSize=new Size(820,650);
        StartPosition=FormStartPosition.CenterParent;BackColor=C.Main;ForeColor=C.Text;Font=new Font("Segoe UI",10);
        var footer=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=150,Padding=new Padding(12),WrapContents=true};
        var previous=new Button {Text="←",Width=45,Height=35};var next=new Button {Text="→",Width=45,Height=35};
        previous.Click+=(_,_)=>{index=Math.Max(0,index-1);ShowPage();};next.Click+=(_,_)=>{index=Math.Min(bundle.Pages.Count-1,index+1);ShowPage();};
        Button OpenButton(string text,string path) {var button=new Button {Text=text,Width=170,Height=35};button.Click+=(_,_)=>Open(path);return button;}
        footer.Controls.Add(previous);footer.Controls.Add(next);footer.Controls.Add(OpenButton("Mở PDF nhãn",bundle.LabelsPdf));
        footer.Controls.Add(OpenButton("Phiếu nhặt A4",bundle.DetailsPdf));footer.Controls.Add(OpenButton("Thư mục bộ nhãn",bundle.Directory));
        footer.SetFlowBreak(footer.Controls[footer.Controls.Count-1],true);
        foreach (string name in PrinterSettings.InstalledPrinters) printers.Items.Add(name);
        if(printers.Items.Count>0)
        {
            var defaultName=new PrinterSettings().PrinterName;
            printers.SelectedIndex=printers.Items.IndexOf(defaultName);if(printers.SelectedIndex<0)printers.SelectedIndex=0;
        }
        print.Enabled=printers.Items.Count>0;
        footer.Controls.Add(printers);footer.Controls.Add(print);footer.SetFlowBreak(print,true);footer.Controls.Add(status);
        Controls.Add(preview);Controls.Add(footer);
        print.Click+=async(_,_)=>await SubmitAsync();
        FormClosing+=(_,e)=>{if(busy){printing.Cancel();e.Cancel=true;status.Text="Đang dừng gửi nhãn…";}};
        FormClosed+=(_,_)=>{preview.Image?.Dispose();printing.Dispose();};
        ShowPage();
    }
    private void ShowPage()
    {
        var old=preview.Image;preview.Image=Image.FromFile(bundle.Pages[index].Path);old?.Dispose();
        status.Text=$"Trang {index+1}/{bundle.Pages.Count} · Đơn {bundle.Pages[index].OrderId} · {(bundle.Pages[index].Kind=="sticker" ? "Sticker WB" : "Sản phẩm/KIZ")} · Chưa xác nhận in trên giấy";
    }
    private void Open(string path)
    {
        try {Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Mở file",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    private async Task SubmitAsync()
    {
        if(busy || printers.SelectedItem is not string name)return;
        busy=true;print.Enabled=false;printers.Enabled=false;status.Text="Đang gửi bộ nhãn tới máy in…";
        try
        {
            var submitted=await Task.Run(()=>PrintPages(bundle,name,printing.Token));
            status.Text=RecordSubmission(bundle,name,submitted,audit,File.WriteAllText);
            print.Text="Đã gửi bộ nhãn";
        }
        catch(Exception ex)
        {
            try {audit("Gửi in lỗi/có thể đã gửi một phần: "+ex.Message+". PDF: "+bundle.LabelsPdf);} catch { }
            status.Text="Chưa gửi xong; kiểm tra hàng đợi máy in trước khi in lại. "+ex.Message;
        }
        // Reprinting requires opening a new job, after checking the printer queue.
        finally {busy=false;print.Enabled=false;printers.Enabled=true;}
    }
    public static string RecordSubmission(WbPrintBundle bundle,string printer,int submitted,Action<string> audit,Action<string,string> write)
    {
        var message=$"Đã gửi {submitted}/{bundle.Pages.Count} trang tới {printer}. Hãy kiểm tra nhãn thực tế.";
        var errors=new List<string>();
        try {write(Path.Combine(bundle.Directory,"print-status.json"),JsonSerializer.Serialize(new
            {State="Submitted",Printer=printer,Pages=submitted,AtUtc=DateTimeOffset.UtcNow}));}
        catch(Exception ex){errors.Add(ex.Message);}
        try {audit(message+" PDF: "+bundle.LabelsPdf);}catch(Exception ex){errors.Add(ex.Message);}
        return errors.Count==0 ? message : message+" Lưu lịch sử bị lỗi: "+string.Join("; ",errors);
    }
    public static PrintDocument CreatePrintDocument(string printer)
    {
        var document=new PrintDocument {PrintController=new StandardPrintController(),DocumentName="Marketplace Hub · Bộ nhãn"};
        document.PrinterSettings.PrinterName=printer;
        document.PrinterSettings.Copies=1;
        document.DefaultPageSettings.PaperSize=new PaperSize("58x40mm",228,157);
        document.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        document.DefaultPageSettings.Landscape=false;
        return document;
    }
    public static int PrintPages(WbPrintBundle bundle,string printer,CancellationToken ct)
    {
        using var document=CreatePrintDocument(printer);
        if(!document.PrinterSettings.IsValid)throw new InvalidOperationException("Máy in được chọn không hợp lệ.");
        var printed=0;
        document.PrintPage+=(_,e)=>
        {
            if(ct.IsCancellationRequested){e.Cancel=true;return;}
            if(Math.Abs(e.PageBounds.Width-228)>2 || Math.Abs(e.PageBounds.Height-157)>2)
                throw new InvalidOperationException("Driver máy in chưa nhận khổ 58×40 mm. Hãy đặt đúng khổ giấy trước khi in.");
            using var image=Image.FromFile(bundle.Pages[printed].Path);
            e.Graphics!.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor;e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;
            e.Graphics.DrawImage(image,e.PageBounds);printed++;e.HasMorePages=printed<bundle.Pages.Count;
        };
        ct.ThrowIfCancellationRequested();document.Print();ct.ThrowIfCancellationRequested();
        if(printed!=bundle.Pages.Count)throw new InvalidOperationException($"Chỉ gửi được {printed}/{bundle.Pages.Count} trang.");
        return printed;
    }
}
