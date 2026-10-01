using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Services;

public static class DeviceFingerprint
{
    private static readonly Lazy<string> Cached=new(Create);
    public static string Get()=>Cached.Value;

    private static string Create()
    {
        string raw;
        try
        {
            var guid=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid")?.ToString();
            raw=!string.IsNullOrWhiteSpace(guid)?"machine-guid:"+guid.Trim().ToLowerInvariant():"stored-device-id:"+StoredId();
        }
        catch{raw="stored-device-id:"+StoredId();}
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string StoredId()
    {
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MarketplaceHub");Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"device-id");
        if(File.Exists(path)){var current=File.ReadAllText(path).Trim();if(Guid.TryParse(current,out _))return current;}
        var created=Guid.NewGuid().ToString();var staging=path+".tmp";File.WriteAllText(staging,created);File.Move(staging,path,true);return created;
    }
}
