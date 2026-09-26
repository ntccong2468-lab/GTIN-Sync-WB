# GTIN Sync WB 0.4.2

Ứng dụng Windows độc lập (.NET 8 WinForms, bộ cài self-contained), không cần Python, website, máy chủ hoặc tài khoản ChatGPT. Cấu hình và lịch sử nằm trong `%LOCALAPPDATA%\GTIN Sync WB`. Khóa Национальный каталог và token từng cửa hàng WB được mã hóa riêng bằng Windows DPAPI; mã nguồn và bộ cài không chứa khóa seller.

## Cài đặt và sử dụng

1. Tải và chạy `GTIN-Sync-WB-Setup-0.4.2-win-x64.exe`. Bộ cài tạo shortcut **GTIN Sync WB** trên desktop. Mở bằng nhấp đúp.
2. Vào **Kết nối API**. Nhập API Key Национальный каталог theo tổ chức có quyền đối với thẻ; thêm từng cửa hàng WB với token Content riêng. Chọn ngày bắt đầu đủ sớm để bao gồm thẻ cũ và kiểm tra hai kết nối.
3. Bấm **Đồng bộ và đối chiếu GTIN** ở Tổng quan, Danh sách GTIN hoặc Bài đăng WB. Nút **Kiểm tra WB/NK** chỉ xác minh phản hồi của token, chưa tải sản phẩm. Ứng dụng đọc danh sách và chi tiết NK, rồi đọc mọi trang bài đăng WB của các cửa hàng. Bản đồng bộ chỉ được thay thế khi cả hai nguồn hoàn tất; nếu lỗi, dữ liệu cũ không được phép ghi. Thanh trạng thái báo nguồn NK hoặc cửa hàng WB bị lỗi.
4. **Danh sách GTIN** đặt «КОД ТОВАРА» và «НАИМЕНОВАНИЕ ТОВАРА» ở đầu bảng. **Bài đăng WB** có mỗi size một dòng, tìm theo tên/article/nmID/barcode, lọc cửa hàng/trạng thái, và nút **Chi tiết** để xem toàn bộ `skus`, `chrtID` và thuộc tính gốc. `vendorCode` là article seller, còn `skus` là barcode của từng size.
5. **Thêm GTIN WB** tự ghép article + màu + size trùng trực tiếp. Chỉ khi dữ liệu khác nhau hoặc mơ hồ mới cần seller nhập ánh xạ hoặc chọn GTIN NK cho một ngoại lệ cụ thể; lưu lựa chọn không gửi WB. Lọc trạng thái, chọn từng dòng hoặc **Chọn tất cả dòng khớp chắc chắn** trong phạm vi cửa hàng/bộ lọc hiện tại; có **Bỏ chọn tất cả**.
6. Bấm **Xem trước cập nhật** để xem ảnh, cửa hàng, article, màu, size, `chrtID`, GTIN và số bài đăng/size/GTIN. Chỉ nút **Xác nhận thêm GTIN** trong bước xem trước mới tạo tác vụ ghi. Màn hình **Bắt đầu thêm GTIN** hiển thị tiến độ và kết quả từng dòng. Có tạm dừng, tiếp tục và xuất CSV/XLSX trong Lịch sử.

**Nạp dữ liệu mẫu** chỉ để xem giao diện; chế độ mẫu chặn lệnh ghi. WB chỉ cho **thêm** barcode qua cập nhật thẻ, không cho thay/xóa mã cũ. GTIN của loại hàng khác với KIZ/Data Matrix của từng đơn vị hàng.

Nếu một danh sách vẫn trống sau khi đồng bộ hoàn tất, xem số GTIN NK và bài đăng WB ở Tổng quan. Kiểm tra đúng tổ chức có quyền truy cập GTIN, ngày bắt đầu đủ sớm, cửa hàng và quyền Content của token. Không gửi API Key/token trong tin nhắn hay ảnh chụp màn hình.

## Quy tắc an toàn

Ứng dụng chỉ tự đề xuất một GTIN thuộc đơn vị hàng (`trade-unit`) trên thẻ `published` khi article, màu và size đủ chắc chắn và chỉ có một ứng viên. Thiếu thuộc tính, nhiều ứng viên, khác màu, GTIN ở `chrtID` khác hoặc dữ liệu cũ đều bị chặn. Ngay trước khi ghi, ứng dụng đọc lại WB và đối chiếu lại; chỉ thêm GTIN vào `skus` đúng size, giữ barcode và thuộc tính cập nhật thẻ còn lại. HTTP 200 chỉ là WB nhận yêu cầu; ứng dụng đọc lại đúng `chrtID` trước khi báo đã thêm xong. Timeout ghi được lưu là chưa rõ kết quả, không gửi lại ngay.

## Build trên Windows

Cần .NET 8 SDK và Inno Setup 6:

```powershell
dotnet run --project tests/GTINSyncWB.Tests/GTINSyncWB.Tests.csproj -c Release
dotnet publish src/GTINSyncWB/GTINSyncWB.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer\GTINSyncWB.iss
```

Workflow `.github/workflows/windows.yml` chạy kiểm thử, build, cài trên Windows runner, xác nhận shortcut desktop, DPAPI và khởi động, rồi chụp 14 ảnh giao diện. Xem [báo cáo kiểm thử](BUILD-STATUS.md) để biết bản build cuối cùng và giới hạn của phép thử.

## Giới hạn đã biết

Chưa kiểm chứng bằng khóa NK và WB của seller; chưa ghi thử dữ liệu thật. Đồng bộ đọc bị ngắt phải đọc lại từ đầu, nhưng hàng đợi **ghi đã xác nhận** được lưu để tiếp tục. Bản dữ liệu lưu khi khởi động lại được đánh dấu cũ; cần đồng bộ lại trước một đợt ghi mới. Ảnh UI 125% trong CI dùng phóng tỷ lệ biểu mẫu, chưa tương đương việc đặt Windows Display Scaling thực tế ở 125%. Các thuộc tính riêng cho từng ngành hàng cần thử bằng thẻ seller cụ thể trước khi cập nhật nhiều thẻ.
