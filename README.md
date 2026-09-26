# GTIN Sync WB 0.3.0

Ứng dụng Windows độc lập (.NET 8 WinForms), không cần Python, website hoặc tài khoản ChatGPT. Mã nguồn không chứa API key. Dữ liệu cấu hình, lịch sử được lưu trong `%LOCALAPPDATA%\GTIN Sync WB`; hai loại khóa được bảo vệ riêng bằng Windows DPAPI của tài khoản đang đăng nhập.

## Cài đặt và sử dụng

1. Chạy `GTIN-Sync-WB-Setup-0.3.0-win-x64.exe`, chọn tạo biểu tượng desktop, sau đó nhấp đúp **GTIN Sync WB**.
2. Vào **Kết nối API**, thêm từng cửa hàng WB với token mới có quyền Контент; nhập API Key Национальный каталог của tổ chức sở hữu thẻ. Đặt ngày bắt đầu đủ sớm để bao phủ thẻ cũ. Bấm kiểm tra từng kết nối.
3. Bấm **Đồng bộ dữ liệu thật** ở Tổng quan. Xem Danh sách GTIN và Bài đăng WB. Nếu NK không có thuộc tính mã mẫu/màu/size hoặc WB không có màu, dòng được giữ để kiểm tra.
4. Ở **Ghép GTIN**, chọn dòng, điền mã mẫu NK nếu khác `vendorCode`; thêm ánh xạ màu hoặc size riêng cho cửa hàng và sản phẩm, ví dụ XL → 48. Kiểm tra từng GTIN và chọn các dòng “Khớp chắc chắn”. Ánh xạ size tạo trong bản 0.2.0 cần lưu lại vì bản 0.3.0 giới hạn theo cửa hàng để tránh áp dụng nhầm.
5. Bấm **Xác nhận thêm GTIN**, kiểm tra số bài đăng/size theo từng cửa hàng trong hộp xác nhận. Ứng dụng lưu hàng đợi đã xác nhận, đọc thẻ WB mới nhất, thêm GTIN mà vẫn giữ barcode hiện có, gửi cập nhật, rồi đọc lại theo `chrtID`. Nếu kết quả không rõ, ứng dụng không gửi lại ngay. Dùng **Tiếp tục tác vụ đã xác nhận** sau khi mở lại. Xuất CSV hoặc XLSX ở trang Ghép GTIN hoặc Lịch sử.
6. **Nạp dữ liệu mẫu** thử giao diện mà không cần khóa. Chế độ mẫu không cho gửi yêu cầu ghi WB.

WB không cho sửa/xóa barcode cũ. GTIN là mã loại hàng; không dán KIZ/Data Matrix của từng sản phẩm vào `skus`.

Nếu mạng lỗi hoặc thẻ thay đổi trong lúc xem trước, đồng bộ lại trước khi ghi. Một số thuộc tính đặc thù của nhóm hàng có thể yêu cầu rà soát thêm với thẻ WB thật; chưa có kiểm thử bằng tài khoản seller. Không dùng token WB từng xuất hiện trong hội thoại: hãy cấp token mới.

## Build trên Windows

Cần .NET 8 SDK và Inno Setup 6. Chạy:

```powershell
dotnet run --project tests/GTINSyncWB.Tests/GTINSyncWB.Tests.csproj -c Release
dotnet publish src/GTINSyncWB/GTINSyncWB.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer\GTINSyncWB.iss
```

Workflow `.github/workflows/windows.yml` chạy test, build, cài đặt, xác nhận shortcut desktop, khởi động ứng dụng và chụp giao diện trên Windows. Bản cài 0.3.0 được tạo tại [Windows CI run #36243827273](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36243827273) từ commit `1bc4eded68d83ddfb0044b586319873fee27920c`: 30 assertions đạt, build, cài đặt và smoke test đạt. SHA-256 của tệp `.exe`: `30834d59f7b562f4ea0ed5bef98d723d45fe46c3d2abd8fa853e847a5088b80d`.

### Giao diện và độ tin cậy 0.3.0

Thanh bên và các ô Tổng quan dùng bố cục co giãn; nhóm thao tác Ghép GTIN xuống dòng, bảng cuộn ngang/dọc và có chỉ dẫn khi chưa có dữ liệu. Hàng đợi ghi đã xác nhận lưu cục bộ, trạng thái chưa rõ được đọc lại trước khi cân nhắc gửi tiếp. Có trang Cài đặt, ghi nhớ chế độ sáng/tối, xuất CSV/XLSX. Ảnh chụp từ ứng dụng đã cài trên Windows CI gồm giao diện sáng/tối, chế độ mẫu/trống, cửa sổ phóng to và tỷ lệ mô phỏng 125%.

## Giới hạn bản 0.3.0

Bản cài đã được kiểm tra cài đặt và khởi động trên Windows CI; chưa xác nhận tương thích bằng API thật của tài khoản seller. Không thực hiện ghi bằng khóa seller trong quá trình phát triển. Ảnh WB được tải để hiển thị dạng thu nhỏ nếu URL còn truy cập được. Bản xem trước được lưu nhưng đánh dấu cũ khi khởi động lại; phải đồng bộ mới trước khi tạo một đợt ghi mới. Hàng đợi ghi đã xác nhận có thể tiếp tục. Nếu đồng bộ đọc bị gián đoạn, ứng dụng đọc lại từ đầu. Ảnh 125% được tạo bằng cách phóng tỷ lệ biểu mẫu trong CI, chưa thay thế phép thử trên màn hình Windows đặt DPI 125% thực tế. Xem [báo cáo kiểm thử](BUILD-STATUS.md).
