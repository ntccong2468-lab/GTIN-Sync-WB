# GTIN Sync WB 0.1.0

Ứng dụng Windows độc lập (.NET 8 WinForms), không cần Python, website hoặc tài khoản ChatGPT. Mã nguồn không chứa API key. Dữ liệu cấu hình, lịch sử được lưu trong `%LOCALAPPDATA%\GTIN Sync WB`; hai loại khóa được bảo vệ riêng bằng Windows DPAPI của tài khoản đang đăng nhập.

## Cài đặt và sử dụng

1. Chạy `GTIN-Sync-WB-Setup-0.1.0-win-x64.exe`, chọn tạo biểu tượng desktop, sau đó nhấp đúp **GTIN Sync WB**.
2. Vào **Kết nối API**, thêm từng cửa hàng WB với token mới có quyền Контент; nhập API Key Национальный каталог của tổ chức sở hữu thẻ. Đặt ngày bắt đầu đủ sớm để bao phủ thẻ cũ. Bấm kiểm tra từng kết nối.
3. Bấm **Đồng bộ dữ liệu thật** ở Tổng quan. Xem Danh sách GTIN và Bài đăng WB. Nếu NK không có thuộc tính mã mẫu/màu/size hoặc WB không có màu, dòng được giữ để kiểm tra.
4. Ở **Ghép GTIN**, chọn dòng, điền mã mẫu NK nếu khác `vendorCode`; thêm ánh xạ size riêng cho sản phẩm, ví dụ XL → 48. Kiểm tra từng GTIN và chọn các dòng “Khớp chính xác”.
5. Bấm **Xác nhận thêm GTIN**, kiểm tra số dòng/cửa hàng trong hộp xác nhận. Ứng dụng đọc thẻ WB mới nhất, thêm GTIN mà vẫn giữ barcode hiện có, gửi cập nhật, rồi đọc lại theo `chrtID`. Xuất CSV ở trang Ghép GTIN hoặc Lịch sử.
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

Workflow `.github/workflows/windows.yml` chạy test, build, cài sạch và kiểm tra khởi động qua desktop shortcut trên Windows. Chỉ phát hành `.exe` khi workflow thực sự thành công.

## Giới hạn bản 0.1.0

Đây là mã nguồn ứng viên; chưa xác nhận tương thích bằng API thật. Không thực hiện ghi bằng khóa seller trong quá trình phát triển. Xuất XLSX chưa triển khai, hiện xuất CSV UTF-8 (Excel mở được). Ảnh WB được tải để hiển thị dạng thu nhỏ nếu URL còn truy cập được. Tác vụ lỗi lưu lịch sử; để thử lại, đồng bộ và xác nhận lại. Bản xem trước lưu trong phiên mở ứng dụng, lịch sử lưu trên máy.
