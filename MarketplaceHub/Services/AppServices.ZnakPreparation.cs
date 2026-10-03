using MarketplaceHub.Core;

namespace MarketplaceHub.Services;

public sealed partial class AppServices
{
    public Task<PriceUpdateResult> PrepareZnakRegistrationAsync(StoreProfile store,IReadOnlyList<ProductRow> products,CancellationToken ct=default)
    {
        var errors=new List<string>();var prepared=0;
        foreach(var product in products)
        {
            ct.ThrowIfCancellationRequested();
            if(product.StoreId!=store.Id||product.Marketplace!=store.Marketplace)throw new InvalidOperationException("Sản phẩm không thuộc cửa hàng đang chuẩn bị.");
            var variants=ProductCatalog.Entry(product).Variants;
            var confirmed=Db.ConfirmedGtinMappings(store);
            var gtins=variants.Select(v=>confirmed.TryGetValue((v.Sku,v.VariantId),out var gtin)?gtin:v.Gtin).Distinct(StringComparer.Ordinal).ToArray();
            var gtinValue=gtins.Length==1?GtinCode.Normalize(gtins[0]):"";
            if(variants.Count!=1||gtinValue.Length==0)
            {
                var message=product.Sku+": chọn/xác nhận GTIN cho từng biến thể trong KIZ Mapping trước.";errors.Add(message);
                Db.SaveZnakRegistrationPreparation(store.Id,product.Sku,"","ERROR",message);continue;
            }
            Db.SaveZnakRegistrationPreparation(store.Id,product.Sku,gtinValue,"READY","Chỉ chuẩn bị cục bộ; chưa gửi đăng ký National Catalog/True API.");prepared++;
        }
        return Task.FromResult(new PriceUpdateResult(errors.Count==0&&prepared>0,
            $"Đã chuẩn bị {prepared} sản phẩm cục bộ; chưa đăng ký trên Znack. Mở KIZ Mapping để kiểm tra từng biến thể và bảng API thật để xác minh National Catalog."+
            (errors.Count>0?"\n"+string.Join("\n",errors.Take(5)):"")));
    }
}
