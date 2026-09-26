using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GTINSyncWB;
public static class Matching
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars=s.Trim().Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c));
        return new string(chars.ToArray()).ToUpperInvariant();
    }
    public static string Color(Listing card)
    {
        if (!string.IsNullOrWhiteSpace(card.Color)) return card.Color;
        foreach(var a in Json.A(card.Raw,"characteristics"))
        {
            var name=Json.S(a,"name");
            if(name.Equals("Цвет",StringComparison.OrdinalIgnoreCase) || name.Equals("color",StringComparison.OrdinalIgnoreCase))
                return (a?["value"] as JsonArray)?.FirstOrDefault()?.ToString() ?? a?["value"]?.ToString() ?? "";
        }
        return "";
    }
    public static List<MatchRow> Build(IReadOnlyList<CatalogItem> goods,IReadOnlyList<Listing> cards,Settings config)
    {
        var result=new List<MatchRow>();
        foreach(var card in cards)
        foreach(var size in Json.A(card.Raw,"sizes"))
        {
            var chrt=Json.L(size,"chrtID"); var wbSize=Json.S(size,"techSize");
            if(string.IsNullOrWhiteSpace(wbSize))wbSize=Json.S(size,"wbSize");
            var color=Color(card);
            var row=new MatchRow{ShopId=card.ShopId,Shop=card.ShopName,NmId=card.NmId,VendorCode=card.VendorCode,Color=color,WbSize=wbSize,ChrtId=chrt,Status=MatchStatus.Missing};
            result.Add(row);
            if(chrt==0 || Normalize(color)=="" || Normalize(card.VendorCode)=="" || Normalize(wbSize)=="") {row.Detail="Thiếu định danh WB (mã hàng, màu, size hoặc chrtID)";continue;}
            // A product-specific model mapping and size mapping are explicit; no global size guess.
            var model=config.ProductRules.GetValueOrDefault(card.ShopId+":"+card.VendorCode,card.VendorCode);
            var mappedSize=config.SizeRules.GetValueOrDefault(Normalize(model)+":"+Normalize(wbSize),wbSize);
            var candidates=goods.Where(g=>Normalize(g.Model)==Normalize(model) && Normalize(g.Color)==Normalize(color) && Normalize(g.Size)==Normalize(mappedSize)).ToList();
            if(candidates.Count==0){row.Detail="Không có đủ mã mẫu + màu + size trùng khớp";continue;}
            if(candidates.Any(g=>!g.Accessible)){row.Status=MatchStatus.AccessDenied;row.Detail="Không có quyền đọc đủ thuộc tính thẻ";continue;}
            if(candidates.Any(g=>!g.Status.Equals("published",StringComparison.OrdinalIgnoreCase))){row.Status=MatchStatus.Unpublished;row.Detail="Thẻ chưa công bố";continue;}
            if(candidates.Count!=1){row.Status=MatchStatus.Multiple;row.Detail=$"{candidates.Count} GTIN ứng viên";continue;}
            var gtin=candidates[0].Gtin;
            row.Gtin=gtin;row.Source=candidates[0].Source;
            if(!Gtin.IsValid(gtin)){row.Status=MatchStatus.Conflict;row.Detail="GTIN không hợp lệ";continue;}
            var slots=cards.Where(c=>c.ShopId==card.ShopId).SelectMany(c=>Json.A(c.Raw,"sizes").Select(s=>(Card:c,Size:s))).Where(pair=>Json.A(pair.Size,"skus").Any(x=>x?.ToString()==gtin)).ToList();
            if(slots.Any(pair=>Json.L(pair.Size,"chrtID")!=chrt || pair.Card.NmId!=card.NmId)){row.Status=MatchStatus.Conflict;row.Detail="GTIN đã nằm ở size khác";continue;}
            row.Status=slots.Count>0?MatchStatus.Existing:MatchStatus.Exact;
            row.Detail=row.Status==MatchStatus.Exact?"Sẵn sàng, cần seller xác nhận":"GTIN đã có";
        }
        return result;
    }
}
public static class Gtin
{
    public static bool IsValid(string code)
    {
        if(code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit))return false;
        var sum=0;for(var i=code.Length-2,weight=3;i>=0;i--,weight=weight==3?1:3)sum+=(code[i]-'0')*weight;
        return (10-sum%10)%10==code[^1]-'0';
    }
}
public static class CardPayload
{
    public static JsonArray Add(JsonObject fresh,IEnumerable<MatchRow> rows)
    {
        var selected=rows.Where(r=>r.Status==MatchStatus.Exact && r.Selected).ToList();
        if(selected.Count==0)throw new InvalidOperationException("Không có dòng hợp lệ được chọn");
        if(selected.Any(r=>r.NmId!=Json.L(fresh,"nmID") || r.VendorCode!=Json.S(fresh,"vendorCode")))throw new InvalidOperationException("Bài đăng WB đã thay đổi định danh");
        var fields=new[]{"nmID","vendorCode","kizMarked","brand","title","description","dimensions","characteristics","sizes"};
        var copy=new JsonObject();foreach(var key in fields)if(fresh[key]!=null)copy[key]=fresh[key]!.DeepClone();
        var sizes=Json.A(copy,"sizes");
        foreach(var r in selected)
        {
            if(!Gtin.IsValid(r.Gtin))throw new InvalidOperationException("GTIN không hợp lệ");
            var size=sizes.SingleOrDefault(s=>Json.L(s,"chrtID")==r.ChrtId) as JsonObject ?? throw new InvalidOperationException("Không tìm thấy chrtID hiện tại");
            var elsewhere=sizes.Any(s=>Json.L(s,"chrtID")!=r.ChrtId && Json.A(s,"skus").Any(x=>x?.ToString()==r.Gtin));
            if(elsewhere)throw new InvalidOperationException("GTIN xuất hiện ở size khác");
            var skus=size["skus"] as JsonArray ?? throw new InvalidOperationException("Size hiện tại thiếu danh sách barcode");
            if(!skus.Any(x=>x?.ToString()==r.Gtin))skus.Add(r.Gtin);
        }
        return new JsonArray(copy);
    }
}
