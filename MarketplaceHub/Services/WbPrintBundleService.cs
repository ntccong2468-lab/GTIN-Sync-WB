using MarketplaceHub.Core;
using SkiaSharp;
using System.Text.Json;
using ZXing;
using ZXing.Common;

namespace MarketplaceHub.Services;

public sealed record WbPrintOptions(bool ProductLabel, bool IncludeKiz, bool StickerFirst, int ProductCopies);
public sealed record WbPrintOrder(string OrderId, string Name, string Article, string Color, string Size,
    string Brand, string Barcode, int Quantity, bool NeedsKiz, IReadOnlyList<string> KizCodes, LabelResult Label,byte[]? Thumbnail=null);
public sealed record WbPickingRow(string Name,string Article,string Barcode,string Color,string Size,int Quantity,
    IReadOnlyList<string> OrderIds,byte[]? Thumbnail);
public sealed record WbPrintPage(string OrderId, string Kind, string Path);
public sealed record WbPrintBundle(string Directory, string LabelsPdf, string DetailsPdf, string ManifestPath,
    IReadOnlyList<WbPrintPage> Pages);

/// <summary>Prepares immutable 58×40 mm print jobs before any printer is contacted.</summary>
public sealed class WbPrintBundleService
{
    public const int Width = 580, Height = 400;
    public const float PageWidthPoints = 58 * 72 / 25.4f, PageHeightPoints = 40 * 72 / 25.4f;
    public static string HistoryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarketplaceHub", "PrintJobs");

    public WbPrintBundle Prepare(string shop, IReadOnlyList<WbPrintOrder> orders, WbPrintOptions options,
        string? root = null, CancellationToken ct = default)
    {
        if (orders.Count == 0) throw new InvalidOperationException("Chưa chọn đơn cần in.");
        if (orders.Select(x => x.OrderId).Distinct().Count() != orders.Count) throw new InvalidOperationException("Bộ nhãn có ID đơn bị trùng.");
        if (options.ProductCopies < 1 || options.ProductCopies > 20) throw new InvalidOperationException("Số bản nhãn sản phẩm phải từ 1 đến 20.");
        foreach (var order in orders)
        {
            if (!order.Label.Success) throw new InvalidOperationException($"{order.OrderId}: {order.Label.Message}");
            if (options.ProductLabel && string.IsNullOrWhiteSpace(order.Barcode))
                throw new InvalidOperationException($"{order.OrderId}: thiếu barcode sản phẩm. Đồng bộ sản phẩm hoặc chọn chỉ in sticker WB.");
            if (options.IncludeKiz && order.NeedsKiz && order.KizCodes.Count != order.Quantity)
                throw new InvalidOperationException($"{order.OrderId}: cần {order.Quantity} KIZ đã gắn nhưng có {order.KizCodes.Count}. Hãy hoàn tất gắn KIZ trước khi in.");
            if (options.IncludeKiz && order.KizCodes.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException($"{order.OrderId}: có KIZ rỗng.");
        }
        root ??= HistoryDirectory;
        System.IO.Directory.CreateDirectory(root);
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var final = Path.Combine(root, "WB-" + id);
        var staging = Path.Combine(root, ".preparing-" + id);
        System.IO.Directory.CreateDirectory(staging);
        var pages = new List<WbPrintPage>();
        try
        {
            foreach (var order in orders)
            {
                ct.ThrowIfCancellationRequested();
                void Sticker()
                {
                    using var image = RenderSticker(order.Label);
                    SavePage(image, order.OrderId, "sticker");
                }
                void Product()
                {
                    if (!options.ProductLabel && !options.IncludeKiz) return;
                    var codes = options.IncludeKiz && order.KizCodes.Count > 0 ? order.KizCodes : new[] { "" };
                    // Explicit KIZ selection also creates the product/KIZ page when the product checkbox is off.
                    if (!options.ProductLabel && codes.All(string.IsNullOrWhiteSpace)) return;
                    foreach (var code in codes)
                        for (var copy = 0; copy < options.ProductCopies; copy++)
                        {
                            ct.ThrowIfCancellationRequested();
                            using var image = RenderProduct(order, code, options.ProductLabel);
                            SavePage(image, order.OrderId, "product");
                        }
                }
                void SavePage(SKBitmap image, string orderId, string kind)
                {
                    var filename = $"{pages.Count + 1:D5}-{kind}.png";
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    using (var stream = File.Create(Path.Combine(staging, filename))) encoded.SaveTo(stream);
                    pages.Add(new WbPrintPage(orderId, kind, Path.Combine(final, filename)));
                }
                if (options.StickerFirst) { Sticker(); Product(); }
                else { Product(); Sticker(); }
            }
            ct.ThrowIfCancellationRequested();
            var labelPath = Path.Combine(staging, "WB-labels.pdf");
            using (var pdf = SKDocument.CreatePdf(labelPath))
            {
                foreach (var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    using var image = SKBitmap.Decode(Path.Combine(staging, Path.GetFileName(page.Path)))
                        ?? throw new InvalidOperationException("Không đọc được trang nhãn vừa chuẩn bị.");
                    var canvas = pdf.BeginPage(PageWidthPoints, PageHeightPoints);
                    using var paint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false };
                    canvas.DrawBitmap(image, new SKRect(0, 0, PageWidthPoints, PageHeightPoints), paint);
                    pdf.EndPage();
                }
                pdf.Close();
            }
            WriteDetails(Path.Combine(staging, "WB-order-details.pdf"), shop, orders, options, ct);
            File.WriteAllText(Path.Combine(staging, "job.json"), JsonSerializer.Serialize(new
            {
                Marketplace = "Wildberries", Shop = shop, PreparedAtUtc = DateTimeOffset.UtcNow,
                State = "Prepared", Options = options, Orders = orders.Select(x => new
                { x.OrderId, x.Name, x.Article, x.Barcode, x.Quantity, x.NeedsKiz,
                    KizIncluded = options.IncludeKiz ? x.KizCodes.Count : 0, x.Label.PartA, x.Label.PartB }), Pages = pages
            }, new JsonSerializerOptions { WriteIndented = true }));
            ct.ThrowIfCancellationRequested();
            System.IO.Directory.Move(staging, final);
            return new(final, Path.Combine(final, "WB-labels.pdf"), Path.Combine(final, "WB-order-details.pdf"), Path.Combine(final, "job.json"), pages);
        }
        finally { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); }
    }

    public static SKBitmap RenderSticker(LabelResult label)
    {
        if (!label.Success) throw new InvalidOperationException(label.Message);
        var image = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(image); canvas.Clear(SKColors.White);
            if (!string.IsNullOrWhiteSpace(label.FilePath))
            {
                using var official = SKBitmap.Decode(label.FilePath)
                    ?? throw new InvalidOperationException("Không đọc được PNG sticker chính thức của WB.");
                var scale = Math.Min((float)Width / official.Width, (float)Height / official.Height);
                var w = official.Width * scale; var h = official.Height * scale;
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
                canvas.DrawBitmap(official, new SKRect((Width-w)/2,(Height-h)/2,(Width+w)/2,(Height+h)/2),paint);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(label.Barcode) || string.IsNullOrWhiteSpace(label.PartA) || string.IsNullOrWhiteSpace(label.PartB))
                    throw new InvalidOperationException("Thiếu mã sticker chính thức WB. Không thể dựng nhãn.");
                // wcode uses the official barcode plus partA/partB, never the order ID.
                using var qr = RenderCode(label.Barcode, BarcodeFormat.QR_CODE);
                canvas.DrawBitmap(qr, new SKRect(170,80,410,320));
                foreach (var point in new[] {new SKPoint(20,20),new SKPoint(500,20),new SKPoint(20,320),new SKPoint(500,320)})
                    canvas.DrawBitmap(qr,new SKRect(point.X,point.Y,point.X+60,point.Y+60));
                Text(canvas,"WB",26,205,70,true);
                Text(canvas,label.PartA,426,180,28,true);
                Text(canvas,label.PartB.PadLeft(4,'0'),426,224,35,true);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static SKBitmap RenderCode(string text, BarcodeFormat format)
    {
        if (format == BarcodeFormat.DATA_MATRIX)
            text = "\u001d" + (text.StartsWith('\u001d') ? text[1..] : text);
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions { Width = format == BarcodeFormat.CODE_128 ? 530 : 300,
                Height = format == BarcodeFormat.CODE_128 ? 60 : 300, Margin = 2, PureBarcode = true }
        };
        // Keep the legacy encoder: CompactEncoding in ZXing 0.16.10 can corrupt GS after C40 runs.
        var pixels = writer.Write(text);
        var image = new SKBitmap(pixels.Width,pixels.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(pixels.Pixels,0,image.GetPixels(),pixels.Pixels.Length);
        return image;
    }

    private static SKBitmap RenderProduct(WbPrintOrder order, string kiz, bool productLabel)
    {
        var image = new SKBitmap(Width,Height,SKColorType.Bgra8888,SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(image); canvas.Clear(SKColors.White);
            if (!string.IsNullOrWhiteSpace(kiz))
            {
                using var matrix = RenderCode(kiz,BarcodeFormat.DATA_MATRIX);
                canvas.DrawBitmap(matrix,new SKRect(20,45,200,225));
                Text(canvas,"KIZ · GS1",28,250,18,true);
            }
            Text(canvas,order.Brand,220,34,25,true,340);
            Text(canvas,order.Name,220,73,21,true,340);
            Text(canvas,"Арт: " + order.Article,220,115,23,true,340);
            Text(canvas,"Цвет: " + order.Color,220,156,22,false,340);
            Text(canvas,"Размер: " + order.Size,220,197,24,true,340);
            Text(canvas,"WB · " + order.OrderId,220,239,20,false,340);
            if (productLabel)
            {
                using var barcode = RenderCode(order.Barcode,BarcodeFormat.CODE_128);
                canvas.DrawBitmap(barcode,new SKRect(25,278,555,343));
                Text(canvas,order.Barcode,38,382,24,false,360);
                Text(canvas,order.Label.PartB?.PadLeft(4,'0') ?? "",430,382,25,true,130);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static void Text(SKCanvas canvas,string text,float x,float y,float size,bool bold=false,float width=1000)
    {
        using var face = SKTypeface.FromFamilyName("Arial",bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var paint = new SKPaint { Typeface=face,TextSize=size,Color=SKColors.Black,IsAntialias=true };
        text = text.Replace('\r',' ').Replace('\n',' ');
        while (text.Length>1 && paint.MeasureText(text)>width) text=text[..^2]+"…";
        canvas.DrawText(text,x,y,paint);
    }

    public static IReadOnlyList<WbPickingRow> GroupVariants(IReadOnlyList<WbPrintOrder> orders)
        => orders.GroupBy(x=>new {x.Article,x.Barcode,x.Color,x.Size,Unknown=x.Barcode.Length==0 ? x.OrderId : ""})
            .Select(group=>new WbPickingRow(group.First().Name,group.Key.Article,group.Key.Barcode,group.Key.Color,group.Key.Size,
                group.Sum(x=>x.Quantity),group.Select(x=>x.OrderId).ToArray(),group.Select(x=>x.Thumbnail).FirstOrDefault(x=>x is not null)))
            .OrderBy(x=>x.Article,StringComparer.Ordinal).ThenBy(x=>x.Color,StringComparer.Ordinal).ThenBy(x=>x.Size,StringComparer.Ordinal).ToArray();

    private static void WriteDetails(string path,string shop,IReadOnlyList<WbPrintOrder> orders,WbPrintOptions options,CancellationToken ct)
    {
        using var pdf = SKDocument.CreatePdf(path);
        var variants=GroupVariants(orders);
        var number=0;
        foreach (var batch in variants.Chunk(9))
        {
            ct.ThrowIfCancellationRequested();
            var canvas = pdf.BeginPage(595,842);
            Text(canvas,"WB · Phiếu nhặt hàng / Лист подбора",28,34,18,true);
            Text(canvas,shop + " · " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC",28,55,10,false,539);
            Text(canvas,$"Đơn: {orders.Count} · Biến thể: {variants.Count} · Tổng sản phẩm: {orders.Sum(x=>x.Quantity)}",28,73,10);
            var columns=new float[]{28,59,108,310,364,497,567};
            using var border=new SKPaint {Color=SKColors.Gray,StrokeWidth=0.5f,Style=SKPaintStyle.Stroke};
            void RowBorder(float y,float height)
            {
                canvas.DrawRect(new SKRect(28,y,567,y+height),border);
                foreach(var x in columns.Skip(1).SkipLast(1))canvas.DrawLine(x,y,x,y+height,border);
            }
            RowBorder(91,26);
            foreach(var cell in new[]{("№",32f),("Фото",62f),("Артикул / Barcode",113f),("Размер",313f),("Цвет",368f),("Кол-во",501f)})
                Text(canvas,cell.Item1,cell.Item2,108,9,true);
            var y=117f;
            foreach (var variant in batch)
            {
                RowBorder(y,66);
                Text(canvas,(++number).ToString(),32,y+24,10);
                if(variant.Thumbnail is not null)
                {
                    using var picture=SKBitmap.Decode(variant.Thumbnail);
                    if(picture is not null)
                    {
                        var scale=Math.Min(43f/picture.Width,58f/picture.Height);
                        canvas.DrawBitmap(picture,new SKRect(62,y+4,62+picture.Width*scale,y+4+picture.Height*scale));
                    }
                }
                Text(canvas,variant.Article,113,y+18,10,true,192);
                Text(canvas,variant.Barcode,113,y+35,9,false,192);
                Text(canvas,variant.Name,113,y+52,8,false,192);
                Text(canvas,variant.Size,315,y+24,10,true,44);
                Text(canvas,variant.Color,369,y+24,10,false,121);
                Text(canvas,variant.Quantity.ToString(),516,y+26,14,true,44);
                y+=66;
            }
            Text(canvas,"Nhặt theo article + barcode + màu + size. Chi tiết từng đơn được lưu trong job.json.",28,790,9,false,539);
            pdf.EndPage();
        }
        pdf.Close();
    }
}
