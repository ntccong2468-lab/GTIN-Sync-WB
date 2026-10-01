using SkiaSharp;

namespace MarketplaceHub.Services;

public static class MarketplaceKizPdfService
{
    public static string Write(string marketplace,IReadOnlyList<WbPrintOrder> orders,string folder,CancellationToken ct=default)
    {
        var codes=orders.SelectMany(x=>x.KizCodes).ToArray();if(codes.Length==0 || codes.Distinct(StringComparer.Ordinal).Count()!=codes.Length || orders.Any(x=>x.KizCodes.Count!=x.Quantity))throw new InvalidOperationException("Bộ KIZ phải đủ số lượng và không trùng mã.");
        var target=Path.Combine(folder,marketplace+"-KIZ-58x40.pdf");var temporary=target+".tmp";
        try {
            using(var pdf=SKDocument.CreatePdf(temporary)) {
                foreach(var order in orders)foreach(var code in order.KizCodes) {
                    ct.ThrowIfCancellationRequested();using var image=WbPrintBundleService.RenderProduct(order,code,true);
                    var page=pdf.BeginPage(WbPrintBundleService.PageWidthPoints,WbPrintBundleService.PageHeightPoints);
                    page.DrawBitmap(image,new SKRect(0,0,WbPrintBundleService.PageWidthPoints,WbPrintBundleService.PageHeightPoints));pdf.EndPage();
                }pdf.Close();
            }
            File.Move(temporary,target,true);return target;
        }catch{File.Delete(temporary);throw;}
    }
}
