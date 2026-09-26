# GTIN Sync WB 0.2.0

Ứng dụng Windows độc lập (.NET 8 WinForms), không cần Python, website hoặc tài khoản ChatGPT. Mã nguồn không chứa API key. Dữ liệu cấu hình, lịch sử được lưu trong `%LOCALAPPDATA%\GTIN Sync WB`; hai loại khóa được bảo vệ riêng bằng Windows DPAPI của tài khoản đang đăng nhập.

## Cài đặt và sử dụng

1. Chạy `GTIN-Sync-WB-Setup-0.2.0-win-x64.exe`, chọn tạo biểu tượng desktop, sau đó nhấp đúp **GTIN Sync WB**.
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

Workflow `.github/workflows/windows.yml` chạy test, build, cài sạch, xác nhận lối tắt desktop, khởi động ứng dụng và chụp giao diện trên Windows. Bản cài 0.2.0 được tạo tại [Windows CI run #11](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36237955290) từ commit `ab9584bdc1181b771e59fc06e72c02d3b26c5326`: 17 assertions đạt, build, cài đặt và smoke test đạt. SHA-256 của tệp `.exe`: `d7d95d37718f04daf292d1f03a7f4b8d6e76569be0b307a1a8f37ef392bd2de1`.

### Sửa giao diện 0.2.0

Thanh bên rộng hơn; sáu ô Tổng quan và thông báo dùng bố cục co giãn; các nhóm thao tác Ghép GTIN xuống dòng khi hẹp, bảng cuộn ngang/dọc và có chỉ dẫn khi chưa có dữ liệu. Ảnh chụp từ ứng dụng đã cài trên Windows CI gồm giao diện sáng/tối, chế độ mẫu/trống, cửa sổ phóng to và tỷ lệ mô phỏng 125%.

## Giới hạn bản 0.2.0

Bản cài đã được kiểm tra cài đặt và khởi động trên Windows CI; chưa xác nhận tương thích bằng API thật của tài khoản seller. Không thực hiện ghi bằng khóa seller trong quá trình phát triển. Xuất XLSX chưa triển khai, hiện xuất CSV UTF-8 (Excel mở được). Ảnh WB được tải để hiển thị dạng thu nhỏ nếu URL còn truy cập được. Tác vụ lỗi lưu lịch sử; để thử lại, đồng bộ và xác nhận lại. Bản xem trước lưu trong phiên mở ứng dụng, lịch sử lưu trên máy. Ảnh 125% được tạo bằng cách phóng tỷ lệ biểu mẫu trong CI, chưa thay thế phép thử trên màn hình Windows đặt DPI 125% thực tế.
