using MarketplaceHub.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;
namespace MarketplaceHub.Services.Fbs;
public sealed class LabelArtifactStore
{
    private readonly string root;
    public LabelArtifactStore(string? root=null)=>this.root=root??WbPrintBundleService.HistoryDirectory;
    public async Task<LabelArtifact> PersistAsync(FbsWorkflowContext context,VerifiedLabel label,CancellationToken ct)
    {
        if(!label.Label.Success||string.IsNullOrWhiteSpace(label.Label.FilePath)||label.SourceOperation==""||label.SourceEvidenceHash==""||label.Units.Count==0||label.Units.Distinct().Count()!=label.Units.Count||label.Units.Any(x=>x.OrderId!=label.RemoteOrderId||!context.Snapshot.Units.Any(d=>d.Unit==x)))throw new InvalidOperationException("official_label_evidence_required");
        var bytes=await File.ReadAllBytesAsync(label.Label.FilePath,ct);var ext=FileType(bytes);if(ext is null)throw new InvalidOperationException("invalid_label_file");
        var id=Guid.NewGuid().ToString("N");var folder=Path.Combine(root,SafeId(context.JobId),context.Revision.ToString());Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,id+ext);var temp=path+".tmp";
        try{await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        var artifact=new LabelArtifact(id,context.JobId,context.Revision,"OfficialMarketplaceLabel",path,Convert.ToHexString(SHA256.HashData(bytes)),label.Units,true);
        var manifest=path+".json";await File.WriteAllTextAsync(manifest+".tmp",JsonSerializer.Serialize(new{artifact,label.SourceOperation,label.SourceEvidenceHash,label.RemoteOrderId}),ct);File.Move(manifest+".tmp",manifest,true);return artifact;
    }
    public async Task<bool> ValidateAsync(LabelArtifact artifact,CancellationToken ct)
    {
        try{var bytes=await File.ReadAllBytesAsync(artifact.FilePath,ct);return artifact.OfficialMarketplaceLabel&&artifact.Kind=="OfficialMarketplaceLabel"&&FileType(bytes)is not null&&Convert.ToHexString(SHA256.HashData(bytes))==artifact.Sha256;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    private static string SafeId(string id)=>id.Length==32&&id.All(Uri.IsHexDigit)?id:throw new InvalidOperationException("invalid_job_id");
    private static string? FileType(byte[] bytes)
    {
        if(bytes.Length>20&&bytes.AsSpan(0,5).SequenceEqual("%PDF-"u8)&&Encoding.ASCII.GetString(bytes,Math.Max(0,bytes.Length-2048),Math.Min(bytes.Length,2048)).Contains("%%EOF",StringComparison.Ordinal))return ".pdf";
        if(bytes.Length>8&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})){using var image=SKImage.FromEncodedData(bytes);if(image is not null&&image.Width>0&&image.Height>0)return ".png";}return null;
    }
}
