# Marketplace Hub 0.7.1

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

`MarketplaceHub-Setup-0.7.1-win-x64.exe`


## Sửa lỗi 0.7.1

- Bố cục Báo cáo co giãn: ở cửa sổ hẹp, tài chính xuống dưới biểu đồ; bộ lọc ngày không chồng nút đồng bộ. KPI vận hành ghi rõ trạng thái hiện tại, biểu đồ theo khoảng ngày đã chọn.
- Bảng FBS dùng nền chọn mint, cột sản phẩm luôn còn chỗ đọc, thanh thao tác tự xuống dòng. Tên nhãn trong chi tiết theo đúng WB / Ozon / Yandex.
- Tự đồng bộ cửa hàng bật mỗi 60 giây. Các luồng đồng bộ cùng cửa hàng dùng chung khóa, gồm cập nhật đơn riêng và đồng bộ toàn bộ.
- Khi đổi trang, các điều khiển và ảnh cũ được giải phóng; kết quả chậm không thay trang mới. Tải/giải mã ảnh tối đa bốn luồng; ảnh trong bảng giới hạn 160 px, cache dữ liệu ảnh tối đa 64 MiB.
- Không ghi đơn WB thành “mới” khi API trạng thái lỗi; chỉ coi Ozon ship thành công sau khi đọc lại trạng thái.
- KIZ Ozon phải được xác nhận cho từng product_id / exemplar_id đã gửi, đủ mọi đơn vị; trạng thái thiếu, chờ hay từ chối đều chặn đóng đơn.
- Với đơn có nhiều dòng, chọn một dòng vẫn kiểm tra toàn bộ sản phẩm và số KIZ bắt buộc trước khi đóng posting.
- Đơn Ozon `awaiting_deliver` và Yandex `PROCESSING/READY_TO_SHIP` ở nhóm Đang đóng gói, để chọn in nhãn ngay sau khi đóng đơn; đang vận chuyển mới ở nhóm Đang giao.
- Nhãn tải về phải là PDF/PNG thật trước khi chuyển sang in.
- Nhãn KIZ dùng GS1 DataMatrix với FNC1, giữ nguyên AI và ký tự GS; kiểm thử giải mã lại ảnh sinh ra trước khi in.

## Kiểm tra và giới hạn

Các dự án `tests/MarketplaceHub.Contracts` và `tests/MarketplaceHub.UI` dùng HTTP giả lập, không gọi tài khoản bán hàng. CI Windows chạy cả hai trước khi đóng bộ cài; EXE đã publish và EXE sau cài đặt đều phải qua self-test và khởi động giao diện.

Báo cáo quyết toán trực tiếp hiện hỗ trợ WB. Ozon / Yandex hiển thị dữ liệu sản phẩm, đơn hàng và lịch sử đồng bộ; chưa có adapter quyết toán cho hai sàn này.

In nhãn PDF sử dụng lệnh Print của ứng dụng PDF mặc định trên Windows. Máy cần có ứng dụng PDF hỗ trợ in và máy in đã cấu hình. Kiểm thử CI không xác nhận bản in vật lý hay quyền/token API thật của người bán. KIZ bắt buộc cần GTIN hợp lệ và mã sẵn có, hoặc cấu hình SUZ / chứng thư CryptoPro để mua mã.
