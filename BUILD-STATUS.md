# GTIN Sync WB 0.3.0 — báo cáo kiểm thử 26/09/2026

- Mã nguồn: `ntccong2468-lab/GTIN-Sync-WB`, commit ứng dụng `1bc4eded68d83ddfb0044b586319873fee27920c`.
- Windows CI: [run #36243827273](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36243827273) thành công. Chương trình kiểm thử offline đạt **30 assertions**; `dotnet publish` win-x64 self-contained, Inno Setup, cài đặt silent, kiểm tra shortcut desktop và khởi động ứng dụng đều đạt.
- Smoke kiểm tra DPAPI riêng cho khóa NK và khóa WB sau lưu/đọc lại trên Windows. Không dùng token seller thật.
- Ảnh chụp từ ứng dụng đã cài: trang Tổng quan và Ghép GTIN sáng/tối, dữ liệu mẫu/trống, cửa sổ phóng to, tỷ lệ biểu mẫu mô phỏng 125%.
- Tệp cài `GTIN-Sync-WB-Setup-0.3.0-win-x64.exe`, 49.196.628 byte, SHA-256 `30834d59f7b562f4ea0ed5bef98d723d45fe46c3d2abd8fa853e847a5088b80d`.

## Phạm vi kiểm thử offline

Phân trang NK và WB, GTIN có số 0 đầu, ánh xạ màu/size riêng sản phẩm, nhiều ứng viên, thẻ chưa công bố, barcode trùng hoặc ở size khác, payload giữ các trường được WB cho cập nhật, 429/403, timeout POST không gửi lại ngay, hàng đợi tồn tại qua khởi động lại, đọc lại đúng `chrtID`, XLSX lưu GTIN dạng chuỗi. Dữ liệu mẫu cục bộ bị chặn ghi.

## Chưa xác nhận thực tế

Chưa kiểm tra tương thích bằng API Key NK và WB Content token mới của một seller. Không gọi API ghi của seller. Chưa chạy cài đặt trên một máy Windows vật lý không có .NET; CI Windows runner có SDK nhưng ứng dụng được đóng gói self-contained. Tỷ lệ 125% trong CI là phóng tỷ lệ biểu mẫu, chưa phải thay đổi DPI của màn hình thật. Đồng bộ đọc bị ngắt sẽ bắt đầu lại toàn bộ khi chạy lần sau; hàng đợi **ghi đã xác nhận** được lưu để tiếp tục sau khi mở lại. Cần thử nghiệm có giám sát bằng thẻ seller cụ thể trước khi dùng cập nhật hàng loạt dữ liệu thật.

Nguồn tài liệu: [API NK](https://docs.crpt.ru/gismt/API_%D0%9D%D0%9A/) và [WB Product Management](https://dev.wildberries.ru/en/openapi/work-with-products). NK `/v4/product-list` tối đa 10.000 thẻ/khoảng, `/v3/feed-product` tối đa 25 GTIN/lần; WB `/content/v2/cards/update` tối đa 10 yêu cầu/phút, 10 MB/lần và xử lý có thể tới 30 phút. Ứng dụng gửi một thẻ/lần và kiểm tra lại kết quả.
