using MarketplaceHub.Core;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed class OzonDiagnosticsDialog : Form
{
    private readonly MarketplaceGateway api;
    private readonly TextBox clientId = new() { Name="ozonDiagnosticClientId" };
    private readonly TextBox apiKey = new() { Name="ozonDiagnosticApiKey", UseSystemPasswordChar=true };
    private readonly TextBox posting = new() { Name="ozonDiagnosticPosting" };
    private readonly Button run = new() { Name="runOzonReadOnlyDiagnostics", Text="Kiểm tra chỉ đọc", Width=190, Height=38 };
    private readonly Button cancel = new() { Text="Dừng", Width=100, Height=38, Enabled=false };
    private readonly Button copy = new() { Name="copyOzonRedactedReport", Text="Sao chép báo cáo đã ẩn bí mật", Width=260, Height=38, Enabled=false };
    private readonly DataGridView grid = new() { Name="ozonDiagnosticResults", ReadOnly=true, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
    private StoreProfile store;
    private CancellationTokenSource? operation;
    private string safeReport="";

    public OzonDiagnosticsDialog(MarketplaceGateway api,StoreProfile store)
    {
        this.api=api;this.store=store;
        Text="Ozon · kiểm tra API thật (chỉ đọc)";ClientSize=new Size(1040,680);MinimumSize=new Size(850,560);StartPosition=FormStartPosition.CenterParent;BackColor=C.Main;ForeColor=C.Text;
        var warning=new Label{Name="ozonReadOnlyWarning",Left=20,Top=15,Width=990,Height=42,ForeColor=Color.FromArgb(248,187,74),Text="Chế độ an toàn: chỉ đọc seller, quyền, kho, hàng đợi FBS và posting. Không gán KIZ, không đóng đơn, không tạo job nhãn và không tự lưu API Key."};Controls.Add(warning);
        AddField("Client ID",clientId,20,70,250);AddField("API Key",apiKey,290,70,360);AddField("Posting Number (không bắt buộc)",posting,670,70,340);
        clientId.Text=store.ClientId;apiKey.Text=store.ApiKey;
        run.Left=20;run.Top=138;cancel.Left=225;cancel.Top=138;copy.Left=340;copy.Top=138;Controls.AddRange(new Control[]{run,cancel,copy});
        grid.Left=20;grid.Top=195;grid.Width=1000;grid.Height=455;grid.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
        grid.BackgroundColor=C.Card;grid.ForeColor=C.Text;grid.DefaultCellStyle.BackColor=C.Card;grid.DefaultCellStyle.ForeColor=C.Text;grid.ColumnHeadersDefaultCellStyle.BackColor=C.Main;grid.ColumnHeadersDefaultCellStyle.ForeColor=C.Text;
        grid.Columns.Add("stage","Bước");grid.Columns.Add("endpoint","Endpoint");grid.Columns.Add("result","Kết quả");grid.Columns.Add("http","HTTP");grid.Columns.Add("time","Thời gian");grid.Columns.Add("message","Phân tích / hướng xử lý");grid.Columns[5].FillWeight=240;Controls.Add(grid);
        run.Click+=RunAsync;cancel.Click+=(_,_)=>operation?.Cancel();copy.Click+=(_,_)=>{if(safeReport.Length>0)Clipboard.SetText(safeReport);};
        FormClosed+=(_,_)=>{operation?.Cancel();operation?.Dispose();};
    }

    private void AddField(string label,TextBox box,int left,int top,int width)
    {
        Controls.Add(new Label{Left=left,Top=top,AutoSize=true,Text=label,ForeColor=C.Muted});box.SetBounds(left,top+24,width,30);box.BackColor=C.Input;box.ForeColor=C.Text;box.BorderStyle=BorderStyle.FixedSingle;Controls.Add(box);
    }

    private async void RunAsync(object? sender,EventArgs e)
    {
        operation?.Dispose();operation=new CancellationTokenSource();run.Enabled=false;cancel.Enabled=true;copy.Enabled=false;safeReport="";grid.Rows.Clear();
        store=store with{ClientId=clientId.Text.Trim(),ApiKey=apiKey.Text};
        try
        {
            var report=await api.DiagnoseOzonAsync(store,posting.Text.Trim(),operation.Token);
            safeReport=report.SafeText;
            foreach(var step in report.Steps)grid.Rows.Add(step.Stage,step.Endpoint,step.Success?"OK":"LỖI",step.HttpStatus==0?"—":step.HttpStatus,step.ElapsedMs+" ms",step.Message+" "+step.NextAction);
            copy.Enabled=safeReport.Length>0;
        }
        catch(OperationCanceledException){grid.Rows.Add("Đã dừng","—","DỪNG","—","—","Không có mutation nào được thực hiện.");}
        finally{if(!IsDisposed){run.Enabled=true;cancel.Enabled=false;}}
    }
}
