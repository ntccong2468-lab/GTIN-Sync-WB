# Marketplace Hub 0.5.1

Ứng dụng Windows desktop cho quy trình seller, giao diện tiếng Việt theo phong cách WCode: nền navy tối, điểm nhấn tím, bảng dữ liệu lớn và luồng thao tác tập trung.

## Giao diện 0.5.0

- Sidebar tối, menu tím và thanh cửa hàng cố định phía trên.
- Dashboard, FBS, Đăng ký Znack và Cấu hình Znack đã được tinh chỉnh theo bố cục WCode.
- Toàn bộ nhãn điều khiển do ứng dụng quản lý sử dụng tiếng Việt; dữ liệu sản phẩm gốc từ marketplace vẫn được giữ nguyên.
- Bảng FBS có ảnh sản phẩm, Order ID, sản phẩm, giá và bộ lọc trạng thái.
- Đăng ký Znack có ảnh, thuộc tính, barcode/GTIN, trạng thái và thao tác.
- Hỗ trợ hiển thị tốt hơn ở DPI 100%/125%.

## Chức năng chính

- Tổng quan/Dashboard.
- Đóng hàng FBS: đồng bộ đơn, lọc Đơn mới / Đang đóng gói / Đang giao, đóng hàng và tải nhãn.
- Đóng hàng FBO và Đơn hàng FBO.
- KIZ Mapping / DataMatrix.
- Sản phẩm, giá và sao chép bài đăng.
- Đăng ký Znack (Честный ЗНАК).
- Cấu hình Znack và chứng thư số.
- Cửa hàng / API, lịch sử in, nhật ký thao tác và cài đặt.

## Đồng bộ và dữ liệu

- SQLite local database.
- Mã hóa token/API key bằng Windows DPAPI.
- Kết nối nhiều cửa hàng Wildberries, Ozon và Yandex.
- Kiểm tra API, đồng bộ sản phẩm và đơn FBS.
- Thay giá có xác nhận.
- Sao chép listing cùng marketplace và chuyển ảnh nguồn khi API đích hỗ trợ.
- Quét/lưu KIZ, kiểm tra KIZ sẵn sàng trước thao tác FBS cần mã.
- Tải nhãn theo adapter marketplace.
- Cấu hình Znack lưu cục bộ; không lưu private key hoặc PIN chứng thư.

## Kiểm thử và bộ cài

GitHub Actions thực hiện:

`restore → build Release → publish self-contained win-x64 → self-test → GUI smoke test → Inno Setup → silent install → self-test sau cài → GUI smoke sau cài → upload artifact → commit installer đã xác minh`.

Bộ cài đầu ra:

`MarketplaceHub/dist/MarketplaceHub-Setup-0.5.1-win-x64.exe`

CI không có token seller thật hoặc thiết bị CryptoPro/Rutoken/УКЭП của người dùng, vì vậy các bài kiểm thử live marketplace/chữ ký số vẫn cần chạy trên máy seller có thông tin xác thực thật.


## Tương thích WCode 1.1.57

Bản 0.5.1 tái triển khai độc lập các hành vi công khai trong release WCode 1.1.57:

- ComboBox giữ giá trị đã chọn rõ ràng và tăng chiều cao dòng.
- Sidebar cố định icon + chữ bên trái, không nhảy vị trí khi hover.
- Nút mềm bo tròn có trạng thái hover/pressed/disabled.
- Đăng ký Znack dùng hàng đợi tối đa 2 worker.
- Nút hành động Znack dùng nhãn "Đăng ký"; trạng thái hàng đợi được hiển thị rõ.
- Tự động đưa mã vào lưu thông được coi là bắt buộc và migration dữ liệu cũ sang bật.
- Cấu hình Znack sắp theo omsId → omsConnection → chứng thư số.
- Hiển thị tên chủ chứng thư, INN và ngày hết hạn; parser nhận nhãn INN Nga/OID phổ biến.
- Hộp hỗ trợ có Enter gửi, Shift+Enter xuống dòng, Esc đóng.

Repo release WCode công khai chỉ chứa bộ cài và README; mã nguồn được README trỏ tới kho riêng không truy cập công khai. Vì vậy phần trên là clean-room implementation từ hành vi/release notes công khai, không sao chép mã nguồn độc quyền.
