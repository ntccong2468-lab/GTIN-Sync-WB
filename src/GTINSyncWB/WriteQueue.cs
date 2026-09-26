using System.Text.Json.Nodes;

namespace GTINSyncWB;

public enum WriteState { Queued, Sending, Received, Verifying, Unknown, Success, Review, Failed }

public sealed class WriteLine
{
    public long ChrtId { get; set; }
    public string Gtin { get; set; } = "";
    public string WbSize { get; set; } = "";
    public string Color { get; set; } = "";
    public WriteState Status { get; set; } = WriteState.Queued;
    public string Detail { get; set; } = "";
}

public sealed class WriteJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShopId { get; set; } = "";
    public string ShopName { get; set; } = "";
    public long NmId { get; set; }
    public string VendorCode { get; set; } = "";
    public string ExpectedCard { get; set; } = "";
    public DateTimeOffset ConfirmedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public WriteState Status { get; set; } = WriteState.Queued;
    public string Detail { get; set; } = "";
    public List<WriteLine> Lines { get; set; } = [];

    public static WriteJob FromRows(Listing listing,IEnumerable<MatchRow> rows)
    {
        var selected=rows.Where(x=>x.Selected && x.Status==MatchStatus.Exact && x.ShopId==listing.ShopId && x.NmId==listing.NmId).ToList();
        if(selected.Count==0)throw new InvalidOperationException("Không có dòng hợp lệ được xác nhận");
        return new WriteJob{ShopId=listing.ShopId,ShopName=listing.ShopName,NmId=listing.NmId,VendorCode=listing.VendorCode,
            ExpectedCard=CardPayload.Base(listing.Raw).ToJsonString(),
            Lines=selected.Select(x=>new WriteLine{ChrtId=x.ChrtId,Gtin=x.Gtin,WbSize=x.WbSize,Color=x.Color}).ToList()};
    }
    public void Mark(WriteState status,string detail)
    {
        Status=status;Detail=detail;
        foreach(var line in Lines){line.Status=status;line.Detail=detail;}
    }
}

public interface IWriteGateway
{
    Task<Listing> ReadOne(Shop shop,string token,long nmId,CancellationToken ct);
    Task Update(string token,JsonArray payload,CancellationToken ct);
    Task<string> Errors(string token,long nmId,CancellationToken ct);
}

public sealed class WriteProcessor(Storage disk,IWriteGateway gateway,Func<DateTimeOffset>? clock=null,Func<string,string>? tokenForShop=null,Func<TimeSpan,CancellationToken,Task>? delay=null)
{
    private readonly Func<DateTimeOffset> now=clock??(()=>DateTimeOffset.UtcNow);
    private readonly Func<string,string> token=tokenForShop??(_=>"");
    private readonly Func<TimeSpan,CancellationToken,Task> wait=delay??((span,ct)=>Task.Delay(span,ct));

    public async Task Process(WriteJob job,CancellationToken ct)
    {
        if(job.Status is WriteState.Success or WriteState.Review or WriteState.Failed)return;
        var shop=new Shop{Id=job.ShopId,Name=job.ShopName};
        var secret=token(job.ShopId);
        Listing fresh;
        try{fresh=await gateway.ReadOne(shop,secret,job.NmId,ct);}
        catch(OperationCanceledException){return;}
        catch(ApiFailure){Set(job,job.Status==WriteState.Queued?WriteState.Queued:WriteState.Unknown,"Chưa đọc lại được thẻ WB; chưa gửi thêm yêu cầu");return;}

        if(AtWrongSize(fresh.Raw,job)){Set(job,WriteState.Review,"GTIN đang nằm ở chrtID khác; dừng để kiểm tra");return;}
        if(AllPresent(fresh.Raw,job)){Set(job,WriteState.Success,"Đã đọc lại GTIN ở đúng chrtID");return;}
        if(job.Status is WriteState.Sending or WriteState.Received or WriteState.Verifying or WriteState.Unknown)
        {
            if(job.SubmittedAt is null || now()-job.SubmittedAt.Value<TimeSpan.FromMinutes(30))
            {Set(job,WriteState.Unknown,"Chưa rõ kết quả; chờ WB xử lý tới 30 phút rồi kiểm tra lại, chưa gửi trùng");return;}
        }
        if(CardPayload.Base(fresh.Raw).ToJsonString()!=job.ExpectedCard)
        {Set(job,WriteState.Review,"Thẻ WB thay đổi từ lúc xác nhận; đồng bộ và đối chiếu lại");return;}

        var rows=job.Lines.Select(line=>new MatchRow{Selected=true,ShopId=job.ShopId,NmId=job.NmId,VendorCode=job.VendorCode,ChrtId=line.ChrtId,Gtin=line.Gtin,WbSize=line.WbSize,Color=line.Color,Status=MatchStatus.Exact}).ToList();
        JsonArray payload;
        try{payload=CardPayload.Add(fresh.Raw,rows);}
        catch(InvalidOperationException e){Set(job,WriteState.Review,e.Message);return;}
        job.SubmittedAt=now();Set(job,WriteState.Sending,"Đang gửi cập nhật WB");
        try{await gateway.Update(secret,payload,ct);Set(job,WriteState.Received,"WB đã nhận, đang kiểm tra thẻ");}
        catch(OperationCanceledException){Set(job,WriteState.Unknown,"Đã dừng trong lúc gửi; cần đọc lại WB trước khi quyết định");return;}
        catch(ApiFailure e) when(e.UnknownOutcome){Set(job,WriteState.Unknown,"Chưa rõ kết quả gửi; sẽ đọc lại trước khi thử tiếp");await ReadbackUnknown(job,shop,secret);return;}
        catch(ApiFailure e){Set(job,WriteState.Failed,e.Message);return;}

        Set(job,WriteState.Verifying,"Đang kiểm tra GTIN ở đúng chrtID");
        var deadline=now()+TimeSpan.FromMinutes(30);
        while(now()<deadline)
        {
            try
            {
                var observed=await gateway.ReadOne(shop,secret,job.NmId,ct);
                if(AtWrongSize(observed.Raw,job)){Set(job,WriteState.Review,"GTIN xuất hiện ở size khác; cần kiểm tra");return;}
                if(AllPresent(observed.Raw,job)){Set(job,WriteState.Success,"Đã đọc lại GTIN ở đúng chrtID");return;}
                var error=await gateway.Errors(secret,job.NmId,ct);
                if(error.Length>0){Set(job,WriteState.Failed,error);return;}
                await wait(TimeSpan.FromSeconds(20),ct);
            }
            catch(OperationCanceledException){Set(job,WriteState.Unknown,"Ngừng kiểm tra; cần đọc lại WB khi tiếp tục");return;}
            catch(ApiFailure){Set(job,WriteState.Unknown,"Mất kết nối lúc kiểm tra; chưa xác nhận kết quả");return;}
        }
        Set(job,WriteState.Unknown,"WB chưa xác nhận sau 30 phút; kiểm tra lại trước khi thử tiếp");
    }
    private async Task ReadbackUnknown(WriteJob job,Shop shop,string secret)
    {
        try
        {
            var fresh=await gateway.ReadOne(shop,secret,job.NmId,CancellationToken.None);
            if(AtWrongSize(fresh.Raw,job))Set(job,WriteState.Review,"GTIN xuất hiện ở size khác; cần kiểm tra");
            else if(AllPresent(fresh.Raw,job))Set(job,WriteState.Success,"Đã xác nhận GTIN sau khi gửi không rõ kết quả");
        }
        catch(ApiFailure){/* Keep unknown for explicit resume. */}
    }
    private void Set(WriteJob job,WriteState status,string detail)
    {
        job.Mark(status,detail);
        var jobs=disk.LoadJobs();var index=jobs.FindIndex(x=>x.Id==job.Id);
        if(index>=0)jobs[index]=job;else jobs.Add(job);
        disk.SaveJobs(jobs);
    }
    private static bool AllPresent(JsonObject card,WriteJob job)=>job.Lines.All(line=>Json.A(card,"sizes").Any(s=>Json.L(s,"chrtID")==line.ChrtId && Json.A(s,"skus").Any(v=>v?.ToString()==line.Gtin)));
    private static bool AtWrongSize(JsonObject card,WriteJob job)=>job.Lines.Any(line=>Json.A(card,"sizes").Any(s=>Json.L(s,"chrtID")!=line.ChrtId && Json.A(s,"skus").Any(v=>v?.ToString()==line.Gtin)));
}
