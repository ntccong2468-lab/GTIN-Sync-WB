# Tiến độ triển khai kế hoạch 2026-10-02-order-truth-fbo-kiz-gtin

Đặc tả và kế hoạch đã được người dùng xác nhận. Nhánh: `fix/dashboard-fbs-stability`, repository: `ntccong2468-lab/GTIN-Sync-WB`.

- Nhiệm vụ 1 đã hoàn tất: chuẩn hóa trạng thái, đếm external order duy nhất, loại membership và hủy khỏi Đơn mới. Windows CI xanh tại `5472ed3`, regression run `36936280076`.
- Nhiệm vụ 2 đã hoàn tất: kết quả theo từng đơn, selection hỗn hợp không chặn đơn hợp lệ, batch `100/100/5` cho 205 đơn. Windows CI xanh tại `acfe9b5`, regression run `36937180622`.
- Nhiệm vụ 3 đã triển khai: nhật ký nhận đơn, phục hồi và dialog kết quả. RED `36959052332`; Windows xác nhận 24/24 FbsState (kể cả phục hồi không POST/PATCH trùng) và test dialog xanh tại `74a5d46`. Suite UI còn test FBO đỏ của nhiệm vụ 4.
- Nhiệm vụ 4 đang triển khai: FBO theo biến thể. RED tại `74a5d46`: thiếu bảng chọn/số lượng và BuildFboPreparationLabels. Chờ Windows xác minh triển khai.
- Nhiệm vụ 5–7 chưa hoàn tất: KIZ Mapping phân trang, Znack/WB GTIN, Trung tâm kiểm tra.

## Các quyết định thực thi

- Dùng bản sao làm việc chuyên dụng trên nhánh tính năng trong scratch. Không triển khai trên nhánh chính.
- Máy Linux hiện tại không có .NET SDK; thực thi kiểm thử trên Windows GitHub Actions. Không báo test xanh khi chỉ kiểm tra tĩnh.
- Quyền push lên repository/nhánh nêu trên đã được người dùng xác nhận rõ ràng. Chưa có quyền merge vào nhánh chính.
- Installer tiếp tục bị khóa cho đến khi mock và kiểm tra API thật hoàn tất. API key được nhập trong ứng dụng, không gửi trong hội thoại.
- Sau khi workspace tạm được khởi tạo lại, khôi phục từ GitHub `acfe9b5`; các test nhiệm vụ 3 chưa commit được dựng lại từ ngữ cảnh.
- Nhiệm vụ 4 bổ sung nhật ký FBO trong SQLite để phục hồi đúng biến thể/số lượng và KIZ đã giữ sau lỗi. Nhãn chuẩn bị có manifest `OfficialMarketplaceLabels=false`; không đóng giả sticker của sàn.

## Quy tắc phục hồi nhận đơn

- Trước POST/PATCH lưu ý định và mã thao tác. Supply ID được lưu ngay khi server trả về, membership được lưu sau từng lần readback.
- Nếu kết quả tạo shipment chưa rõ, chỉ nhận diện lại bằng tên chứa mã thao tác duy nhất. Không tìm thấy hoặc thấy nhiều kết quả thì giữ trạng thái cần đối soát và chặn POST mới.
- Nếu đã giữ Supply ID, tiếp tục đúng supply sau khi đọc lại trạng thái/membership; shipment cũ hoặc đã đóng không nhận thêm đơn.
