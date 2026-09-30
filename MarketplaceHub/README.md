# Marketplace Hub 0.7.0

Bản 0.7.0 tái cấu trúc giao diện theo dashboard sáng xanh: sidebar xanh, nền mint, card trắng bo tròn và bố cục báo cáo trực quan.

## Kiến trúc được đối chiếu từ các repository công khai của rupphi

- **WB**: tách product / order / supply sync, cửa sổ 30 ngày, status batch, supply workflow và sticker 58×40.
- **Ozon**: catalog/posting sync tách riêng; FBS dùng posting_number, exemplar KIZ phải create/get → validate → set → status trước ship; nhãn là PDF chính thức của Ozon.
- **Độ ổn định UI**: các tác vụ mạng phải rời UI thread, không chạy đồng bộ chồng nhau, không giữ event handler cũ sau khi chuyển trang.
- **Finance**: dashboard đọc từ read-model riêng, không làm chặn luồng FBS.

## Thay đổi chính 0.7.0

- Gộp **Tổng quan + Tài chính** thành **Báo cáo**.
- Báo cáo có KPI, biểu đồ đơn 7 ngày, trạng thái tài chính và lịch sử các luồng sync.
- Thêm **Đồng bộ dữ liệu** chung cho Wildberries, Ozon và Yandex Market.
- Một cửa hàng chỉ có một sync chạy tại một thời điểm; phần ghi SQLite lớn chạy ngoài UI thread.
- FBS dùng chung một màn cho ba sàn.
- **Wildberries**: tạo supply → add order → SGTIN/KIZ → sticker PNG 58×40.
- **Ozon**: nếu posting cần KIZ, chạy exemplar create/get → validate → set → status rồi mới ship; nhãn là PDF chính thức Ozon.
- **Yandex Market**: gửi box layout + CIS/KIZ, chờ identifiers/status OK rồi mới chuyển READY_TO_SHIP; nhãn A9_HORIZONTALLY 58×40 PDF.
- Màn FBS hiển thị rõ loại nhãn của từng sàn và có **Tự động KIZ + in KIZ**.
- In KIZ tạo DataMatrix 58×40 từ mã đã gán.
- Sửa hiện tượng app đơ khi chuyển trang nhiều lần: hủy image request của trang cũ, bỏ Resize handler cũ, giới hạn cache ảnh và chặn sync trùng.
- Ảnh WebP/JPG/PNG vẫn dùng SkiaSharp.
- Thêm logo Marketplace Hub mới và icon cho EXE/installer.

## Bộ cài

`MarketplaceHub/dist/MarketplaceHub-Setup-0.7.0-win-x64.exe`
