# Đối chiếu luồng in WB

Nguồn mã công khai: `rupphi/test-wcode`, commit `8f7c3d1ac8598158029b8d037e2d3c128e240727` (nhánh thử Znack 1.1.32). Release notes production 1.1.61 tại `rupphi/relatest-wcode/releases/tag/v1.1.61` được dùng cho yêu cầu phiếu A4 theo biến thể. Repository `rupphi/source-wcode` trả 404 với kết nối hiện tại, nên không khẳng định đã rà toàn bộ source production 1.1.61.

WB API: `https://dev.wildberries.ru/en/docs/openapi/orders-fbs?locale=ru`, sticker tối đa 100 đơn, chỉ confirm/complete; metadata batch `/api/marketplace/v3/orders/meta`, ưu tiên `metaDetails`, giữ tương thích `meta` cũ.

| Bước | wcode đã đối chiếu | Marketplace Hub 0.7.2 |
|---|---|---|
| Chuẩn bị đơn | Load supply, enrich sticker theo batch, map orderId | Đơn đã chọn, kiểm tra trạng thái thật, batch 100, map orderId |
| Sticker | Dùng barcode/partA/partB chính thức để dựng QR | Ưu tiên PNG WB; fallback QR chỉ khi đủ barcode/partA/partB chính thức |
| KIZ | Kiểm tra metadata/GTIN, reserve/release mã, export rồi enqueue attach | Mã đã được gắn trong luồng đóng đơn; khi in đọc lại WB để dùng đúng mã, không thay mã |
| Trang sản phẩm | Brand/article/color/size, Code128, KIZ DataMatrix | Mẫu cố định cùng các trường, chọn đúng size/chrtId; giữ GS1 DataMatrix |
| Trang và bản sao | Barcode trước/sau sticker; copies | Sản phẩm/KIZ trước/sau sticker; 1–20 copies cho sản phẩm/KIZ; sticker một bản |
| File | PDF nhãn + NHAT_HANG PDF, publish atomic | PDF 58×40 + phiếu A4 + manifest + trang PNG, publish cả thư mục sau khi xong |
| Phiếu nhặt | Ghi chú 1.1.61: A4 tổng hợp biến thể WB/Ozon | WB A4 theo article/barcode/màu/size, số lượng và ảnh catalog đúng SKU nếu đã cache |
| Gửi in | Mở PDF để người bán xem/in | Preview từng trang, mở PDF, chọn máy in, một job với khổ/copies đã cố định |
| Lịch sử | Snapshot print job và hình ảnh | Lưu bộ PDF/manifest/trang nhãn và trạng thái gửi, truy cập từ Lịch sử in |
| Quota | Ghi chú 1.1.61: checkpoint/server retry | Cache sticker có thời hạn; đọc trạng thái mới; chờ retry header, dừng sau retry hữu hạn |

Không sao chép template designer tùy biến, bộ lọc “Ozon chỉ đơn chưa in”, hay hệ thống Znack đa máy trong bản sửa WB này. Ozon/Yandex giữ luồng PDF chính thức hiện có. App không nhận nhãn trống là thành công và không thay đổi trạng thái đơn chỉ để lấy sticker.

Kiểm thử gồm: phản hồi đảo thứ tự, sticker ID khác, 119 đơn, trạng thái thiếu/hủy/new, 429/cancel, cache theo token, metadata bắt buộc/optional/pending/invalid, PNG hỏng, thứ tự trang/bản sao, phiếu biến thể, giải mã QR và GS1 KIZ, giải mã raster cuối ở 203/254/300 dpi, dialog Windows và lỗi lịch sử sau spool.

Không có token seller hoặc máy in vật lý trong môi trường kiểm thử. Chưa xác nhận nhận hàng/scan thực tế tại WB hoặc tốc độ/khổ giấy của một driver nhiệt cụ thể. Bộ PDF mẫu trong CI dùng mã fixture, không được sử dụng để giao hàng.
