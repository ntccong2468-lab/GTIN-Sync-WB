using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Diagnostics;
namespace MarketplaceHub.UI;

public sealed class FbsLabelJobDialog:Form
{
    private readonly AppServices app;
    private readonly LabelTarget target;
    private readonly Label stage=new(){Name="fbsJobStage",Dock=DockStyle.Top,Height=32};
    private readonly Label detail=new(){Dock=DockStyle.Fill,AutoEllipsis=true};
    private readonly DataGridView units=new(){Name="fbsJobUnits",Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None};
    private readonly FlowLayoutPanel actions=new(){Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new(8)};
    private readonly CheckBox physical=new(){Name="kizPhysicalConfirmed",Text="Đã dán đúng mã đã scan vào các sản phẩm chọn",AutoSize=true};
    private readonly TextBox scan=new(){Name="fbsJobScan",Width=280,PlaceholderText="Scan KIZ cho đúng dòng đang chọn"};
    private readonly Dictionary<FbsUnitKey,string> scanned=new();
    private readonly Button resume,pause,reprint,matrix,confirm,revision;
    private LabelJobResult? current;
    private CancellationToken workerLifetime;
    private bool subscribed=true,busy,darkAppearance=true;
    public FbsLabelJobDialog(AppServices app,LabelTarget target)
    {
        this.app=app;this.target=target;Text=target.Marketplace+" · xuất nhãn FBS · "+target.TargetId;Size=new(1000,700);MinimumSize=new(900,560);StartPosition=FormStartPosition.CenterParent;Font=new("Segoe UI",10);
        var shell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new(12)};shell.ColumnStyles.Add(new(SizeType.Percent,100));shell.RowStyles.Add(new(SizeType.Absolute,82));shell.RowStyles.Add(new(SizeType.Percent,100));shell.RowStyles.Add(new(SizeType.Absolute,84));shell.RowStyles.Add(new(SizeType.Absolute,84));shell.RowStyles.Add(new(SizeType.Absolute,80));Controls.Add(shell);
        var header=new Panel{Dock=DockStyle.Fill};header.Controls.Add(detail);header.Controls.Add(stage);shell.Controls.Add(header,0,0);shell.Controls.Add(units,0,1);
        units.Columns.Add(new DataGridViewCheckBoxColumn{Name="selected",HeaderText="Chọn",Width=55});
        foreach(var column in new[]{("order","Đơn / item / unit",245),("gtin","GTIN đúng biến thể",170),("code","Mã đang giữ",145),("legal","Điều kiện / trạng thái",240),("physical","Tem vật lý",145),("error","Cần xử lý",220)})units.Columns.Add(new DataGridViewTextBoxColumn{Name=column.Item1,HeaderText=column.Item2,Width=column.Item3,ReadOnly=true});
        units.DefaultCellStyle.WrapMode=DataGridViewTriState.True;units.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
        var scanRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,Padding=new(8)};scanRow.Controls.Add(scan);var match=new Button{Text="Đối chiếu scan",AutoSize=true};scanRow.Controls.Add(match);scanRow.Controls.Add(physical);confirm=new Button{Name="fbsJobConfirmPhysical",Text="Xác nhận các tem đã chọn",AutoSize=true};scanRow.Controls.Add(confirm);shell.Controls.Add(scanRow,0,2);
        var guidance=new Label{Dock=DockStyle.Fill,Text="Mã phát hành cần trạng thái pháp lý hợp lệ và dán vào đúng sản phẩm. Hoàn tất hồ sơ tại Честный ЗНАК nếu đang chờ pháp lý, rồi kiểm tra lại. PDF Data Matrix là tem chuẩn bị; nhãn vận chuyển giữ file chính thức của sàn."};shell.Controls.Add(guidance,0,3);shell.Controls.Add(actions,0,4);
        Button Action(string name,string text){var b=new Button{Name=name,Text=text,AutoSize=true,Height=34,Margin=new(4)};actions.Controls.Add(b);return b;}
        resume=Action("fbsJobResume","Kiểm tra / tiếp tục");pause=Action("fbsJobPause","Tạm dừng");reprint=Action("fbsJobReprint","In lại nhãn sàn");matrix=Action("kizPrintDataMatrix","Xuất Data Matrix");var profile=Action("kizWorkflowProfile","Profile KIZ");revision=Action("fbsJobRevision","Revision mới");
        var light=new CheckBox{Text="Giao diện sáng",AutoSize=true,Margin=new(8,10,0,0)};actions.Controls.Add(light);light.CheckedChanged+=(_,_)=>SetAppearance(!light.Checked);
        resume.Click+=async(_,_)=>await RunAsync(workerLifetime);pause.Click+=(_,_)=>{if(current is not null)app.PauseFbsLabelJob(current.JobId);};
        reprint.Click+=async(_,_)=>await Execute(async()=>{var result=await app.LabelJobs.ReprintAsync(current!.JobId,current.Revision,workerLifetime);Apply(result);Open(result.Artifacts.Where(x=>x.OfficialMarketplaceLabel));});
        matrix.Click+=async(_,_)=>await Execute(async()=>Open(await app.ExportJobDataMatrixAsync(current!.JobId,workerLifetime)));
        confirm.Click+=async(_,_)=>await Execute(async()=>{
            units.EndEdit();var selected=units.Rows.Cast<DataGridViewRow>().Where(x=>x.Cells["selected"].Value is true).Select(x=>(FbsUnitKey)x.Tag!).ToArray();
            if(!physical.Checked||selected.Length==0||selected.Any(x=>!scanned.ContainsKey(x)))throw new InvalidOperationException("Chọn đúng từng sản phẩm, scan mã đang giữ và xác nhận đã dán tem.");
            Apply(await app.ConfirmJobPhysicalMarksAsync(current!.JobId,selected.Select(x=>new PhysicalMarkEvidence(x,scanned[x],DateTimeOffset.UtcNow)).ToArray(),workerLifetime));physical.Checked=false;scanned.Clear();
        });
        match.Click+=async(_,_)=>await MatchScanAsync();scan.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await MatchScanAsync();}};
        profile.Click+=(_,_)=>{var store=app.Db.Stores().SingleOrDefault(x=>x.Id==target.StoreId);if(store is null)return;using var dialog=new KizWorkflowProfileDialog(app,store);dialog.ShowDialog(this);};
        revision.Click+=async(_,_)=>{if(current is null||MessageBox.Show(this,"Tạo revision cho nhu cầu hiện tại sau khi đối soát? Mã đã giữ cho sản phẩm cũ được bảo toàn.","Revision mới",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;await Execute(async()=>Apply(await app.LabelJobs.CreateRevisionAsync(current.JobId,true,workerLifetime)));};
        app.FbsLabelJobChanged+=Changed;FormClosed+=(_,_)=>Unsubscribe();SetAppearance(true);
        var saved=app.CurrentFbsLabelResult(target);if(saved is not null)Apply(saved);else Apply(new("",0,FbsLabelJobStage.Queued,0,0,Array.Empty<UnitWorkflowResult>(),Array.Empty<LabelArtifact>(),null));
    }
    public void SetAppearance(bool dark)
    {
        darkAppearance=dark;
        var surface=dark?C.Main:SystemColors.Window;var text=dark?C.Text:SystemColors.WindowText;BackColor=surface;ForeColor=text;
        void Paint(Control c){c.BackColor=surface;c.ForeColor=text;foreach(Control child in c.Controls)Paint(child);}foreach(Control c in Controls)Paint(c);
        units.BackgroundColor=surface;units.EnableHeadersVisualStyles=false;units.DefaultCellStyle.BackColor=surface;units.DefaultCellStyle.ForeColor=text;units.DefaultCellStyle.SelectionBackColor=dark?C.Border:SystemColors.Highlight;units.DefaultCellStyle.SelectionForeColor=dark?C.Text:SystemColors.HighlightText;units.ColumnHeadersDefaultCellStyle.BackColor=dark?C.Card:SystemColors.Control;units.ColumnHeadersDefaultCellStyle.ForeColor=text;
        if(current is not null)SetStageColor(current);
    }
    public void Apply(LabelJobResult result)
    {
        if(IsDisposed||!subscribed)return;current=result;stage.Text=$"{StageText(result.Stage)} · {result.VerifiedUnits}/{result.TotalUnits} sản phẩm có nhãn đã xác minh";SetStageColor(result);
        var job=app.GetFbsLabelJob(result.JobId);detail.Text=$"{target.Marketplace} · {target.TargetId} · revision {result.Revision} · {(job is null?"":job.UpdatedAt.ToLocalTime().ToString("dd/MM HH:mm:ss"))}\n"+(result.ErrorCode??"Có thể chuẩn bị KIZ còn thiếu, đóng gói và lấy nhãn khi đủ điều kiện.");
        var selected=units.Rows.Cast<DataGridViewRow>().Where(x=>x.Cells["selected"].Value is true).Select(x=>(FbsUnitKey)x.Tag!).ToHashSet();units.Rows.Clear();
        foreach(var item in result.Units){var demand=job?.Snapshot.Units.SingleOrDefault(x=>x.Unit==item.Unit);var hash=item.CodeHash;var pasted=hash is not null&&app.Db.PhysicalMarkMatches(item.Unit,hash);var index=units.Rows.Add(selected.Contains(item.Unit),$"{item.Unit.OrderId} / {item.Unit.ItemId} / {item.Unit.UnitIndex+1}",demand?.Gtin??"",hash is null?"Chưa có mã":"…"+hash[^Math.Min(8,hash.Length)..],item.Stage,pasted?"Đã xác nhận dán":"Chưa xác nhận",item.ErrorCode??"");units.Rows[index].Tag=item.Unit;}
        reprint.Enabled=!busy&&result.Artifacts.Any(x=>x.OfficialMarketplaceLabel);matrix.Enabled=!busy&&job is not null;confirm.Enabled=!busy&&job is not null;revision.Enabled=!busy&&result.Stage==FbsLabelJobStage.SnapshotChanged;
    }
    public async Task RunAsync(CancellationToken lifetime=default){workerLifetime=lifetime;await Execute(async()=>Apply(await app.ExportFbsLabelsAsync(target,null,lifetime)));}
    private async Task Execute(Func<Task> action)
    {
        if(busy)return;busy=true;resume.Enabled=false;try{await action();}catch(OperationCanceledException){if(subscribed&&!IsDisposed)detail.Text="Đã tạm dừng; mã và order ID được giữ.";}catch(Exception ex){if(subscribed&&!IsDisposed)detail.Text=ex is InvalidOperationException?ex.Message:"Chưa xác minh được tác vụ; kiểm tra lại.";}finally{busy=false;if(subscribed&&!IsDisposed){resume.Enabled=true;if(current is not null)Apply(current);}}
    }
    private async Task MatchScanAsync()
    {
        try{
            if(current is null||units.CurrentRow?.Tag is not FbsUnitKey key)throw new InvalidOperationException("Chọn đúng một sản phẩm để scan.");var job=app.GetFbsLabelJob(current.JobId)??throw new InvalidOperationException("Tác vụ chưa được lập.");var raw=scan.Text;var matched=await app.ScanJobKizAsync(job.Id,key,raw,workerLifetime);scanned[key]=matched.CodeHash!;if(!IsDisposed&&subscribed){Apply(app.ProjectLabelJob(job.Id));foreach(DataGridViewRow row in units.Rows)if((FbsUnitKey)row.Tag! == key)row.Cells["selected"].Value=true;detail.Text="Scan đã khớp. Dán đúng sản phẩm rồi xác nhận tem đã chọn.";scan.Clear();}
        }catch(InvalidOperationException ex){detail.Text=ex.Message;scan.Clear();}
    }
    private void Changed(LabelJobResult result)
    {
        if(!subscribed||IsDisposed||!IsHandleCreated)return;var job=app.GetFbsLabelJob(result.JobId);if(job?.Snapshot.Target!=target)return;
        try{BeginInvoke(()=>{if(subscribed&&!IsDisposed)Apply(result);});}catch(InvalidOperationException){}
    }
    public void Unsubscribe(){if(!subscribed)return;subscribed=false;app.FbsLabelJobChanged-=Changed;}
    private void SetStageColor(LabelJobResult result)=>stage.ForeColor=result.Stage==FbsLabelJobStage.LabelsReady&&result.TotalUnits>0&&result.VerifiedUnits==result.TotalUnits?(darkAppearance?C.Green:Color.FromArgb(0,110,60)):(darkAppearance?C.Orange:Color.FromArgb(130,65,0));
    protected override void Dispose(bool disposing){if(disposing)Unsubscribe();base.Dispose(disposing);}
    private static void Open(IEnumerable<LabelArtifact> artifacts){foreach(var item in artifacts)if(File.Exists(item.FilePath)&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.FilePath)))==item.Sha256)Process.Start(new ProcessStartInfo(item.FilePath){UseShellExecute=true});}
    private static string StageText(FbsLabelJobStage value)=>value switch{FbsLabelJobStage.Purchasing=>"Đang yêu cầu cấp KIZ",FbsLabelJobStage.Verifying=>"Đang xác minh nhãn",FbsLabelJobStage.AwaitingLegalState=>"Chờ trạng thái pháp lý",FbsLabelJobStage.AwaitingPhysicalMark=>"Chờ dán tem vật lý",FbsLabelJobStage.Partial=>"Đã có một phần nhãn",FbsLabelJobStage.LabelsReady=>"Nhãn sẵn sàng",FbsLabelJobStage.Paused=>"Đã tạm dừng",FbsLabelJobStage.NeedsReconciliation=>"Cần đối soát",FbsLabelJobStage.SnapshotChanged=>"Nhu cầu đã thay đổi",_=>value.ToString()};
}
