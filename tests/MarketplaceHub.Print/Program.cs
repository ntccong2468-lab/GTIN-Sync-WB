using MarketplaceHub.Core;
using MarketplaceHub.Services;
using SkiaSharp;
using ZXing;
using ZXing.Common;

var failures = new List<string>();
var checks = 0;
void Check(string name, Action run) { checks++; try { run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
var output = Environment.GetEnvironmentVariable("MARKETPLACE_PRINT_SAMPLES") ?? Path.Combine(Path.GetTempPath(), "MarketplaceHub-print-tests");
Directory.CreateDirectory(output);
const string code = "010460123456789321serialABCDEFG\u001d91ABCD\u001d92proof";
var sticker = new LabelResult(true, "fixture", null, "!official-WB-101", "231648", "9753");
var order = new WbPrintOrder("101", "Áo khoác / Куртка", "SKU-A", "Đen", "48", "Brand", "4601234567893", 1, true, new[] {code}, sticker);
var service = new WbPrintBundleService();
Check("WB bundle preserves per-order page sequence, copy count and PDF dimensions", () =>
{
    var bundle = service.Prepare("Shop fixture", new[] {order, order with {OrderId="102", NeedsKiz=false, KizCodes=Array.Empty<string>()}},
        new WbPrintOptions(true, true, false, 2), output);
    Expect(bundle.Pages.Count == 6 && bundle.Pages.Select(x => x.Kind).SequenceEqual(new[] {"product", "product", "sticker", "product", "product", "sticker"}), "Incorrect page order/copies.");
    Expect(bundle.Pages.Take(3).All(x => x.OrderId == "101") && bundle.Pages.Skip(3).All(x => x.OrderId == "102"), "Pages from different orders interleaved.");
    Expect(File.ReadAllText(bundle.LabelsPdf).StartsWith("%PDF-") && File.Exists(bundle.DetailsPdf) && File.Exists(bundle.ManifestPath), "Bundle was not published fully.");
    using var bitmap = SKBitmap.Decode(bundle.Pages[0].Path);
    Expect(bitmap.Width == 580 && bitmap.Height == 400, "Labels are not 58×40mm at 254dpi.");
    File.WriteAllText(Path.Combine(output, "latest-bundle.txt"), bundle.LabelsPdf);
});
Check("WB QR fallback decodes the exact official barcode", () =>
{
    using var image = WbPrintBundleService.RenderSticker(sticker);
    var decoded = new BarcodeReaderGeneric().Decode(image.Bytes, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
    Expect(decoded?.Text == sticker.Barcode, "Fallback sticker QR does not encode the official payload.");
});
Check("KIZ remains GS1 DataMatrix with every separator", () =>
{
    foreach (var input in new[] {code, "\u001d"+code})
    {
        using var image = WbPrintBundleService.RenderCode(input, BarcodeFormat.DATA_MATRIX);
        var decoded = new BarcodeReaderGeneric().Decode(image.Bytes, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        Expect(decoded is not null && decoded.RawBytes[0] == 232 && decoded.Text.TrimStart('\u001d') == code, "GS1/FNC1 or separator data corrupted.");
    }
});
Check("Required KIZ or missing sticker prevents all bundle output", () =>
{
    var before = Directory.GetDirectories(output).Length;
    foreach (var invalid in new[] {order with {KizCodes=Array.Empty<string>()}, order with {Label=new LabelResult(false,"not ready")}, order with {Barcode=""}})
    {
        var rejected = false;
        try { service.Prepare("fixture", new[] {order, invalid with {OrderId="102"}}, new WbPrintOptions(true,true,false,1), output); }
        catch (InvalidOperationException) { rejected = true; }
        Expect(rejected && Directory.GetDirectories(output).Length == before, "A partial print bundle escaped validation.");
    }
});
Check("Sticker-first and sticker-only options are honored", () =>
{
    var first = service.Prepare("fixture", new[] {order}, new WbPrintOptions(true,true,true,1), output);
    Expect(first.Pages.Select(x => x.Kind).SequenceEqual(new[] {"sticker","product"}), "Sticker-first ignored.");
    var only = service.Prepare("fixture", new[] {order with {Barcode="", KizCodes=Array.Empty<string>()}}, new WbPrintOptions(false,false,true,1), output);
    Expect(only.Pages.Count == 1 && only.Pages[0].Kind == "sticker", "Sticker-only requires unrelated product/KIZ data.");
});
Check("Invalid official PNG cannot silently fall back to QR", () =>
{
    var path = Path.Combine(output,"bad.png"); File.WriteAllText(path,"not PNG");
    var rejected = false;
    try { using var image = WbPrintBundleService.RenderSticker(sticker with {FilePath=path}); }
    catch (InvalidOperationException) { rejected=true; }
    Expect(rejected,"Corrupt official sticker must be rejected.");
});
Check("A4 picking groups exact variants and preserves quantity", () =>
{
    var rows = WbPrintBundleService.GroupVariants(new[] {order,order with {OrderId="102",Quantity=2},
        order with {OrderId="103",Size="50"},order with {OrderId="104",Color="Trắng"}});
    Expect(rows.Count==3 && rows.Sum(x=>x.Quantity)==5,"Picking totals or variant identity were lost.");
    Expect(rows.Single(x=>x.Size=="48" && x.Color=="Đen").Quantity==3,"Same variant was not grouped.");
});
Check("Final product-page KIZ and barcode decode at 203, 254 and 300 dpi", () =>
{
    var cryptoFixtures=Enumerable.Range(1,12).Select(id=>"010460123456789321"+id.ToString("D13")+"\u001d91ABCD\u001d92"+
        Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToString()))));
    foreach(var marking in new[]{code,"010460123456789321serialABCDEFG\u001d91ABCD\u001d92"+new string('a',88)}.Concat(cryptoFixtures))
    {
    var bundle=service.Prepare("fixture",new[]{order with {KizCodes=new[]{marking}}},new WbPrintOptions(true,true,false,1),output);
    using var page=SKBitmap.Decode(bundle.Pages[0].Path);
    foreach(var dpi in new[]{203,254,300})
    foreach(var roundDimensions in new[]{false,true})
    {
        var scale=dpi/254f;
        int Pixels(float value)=>roundDimensions ? (int)Math.Round(value) : (int)value;
        using var raster=page.Resize(new SKImageInfo(Pixels(580*scale),Pixels(400*scale)),SKFilterQuality.None);
        foreach(var area in new[]{(new SKRectI(20,45,240,265),BarcodeFormat.DATA_MATRIX,marking),(new SKRectI(25,278,555,343),BarcodeFormat.CODE_128,order.Barcode)})
        {
            var rect=new SKRectI((int)(area.Item1.Left*scale),(int)(area.Item1.Top*scale),(int)(area.Item1.Right*scale),(int)(area.Item1.Bottom*scale));
            using var crop=new SKBitmap();raster.ExtractSubset(crop,rect);
            using var packed=crop.Copy();
            var reader=new BarcodeReaderGeneric {Options=new DecodingOptions {PossibleFormats=new[]{area.Item2},TryHarder=true}};
            var decoded=reader.Decode(packed.Bytes,packed.Width,packed.Height,RGBLuminanceSource.BitmapFormat.BGRA32);
            Expect(decoded?.Text.TrimStart('\u001d')==area.Item3,$"Final {area.Item2} corrupted at {dpi}dpi.");
        }
    }
    }
});
Check("Official WB PNG is preserved instead of reconstructed metadata", () =>
{
    var path=Path.Combine(output,"official.png");
    using(var qr=WbPrintBundleService.RenderCode("!official-file-payload",BarcodeFormat.QR_CODE))
    using(var data=qr.Encode(SKEncodedImageFormat.Png,100))
    using(var file=File.Create(path))data.SaveTo(file);
    using var page=WbPrintBundleService.RenderSticker(sticker with {FilePath=path,Barcode="other-metadata"});
    var decoded=new BarcodeReaderGeneric().Decode(page.Bytes,page.Width,page.Height,RGBLuminanceSource.BitmapFormat.BGRA32);
    Expect(decoded?.Text=="!official-file-payload","Official WB sticker was replaced by a synthetic one.");
});
Check("One KIZ cannot be printed for two different WB orders", () =>
{
    var rejected=false;
    try {service.Prepare("fixture",new[]{order,order with {OrderId="102"}},new WbPrintOptions(true,true,false,1),output);}
    catch(InvalidOperationException ex){rejected=ex.Message.Contains("KIZ");}
    Expect(rejected,"The same KIZ was reused for multiple orders.");
});

Check("Ozon and Yandex KIZ PDFs retain one page per unit and reject duplicates", () =>
{
    foreach(var market in new[]{"Ozon","Yandex"}) {
        var folder=Path.Combine(output,market);Directory.CreateDirectory(folder);
        var items=new[]{order with{Quantity=2,KizCodes=new[]{code,code+"2"}}};
        var path=MarketplaceKizPdfService.Write(market,items,folder);var data=File.ReadAllText(path);Expect(data.StartsWith("%PDF-") && System.Text.RegularExpressions.Regex.Matches(data,@"/Type /Page\b").Count==2,"Missing per-unit KIZ pages.");
        var rejected=false;try{MarketplaceKizPdfService.Write(market,new[]{order with{Quantity=2,KizCodes=new[]{code,code}}},folder);}catch(InvalidOperationException){rejected=true;}Expect(rejected,"Duplicate unit codes were printed.");
    }
});
Console.WriteLine($"{checks-failures.Count}/{checks} print regressions passed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
