# GTIN Sync WB 0.4.2 — kiểm thử 26/09/2026

Mã nguồn: [ntccong2468-lab/GTIN-Sync-WB](https://github.com/ntccong2468-lab/GTIN-Sync-WB), commit ứng dụng `6fc2e9e97d0f8e0f5da9fd2ade62632b18a5f964`.

[Windows CI run #36265988424](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36265988424) thành công: **49 assertions**, publish `win-x64` self-contained, Inno Setup, cài đặt trên Windows runner, xác nhận shortcut desktop, mở ứng dụng và kiểm tra lưu/đọc lại khóa DPAPI qua smoke test. CI chụp 14 ảnh giao diện gồm Tổng quan, Danh sách GTIN, Bài đăng WB, Thêm GTIN WB, cửa sổ thường/phóng to và kiểm tra tỷ lệ biểu mẫu 125%.

Bộ cài `GTIN-Sync-WB-Setup-0.4.2-win-x64.exe`: **49.210.306 byte**, SHA-256 `fed06a9e64ba826158bd0c6eb35c486c8e83ebe77001b0667411b7610c179587`.

## Lỗi đồng bộ được xử lý

Ảnh người dùng cho thấy trang GTIN và WB trống, trạng thái **WB đã phản hồi**. Trạng thái đó là kết quả của **Kiểm tra WB**, chỉ thử token, chưa đồng bộ. Bản 0.4.2 ghi rõ sự khác nhau, thêm nút đồng bộ ngay ở hai trang dữ liệu, báo nguồn NK/WB bị lỗi, tổng số thẻ đọc xong và hướng dẫn khi kết quả bằng 0.

Luồng NK trước đây chia ngày từ 2000 thành các khoảng 30 ngày và chờ giới hạn request từng khoảng. Với 26 năm, nó tạo hàng trăm lượt trước khi đến WB. Bản mới thử toàn bộ khoảng ngày; nếu NK trả HTTP 413 hoặc vượt 10.000 bản ghi, nó chia đôi rồi tiếp tục phân trang. [CI chạy kiểm thử đỏ trước sửa](https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36265788953) ở tình huống một khoảng nhiều tháng nhưng ít bản ghi, sau đó kiểm thử xanh với cách xử lý mới.

## Phạm vi xác nhận

Kiểm thử API dùng dữ liệu mô phỏng, gồm phân trang NK/WB, GTIN giữ số 0 đầu, tách khoảng khi 413, đối chiếu màu/size/mẫu, giữ nguyên thẻ WB, timeout ghi, đọc lại đúng `chrtID`, lỗi xử lý bất đồng bộ và tiếp tục hàng đợi. CI **không có khóa thật của seller**; chưa thể xác nhận quyền, dữ liệu sản phẩm hay đồng bộ NK/WB trong tài khoản thật. Không gửi lệnh ghi thật lên WB. Nếu sau khi cài vẫn có lỗi đồng bộ, cần thông báo trạng thái lỗi cụ thể xuất hiện sau khi bấm **Đồng bộ và đối chiếu GTIN**, cùng nguồn đang đọc; không gửi khóa.

Ảnh 125% của CI dùng `Scale(1.25)` trong biểu mẫu; chưa tương đương thử cấu hình Windows Display Scaling 125% trên máy seller. CI là Windows runner có sẵn nhiều thành phần phát triển, nên phép cài thử chưa chứng minh tuyệt đối một máy Windows sạch không có .NET SDK; bản publish có runtime self-contained.
