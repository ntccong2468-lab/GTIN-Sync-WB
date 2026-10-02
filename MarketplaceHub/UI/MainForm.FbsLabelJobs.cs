using MarketplaceHub.Core;
namespace MarketplaceHub.UI;
public sealed partial class MainForm
{
    private Task ShowFbsLabelJobAsync(LabelTarget target,CancellationToken pageToken)
    {
        if(pageToken.IsCancellationRequested||IsDisposed)return Task.CompletedTask;
        var dialog=new FbsLabelJobDialog(app,target);dialog.Show(this);
        var registration=pageToken.Register(()=>{dialog.Unsubscribe();if(dialog.IsHandleCreated&&!dialog.IsDisposed)try{dialog.BeginInvoke(()=>{if(!dialog.IsDisposed)dialog.Close();});}catch(InvalidOperationException){} });
        dialog.FormClosed+=(_,_)=>registration.Dispose();_=dialog.RunAsync(lifetimeCts.Token);return Task.CompletedTask;
    }
}
