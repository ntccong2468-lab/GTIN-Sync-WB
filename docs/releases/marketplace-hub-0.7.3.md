# Marketplace Hub 0.7.3

WB: chọn tất cả đơn mới → tạo shipment hoặc thêm vào shipment đang mở cùng ngày Moscow → xác nhận toàn bộ membership/trạng thái → giữ/gắn/đọc lại KIZ → xuất nhãn.119 đơn tạo một shipment, chia request 100+19. Chi tiết hiển thị toàn bộ đơn, KIZ đúng GTIN/chủ sở hữu, nút xuất và giao hàng riêng.

Ozon/Yandex: tạo/thêm lượt đóng hàng cục bộ, nhận toàn bộ posting/order và từng đơn vị hàng, giữ KIZ trước khi gửi, đối soát mã/trạng thái sàn rồi mới xuất PDF. Retry layout Yandex dùng thứ tự unit reservation kể cả khi API đảo thứ tự CIS. Journal theo đơn chặn gửi lại ship/READY_TO_SHIP chưa rõ kết quả qua lượt mới. Lượt cũ chưa xong có thể mở/tiếp tục, không nhận đơn của ngày mới.

Đồng bộ catalog cả ba sàn lưu checkpoint/cursor giao dịch, retry quota cùng trang, giữ đầy đủ size/barcode/GTIN và ảnh theo SKU. UI tìm kiếm/lọc thiếu GTIN, phân trang 50 biến thể. Mã scanner-prefix/GS được đối chiếu cùng danh tính KIZ với index bổ sung; nâng cấp không xóa shop/đơn/catalog/mapping/KIZ/lịch sử.

## Kiểm chứng

- Source: `ec3c3b64080d8798b7fc8e0957a132927633aa42`.
- Windows CI: https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36808869705 .
- 106/106 hồi quy:31 WB/API,18 FBS Ozon/Yandex,8 catalog,11 lưu dữ liệu/khôi phục,11 PDF,27 UI.
- EXE sau publish và sau cài đặt:14/14 self-test mỗi bản; cả hai kiểm tra khởi động giao diện thành công.
- Installer commit: `22f7607b8a50453274d8b877c8119489f83d56a4`.
- `MarketplaceHub-Setup-0.7.3-win-x64.exe`:69,813,894 bytes.
- SHA256: `3972d7c7ca503098c330166f754e078cf3e180479f86e51ad78834e9c72db298`, khớp log CI và bản tải xuống.

Kiểm thử dùng API fixture, không dùng token seller thật hoặc máy in vật lý. Ozon multibox/giao hàng ngoài Ozon được chặn để xử lý trên seller portal; Yandex cần seller xác nhận layout khi API không trả phân bố hộp. Nhãn Ozon/Yandex in bằng trình xem PDF Windows. Tham chiếu source WCode công khai và release notes1.1.61; chưa kiểm chứng toàn bộ source production1.1.61 hoặc phiếu A4 Ozon.
