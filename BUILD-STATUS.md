# GTIN Sync WB 0.4.1 — báo cáo kiểm thử 26/09/2026

Mã nguồn: [ntccong2468-lab/GTIN-Sync-WB](https://github.com/ntccong2468-lab/GTIN-Sync-WB), commit ứng dụng `bb893a5a8fb4d32c7d099d225311939468cf792c`. [Windows CI run #36264328292](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36264328292) thành công: **47 assertions**, publish `win-x64` self-contained, tạo và cài bộ cài, kiểm tra shortcut desktop, khởi động và DPAPI. Tệp `GTIN-Sync-WB-Setup-0.4.1-win-x64.exe` có **49.213.557 byte**, SHA-256 `6d60f6529ecf62a6d399aa3fcdd04855031f3e4a238f3e35cd847e2c96b5772b`.

## Kiểm thử mô phỏng

Kiểm thử offline bao gồm: phân trang NK và WB; GTIN có số 0 đầu và cấp bao bì `trade-unit` so với `box`; tự ghép article/màu/size trực tiếp; hai mẫu cùng size, màu khác, tên gần giống, thiếu thuộc tính; ánh xạ size theo mẫu; nhiều GTIN và seller ghim ngoại lệ theo `chrtID`; chọn tất cả chỉ trong phạm vi cửa hàng/bộ lọc và loại dòng hết điều kiện; trùng GTIN hoặc ở size khác; giữ size, barcode và thuộc tính WB; thay đổi thẻ trước khi ghi; 429/403; HTTP 200 nhưng WB báo lỗi xử lý; timeout POST không gửi lại ngay; đọc lại đúng `chrtID`; tác vụ tồn tại sau khởi động lại; báo cáo XLSX giữ GTIN dạng chuỗi. Dữ liệu mẫu bị chặn ghi.

Windows CI cài bộ cài Inno Setup trên runner, kiểm tra shortcut desktop, khởi động ứng dụng và lưu/đọc lại hai bí mật DPAPI tách biệt. **14 ảnh chụp** lấy từ ứng dụng đã cài ở chế độ sáng/tối, cửa sổ thường/phóng to và biểu mẫu phóng 125%; đã xem lại Bài đăng WB, Thêm GTIN WB, Tổng quan và Kết nối API. Đây chưa phải máy seller với màn hình DPI 125% thật.

## Chưa thử bằng API thật

Không có API Key Национальный каталог hay WB Content token của seller trong môi trường build. Chưa kiểm chứng đọc thẻ/sản phẩm thật, phân quyền subaccount và ghi thẻ WB thật. Không thực hiện lệnh ghi lên WB bằng khóa seller. Khi có khóa nhập trực tiếp trong ứng dụng, nên thử có giám sát với một thẻ cụ thể trước khi chạy lô. Nếu mất kết nối khi đọc, tiến độ đọc chưa tiếp tục từ trang dang dở; lần sau đọc lại từ đầu. Hàng đợi ghi đã xác nhận vẫn lưu trên máy.

Tài liệu chính thức: [Национальный каталог](https://docs.crpt.ru/gismt/API_%D0%9D%D0%9A/) và [Wildberries Content API](https://dev.wildberries.ru/en/openapi/work-with-products). NK `/v4/product-list` giới hạn 10.000 thẻ mỗi cửa sổ và `/v3/feed-product` tối đa 25 GTIN/lần; WB `/content/v2/cards/update` tối đa 10 yêu cầu/phút, 10 MB/lần, xử lý bất đồng bộ có thể tới 30 phút. Ứng dụng ghi từng thẻ và đọc lại trước khi báo hoàn tất.
