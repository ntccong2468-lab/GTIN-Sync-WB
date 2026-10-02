using MarketplaceHub.Core;
using MarketplaceHub.Services;
using System.Text.Json.Nodes;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private readonly SemaphoreSlim printOperations = new(1,1);

    private async Task PrepareWbPrintAsync(StoreProfile store,IReadOnlyList<FbsOrderRow> orders,bool includeKiz,CancellationToken pageToken)
    {
        var supplies=orders.Select(x=>app.Db.FindWbSupplyForOrder(store.Id,x.ExternalOrderId)).Distinct().ToArray();
        if(supplies.Length!=1||string.IsNullOrWhiteSpace(supplies[0])){ShowInfo("Chọn đúng shipment đã nhận trước khi xuất/in nhãn.");return;}
        await ShowFbsLabelJobAsync(new(store.Id,store.Marketplace,LabelTargetKind.WbSupply,supplies[0]!),pageToken);
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
