namespace GTINSyncWB;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length==2 && args[0]=="--smoke")
        {
            var value="temporary-key-for-smoke";
            if(Secrets.Reveal(Secrets.Protect(value))!=value)Environment.Exit(2);
            var smokeFolder=Path.Combine(Path.GetTempPath(),"gtin-smoke-"+Guid.NewGuid().ToString("N"));
            try {var storage=new Storage(smokeFolder);storage.Save(new Settings{Organization="Smoke test",ProtectedCatalogKey=Secrets.Protect(value)});var saved=storage.Load();if(saved.Organization!="Smoke test" || Secrets.Reveal(saved.ProtectedCatalogKey)!=value)Environment.Exit(3);}
            finally {if(Directory.Exists(smokeFolder))Directory.Delete(smokeFolder,true);}
            using var window=new Dashboard();
            window.Shown+=(_,_)=>{File.WriteAllText(args[1],window.Text);window.BeginInvoke(new Action(window.Close));};
            Application.Run(window);return;
        }
        Application.Run(new Dashboard());
    }
}
