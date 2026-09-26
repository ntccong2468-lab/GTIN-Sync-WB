namespace GTINSyncWB;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var instance=new Mutex(true,@"Local\GTINSyncWB",out var firstInstance);
        if(!firstInstance)
        {
            if(args.Length==0)MessageBox.Show("GTIN Sync WB đang mở trong phiên Windows này.","GTIN Sync WB",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        if(args.Length==2 && args[0]=="--ui-snapshot")
        {
            using var window=new Dashboard();
            window.Shown+=(_,_)=>{window.CaptureForCi(args[1]);window.BeginInvoke(new Action(window.Close));};
            Application.Run(window);return;
        }
        if(args.Length==2 && args[0]=="--smoke")
        {
            var value="temporary-key-for-smoke";
            if(Secrets.Reveal(Secrets.Protect(value))!=value)Environment.Exit(2);
            var smokeFolder=Path.Combine(Path.GetTempPath(),"gtin-smoke-"+Guid.NewGuid().ToString("N"));
            try
            {
                var storage=new Storage(smokeFolder);
                storage.Save(new Settings{Organization="Smoke test",ProtectedCatalogKey=Secrets.Protect(value),Shops=[new Shop{Name="Smoke shop",ProtectedToken=Secrets.Protect("separate-wb-smoke-key")}]});
                var saved=storage.Load();
                if(saved.Organization!="Smoke test" || Secrets.Reveal(saved.ProtectedCatalogKey)!=value || saved.Shops.Count!=1 || Secrets.Reveal(saved.Shops[0].ProtectedToken)!="separate-wb-smoke-key")Environment.Exit(3);
            }
            finally {if(Directory.Exists(smokeFolder))Directory.Delete(smokeFolder,true);}
            using var window=new Dashboard();
            window.Shown+=(_,_)=>{File.WriteAllText(args[1],window.Text);window.BeginInvoke(new Action(window.Close));};
            Application.Run(window);return;
        }
        Application.Run(new Dashboard());
    }
}
