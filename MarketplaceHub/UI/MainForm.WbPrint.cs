using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private readonly SemaphoreSlim printOperations = new(1,1);

    private async Task PrepareWbPrintAsync(StoreProfile store, IReadOnlyList<FbsOrderRow> orders, bool includeKiz, CancellationToken pageToken)
    {
        if (!await printOperations.WaitAsync(0,pageToken)) { ShowInfo("Đang có một bộ nhãn được chuẩn bị hoặc xem trước."); return; }
        try
        {
            using var optionsDialog = new WbPrintOptionsDialog(includeKiz);
            if (optionsDialog.ShowDialog(this)!=DialogResult.OK) return;
            var options = optionsDialog.Options;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(pageToken,lifetimeCts.Token);
            using var progressDialog = new WbPrintProgressDialog(()=>cancel.Cancel());
            progressDialog.Show(this);
            var progress = new Progress<string>(message=> {if(!pageToken.IsCancellationRequested)progressDialog.Report(message);});
            WbPrintBundle bundle;
            try
            {
                var labels = await app.Api.DownloadLabelsAsync(store,orders.Select(x=>x.ExternalOrderId),cancel.Token,progress);
                cancel.Token.ThrowIfCancellationRequested();
                var failures = labels.Where(x=>!x.Value.Success).ToArray();
                if(failures.Length>0)
                {
                    var detail=string.Join(Environment.NewLine,failures.Take(10).Select(x=>x.Key+": "+x.Value.Message));
                    throw new InvalidOperationException($"Chưa tạo bộ nhãn: {failures.Length}/{orders.Count} đơn chưa có sticker hợp lệ.\n{detail}"+
                        (failures.Length>10 ? $"\nCòn {failures.Length-10} đơn cùng lỗi; xem lịch sử in." : ""));
                }
                var remoteKiz = await app.Api.GetWbPrintKizAsync(store,orders.Select(x=>x.ExternalOrderId),cancel.Token,progress);
                var markingValidation=app.ValidateWbSupplyKiz(store,orders,ResolveWbGtins(store,orders),remoteKiz);
                if(!markingValidation.Success)throw new InvalidOperationException(markingValidation.Message);
                cancel.Token.ThrowIfCancellationRequested();
                var input=orders.Select(order=>
                {
                    remoteKiz.TryGetValue(order.ExternalOrderId,out var metadata);
                    return BuildWbPrintOrder(order,FindProductForOrder(store,order),labels[order.ExternalOrderId],
                        metadata?.Codes??Array.Empty<string>(),options.ProductLabel) with {NeedsKiz=order.NeedsKiz || metadata?.Required==true};
                }).ToArray();
                progressDialog.Report("Đang tạo PDF nhãn 58×40 mm và phiếu nhặt A4…");
                bundle=await Task.Run(()=>new WbPrintBundleService().Prepare(store.Name,input,options,null,cancel.Token),cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
            }
            finally {progressDialog.Close();}
            app.Db.Audit("In nhãn WB","Đã chuẩn bị PDF",$"{orders.Count} đơn · {bundle.Pages.Count} trang · {bundle.LabelsPdf}");
            if(pageToken.IsCancellationRequested || IsDisposed)return;
            using var preview = new WbPrintPreviewDialog(bundle, detail=>app.Db.Audit("In nhãn WB","Gửi máy in",detail));
            preview.ShowDialog(this);
        }
        catch(OperationCanceledException)
        {
            app.Db.Audit("In nhãn WB","Đã dừng","Dừng chuẩn bị bộ nhãn; chưa gửi tới máy in.");
            if(!pageToken.IsCancellationRequested && !IsDisposed)ShowInfo("Đã dừng chuẩn bị nhãn WB.");
        }
        catch(Exception ex)
        {
            app.Db.Audit("In nhãn WB","Lỗi",ex.Message);
            if(!pageToken.IsCancellationRequested && !IsDisposed)ShowInfo(ex.Message);
        }
        finally {printOperations.Release();}
    }

    private WbPrintOrder BuildWbPrintOrder(FbsOrderRow order,ProductRow? product,LabelResult label,IReadOnlyList<string> kiz,bool requireProduct)
    {
        var meta=product is null ? new ProductMetaValue("","","","","","",order.Sku,"") : ProductMeta(product);
        var raw=JsonNode.Parse(order.RawJson);
        var skus=(raw?["skus"] as JsonArray)?.Select(x=>x?.ToString()??"").Where(x=>x.Length>0).ToArray() ?? Array.Empty<string>();
        var barcode=skus.FirstOrDefault()??"";
        var size=meta.Size;
        if(product is not null)
        {
            var sizes=JsonNode.Parse(product.RawJson)?["sizes"] as JsonArray;
            if(sizes is not null)
            {
                var chrt=raw?["chrtId"]?.ToString();
                var matches=sizes.Where(x=>(string.IsNullOrWhiteSpace(chrt) || x?["chrtID"]?.ToString()==chrt)
                    && (skus.Length==0 || ((x?["skus"] as JsonArray)?.Any(code=>skus.Contains(code?.ToString()??""))??false))).ToArray();
                var hasExplicitIdentity=!string.IsNullOrWhiteSpace(chrt) || skus.Length>0;
                JsonNode? variant=matches.Length==1 ? matches[0] : sizes.Count==1 && !hasExplicitIdentity ? sizes[0] : null;
                if(variant is null && sizes.Count>0 && requireProduct)
                    throw new InvalidOperationException($"{order.ExternalOrderId}: chưa xác định được size/barcode chính xác của biến thể WB. Hãy đồng bộ lại sản phẩm và đơn.");
                size=variant?["techSize"]?.ToString()??(sizes.Count>1 ? "" : size);
                var variantCodes=(variant?["skus"] as JsonArray)?.Select(x=>x?.ToString()??"").ToArray()??Array.Empty<string>();
                var candidates=(skus.Length>0 ? variantCodes.Where(skus.Contains) : variantCodes).ToArray();
                var mapped=variant?["chrtID"]?.ToString() is { } variantId&&app.Db.ConfirmedGtinMappings(new StoreProfile(product.StoreId,product.Marketplace,"","","","","","",true)).TryGetValue((product.Sku,variantId),out var sellerGtin)?sellerGtin:"";
                var gtin=mapped.Length>0?mapped:ProductCatalog.UniqueGtin(candidates);
                if(requireProduct && gtin.Length==0)throw new InvalidOperationException($"{order.ExternalOrderId}: barcode của biến thể thiếu hoặc có nhiều GTIN khác nhau. Chưa chọn mã để in/gắn KIZ.");
                barcode=mapped.Length>0?mapped:gtin.Length>0?candidates.First(x=>ProductCatalog.NormalizeGtin(x)==gtin):"";
            }
            else if(barcode.Length==0)barcode=meta.Barcode;
        }
        byte[]? thumbnail=null;
        if(product is not null)imageCache.TryGetValue(ProductImageUrl(product),out thumbnail);
        return new(order.ExternalOrderId,product?.Name??order.Name,meta.Article,meta.Color,size,meta.Brand,barcode,order.Quantity,order.NeedsKiz,kiz,label,thumbnail);
    }
}
