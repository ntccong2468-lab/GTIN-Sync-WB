using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GTINSyncWB;
public sealed class Storage
{
    public string Folder { get; }
    public Storage(string? folder=null) => Folder=folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GTIN Sync WB");
    public Settings Load()
    {
        var path=Path.Combine(Folder,"settings.json");
        return File.Exists(path)?JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))??new():new();
    }
    public void Save(Settings s)
    {
        Directory.CreateDirectory(Folder);
        var path=Path.Combine(Folder,"settings.json");var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,path,true);
    }
    public void Append(HistoryEntry entry)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(Path.Combine(Folder,"history.jsonl"),JsonSerializer.Serialize(entry)+Environment.NewLine);
    }
    public List<HistoryEntry> History()
    {
        var path=Path.Combine(Folder,"history.jsonl");
        return File.Exists(path)?File.ReadLines(path).Select(x=>{try{return JsonSerializer.Deserialize<HistoryEntry>(x);}catch{return null;}}).OfType<HistoryEntry>().ToList():[];
    }
    public void SaveJobs(IEnumerable<WriteJob> jobs)=>WriteAtomic("jobs.json",JsonSerializer.Serialize(jobs));
    public List<WriteJob> LoadJobs()
    {
        var path=Path.Combine(Folder,"jobs.json");
        return File.Exists(path)?JsonSerializer.Deserialize<List<WriteJob>>(File.ReadAllText(path))??[]:[];
    }
    public void SaveSnapshot(ReadSnapshot snapshot)=>WriteAtomic("snapshot.json",JsonSerializer.Serialize(snapshot));
    public ReadSnapshot? LoadSnapshot()
    {
        var path=Path.Combine(Folder,"snapshot.json");
        return File.Exists(path)?JsonSerializer.Deserialize<ReadSnapshot>(File.ReadAllText(path)):null;
    }
    private void WriteAtomic(string file,string content)
    {
        Directory.CreateDirectory(Folder);
        var path=Path.Combine(Folder,file);var temp=path+".tmp";
        File.WriteAllText(temp,content);
        File.Move(temp,path,true);
    }
}
public sealed class ReadSnapshot
{
    public DateTimeOffset CompletedAt { get; set; }
    public List<CatalogItem> Goods { get; set; } = [];
    public List<Listing> Cards { get; set; } = [];
}
public static class Secrets
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] bytes,bool encrypt)
    {
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            Blob output;
            bool ok=encrypt?CryptProtectData(ref input,"GTIN Sync WB",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out output);
            if(!ok)throw new InvalidOperationException("Không thể đọc khóa đã bảo vệ bằng Windows DPAPI");
            try { var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result; }
            finally {LocalFree(output.Data);}
        }
        finally {Marshal.FreeHGlobal(input.Data);Array.Clear(bytes);}
    }
    public static string Protect(string value)=>Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value),true));
    public static string Reveal(string protectedValue)=>Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedValue),false));
}
