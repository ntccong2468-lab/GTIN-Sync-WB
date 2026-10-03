using MarketplaceHub.Core;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Services;

namespace MarketplaceHub.UI;

public sealed partial class MainForm
{
    private string fbsWorkspaceMode="new";
    private OrderTruthSnapshot BuildOrderTruth(StoreProfile store)
    {
        IReadOnlySet<string> members = store.Marketplace == Marketplace.Wildberries
            ? app.Db.WbReceivedOrderIds(store.Id)
            : app.Db.MarketplaceReceivedOrderIds(store);
        return new OrderTruthService().Build(store, app.Db.Orders(store.Id), members, app.Db.OrderRemoteStates(store.Id));
    }

    private void ShowFbsPacking(){fbsWorkspaceMode="pack";ShowFbsWorkspace("pack");}

    private void ShowFbsWorkspace(string? requestedMode=null,string? message=null)
    {
        if(requestedMode is not null)fbsWorkspaceMode=requestedMode;ClearWork();activePage=()=>ShowFbsWorkspace(fbsWorkspaceMode);var token=pageCts.Token;
        var title=Title("Đóng hàng FBS");title.Left=4;work.Controls.Add(title);
        var refresh=ActionButton("↻ Cập nhật đơn hàng",220);refresh.Left=235;refresh.Top=0;work.Controls.Add(refresh);
        if(!string.IsNullOrWhiteSpace(message))work.Controls.Add(new Label{Left=470,Top=8,Width=Math.Max(240,work.ClientSize.Width-520),Height=44,ForeColor=message.Contains("Chưa",StringComparison.OrdinalIgnoreCase)?C.Orange:C.Green,Text=message,AutoEllipsis=true});
        var tabs=CardPanel();tabs.Left=4;tabs.Top=66;tabs.Height=72;tabs.Width=Math.Max(560,work.ClientSize.Width-55);work.Controls.Add(tabs);
        var a=TabButton("Đơn mới",fbsWorkspaceMode=="new",112);a.SetBounds(0,0,112,56);var b=TabButton("Đang đóng gói",fbsWorkspaceMode=="pack",165);b.SetBounds(118,0,165,56);var c=TabButton("Đang giao",fbsWorkspaceMode=="ship",130);c.SetBounds(289,0,130,56);tabs.Controls.AddRange(new Control[]{a,b,c});
        a.Click+=(_,_)=>ShowFbsWorkspace("new");b.Click+=(_,_)=>ShowFbsWorkspace("pack");c.Click+=(_,_)=>ShowFbsWorkspace("ship");
        var store=CurrentStore();if(store is null)return;if(fbsWorkspaceMode=="new")BuildFbsNewOrders(store,token,refresh);else BuildFbsShipments(store,token,refresh,fbsWorkspaceMode=="ship");
        var childLayout=activeWorkResize;
        SetWorkResize((sender,args)=>{tabs.Width=Math.Max(560,work.ClientSize.Width-55);childLayout?.Invoke(sender,args);});
    }

    private void BuildFbsNewOrders(StoreProfile store,CancellationToken token,Button refresh)
    {
        var search=DarkText("Tìm theo đơn hàng, sản phẩm, article...",400);search.SetBounds(4,145,400,44);work.Controls.Add(search);
        var category=DarkCombo(250);category.Left=414;category.Top=145;category.Items.AddRange(new object[]{"Tất cả danh mục","Có KIZ","Không KIZ"});category.SelectedIndex=0;work.Controls.Add(category);
        var clear=IconButton("×");clear.SetBounds(674,146,44,44);clear.Click+=(_,_)=>{search.Clear();category.SelectedIndex=0;};work.Controls.Add(clear);
        var receive=ActionButton("Nhận đơn",190,true);receive.Left=728;receive.Top=145;receive.Visible=false;work.Controls.Add(receive);
        var card=CardPanel();card.SetBounds(4,205,Math.Max(560,work.ClientSize.Width-55),Math.Max(260,work.ClientSize.Height-235));work.Controls.Add(card);
        var grid=DarkGrid();grid.Name="fbsNewOrders";grid.Dock=DockStyle.Fill;grid.ReadOnly=false;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;grid.GridColor=C.Border;
        grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="select",HeaderText="☐",Width=56,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="order",HeaderText="ORDER ID",Width=235,AutoSizeMode=DataGridViewAutoSizeColumnMode.None,ReadOnly=true});
        grid.Columns.Add(new DataGridViewImageColumn{Name="image",HeaderText="ẢNH",Width=120,AutoSizeMode=DataGridViewAutoSizeColumnMode.None,ImageLayout=DataGridViewImageCellLayout.Zoom,ReadOnly=true});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="product",HeaderText="SẢN PHẨM",MinimumWidth=260,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,ReadOnly=true});
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="price",HeaderText="GIÁ",Width=125,AutoSizeMode=DataGridViewAutoSizeColumnMode.None,ReadOnly=true});card.Controls.Add(grid);
        var loading=false;
        void Selection(){if(loading)return;grid.EndEdit();var n=grid.Rows.Cast<DataGridViewRow>().Count(r=>r.Cells[0].Value is true);receive.Visible=n>0;receive.Text=$"Nhận đơn ({n})";grid.Columns[0].HeaderText=n==0?"☐":n==grid.Rows.Count?"☑":"▣";grid.InvalidateColumn(0);}
        void Load(){loading=true;try{var keep=grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Cells[0].Value is true&&r.Tag is OrderTruthRow).Select(r=>((OrderTruthRow)r.Tag).ExternalOrderId).ToHashSet(StringComparer.Ordinal);DisposeImages(card);grid.Rows.Clear();var q=search.Text.Trim();var truth=BuildOrderTruth(store);
            foreach(var item in truth.Orders.Where(x=>x.State==OrderTruthState.New)){var order=item.Lines[0];if(q.Length>0&&!item.ExternalOrderId.Contains(q,StringComparison.OrdinalIgnoreCase)&&!item.Lines.Any(x=>x.Sku.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Name.Contains(q,StringComparison.OrdinalIgnoreCase)))continue;var needsKiz=item.Lines.Any(x=>x.NeedsKiz);if(category.SelectedIndex==1&&!needsKiz||category.SelectedIndex==2&&needsKiz)continue;var p=FindProductForOrder(store,order);var name=string.IsNullOrWhiteSpace(order.Name)?p?.Name??order.Sku:order.Name;var variants=item.Lines.Count>1?$" · {item.Lines.Count} dòng sản phẩm":"";var row=grid.Rows.Add(keep.Contains(item.ExternalOrderId),item.ExternalOrderId+"\n"+FormatOrderTime(order),null,name+variants+"\n"+BuildProductMetaLine(p,order),p?.Price is null?"":$"{p.Price:0.##} ₽");grid.Rows[row].Tag=item;grid.Rows[row].Height=98;var image=ProductImageUrl(p);if(!string.IsNullOrWhiteSpace(image))_=LoadImageAsync(grid,row,2,image);}
        }finally{loading=false;Selection();}}
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};grid.CellValueChanged+=(_,e)=>{if(e.ColumnIndex==0)Selection();};
        grid.ColumnHeaderMouseClick+=(_,e)=>{if(e.ColumnIndex!=0)return;var eligible=grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Tag is OrderTruthRow{State:OrderTruthState.New}).ToArray();var select=eligible.Any(r=>r.Cells[0].Value is not true);loading=true;foreach(var row in eligible)row.Cells[0].Value=select;loading=false;Selection();};
        receive.Click+=async(_,_)=>{var selected=grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Cells[0].Value is true).Select(r=>r.Tag).OfType<OrderTruthRow>().SelectMany(x=>x.Lines).ToArray();if(selected.Length==0)return;receive.Enabled=false;try{if(store.Marketplace==Marketplace.Wildberries)await BeginWbShipmentAsync(store,selected,false,token);else await BeginMarketplaceFbsAsync(store,selected,token,false);}finally{if(!receive.IsDisposed)receive.Enabled=true;}};
        search.TextChanged+=(_,_)=>Load();category.SelectedIndexChanged+=(_,_)=>Load();
        refresh.Click+=async(_,_)=>{refresh.Enabled=false;try{var r=await app.SyncOrdersAsync(store,lifetimeCts.Token);if(!token.IsCancellationRequested)ShowFbsWorkspace("new",r.Message);}catch(Exception ex){if(!token.IsCancellationRequested)ShowInfo(ex.Message);}finally{if(!refresh.IsDisposed)refresh.Enabled=true;}};
        SetWorkResize((_,_)=>{var w=Math.Max(560,work.ClientSize.Width-55);card.Width=w;card.Height=Math.Max(260,work.ClientSize.Height-card.Top-30);search.Width=Math.Max(220,w-category.Width-clear.Width-receive.Width-38);category.Left=search.Right+10;clear.Left=category.Right+10;receive.Left=clear.Right+10;});refreshActivePage=Load;Load();
    }

    private void BuildFbsShipments(StoreProfile store,CancellationToken token,Button refresh,bool shipping)
    {
        var card=CardPanel();card.SetBounds(4,145,Math.Max(560,work.ClientSize.Width-55),Math.Max(260,work.ClientSize.Height-175));work.Controls.Add(card);var grid=DarkGrid();grid.Name=shipping?"fbsShipping":"fbsPacking";grid.Dock=DockStyle.Fill;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
        grid.Columns.Add(new DataGridViewTextBoxColumn{Name="supply",HeaderText="SUPPLY",MinimumWidth=300,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="state",HeaderText="TRẠNG THÁI",Width=190,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="products",HeaderText="SẢN PHẨM",Width=130,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});grid.Columns.Add(new DataGridViewTextBoxColumn{Name="date",HeaderText="NGÀY",Width=235,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});card.Controls.Add(grid);
        void Load(){grid.Rows.Clear();if(store.Marketplace==Marketplace.Wildberries){foreach(var x in app.Db.WbSupplies(store,shipping)){var n=grid.Rows.Add($"{x.Id} - {x.Name}",shipping?"Đang giao":"Đang đóng gói",x.OrderCount?.ToString()??"—",x.CreatedAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm"));grid.Rows[n].Tag=x;grid.Rows[n].Height=84;}}else foreach(var x in app.Db.MarketplaceFbsBatches(store,shipping)){var n=grid.Rows.Add(x.Name,x.Status,x.Quantity.ToString(),x.CreatedAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm"));grid.Rows[n].Tag=x;grid.Rows[n].Height=84;}}
        async Task Open(){if(grid.SelectedRows.Count==0)return;var tag=grid.SelectedRows[0].Tag;if(tag is WbSupplySummary wb)await OpenWbSupplyAsync(store,wb.Id,token);else if(tag is MarketplaceFbsBatchSummary batch)ShowMarketplaceFbsBatch(store,batch.Id);}
        grid.CellDoubleClick+=async(_,e)=>{if(e.RowIndex>=0)await Open();};grid.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Open();}};
        refresh.Click+=async(_,_)=>{refresh.Enabled=false;try{if(store.Marketplace==Marketplace.Wildberries)await app.RefreshWbSuppliesAsync(store,lifetimeCts.Token);else await app.SyncOrdersAsync(store,lifetimeCts.Token);if(!token.IsCancellationRequested)ShowFbsWorkspace(shipping?"ship":"pack");}catch(Exception ex){if(!token.IsCancellationRequested)ShowInfo(ex.Message);}finally{if(!refresh.IsDisposed)refresh.Enabled=true;}};
        SetWorkResize((_,_)=>{card.Width=Math.Max(560,work.ClientSize.Width-55);card.Height=Math.Max(260,work.ClientSize.Height-card.Top-30);});refreshActivePage=Load;Load();
        if(store.Marketplace==Marketplace.Wildberries)_=Task.Run(async()=>{try{await app.RefreshWbSuppliesAsync(store,token);}catch{}if(!token.IsCancellationRequested&&!IsDisposed)BeginInvoke((Action)(()=>{if(!token.IsCancellationRequested)Load();}));});
    }
}

