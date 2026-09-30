# Marketplace Hub 0.6.0

Ứng dụng Windows desktop cho quy trình seller, giao diện tiếng Việt theo phong cách WCode: nền navy tối, điểm nhấn tím, bảng dữ liệu lớn và luồng thao tác tập trung.

## Giao diện 0.6.0

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

`MarketplaceHub/dist/MarketplaceHub-Setup-0.6.0-win-x64.exe`

CI không có token seller thật hoặc thiết bị CryptoPro/Rutoken/УКЭП của người dùng, vì vậy các bài kiểm thử live marketplace/chữ ký số vẫn cần chạy trên máy seller có thông tin xác thực thật.

## Bản 0.6.0

Bản này đưa các phần quan trọng đã đối chiếu từ mã nguồn công khai `rupphi/test-wcode` vào Marketplace Hub theo cách triển khai độc lập:

- Đồng bộ FBS không còn xóa cache đơn hàng khi API chỉ trả một queue con.
- Wildberries FBS đọc đơn mới + cửa sổ 30 ngày và cập nhật supplier/wb status theo batch.
- Ozon FBS chuyển sang `v4/posting/fbs/unfulfilled/list` với cursor và cutoff window.
- Lưu `sync_state` và `sync_runs` để thấy lần đồng bộ, số bản ghi đọc/ghi và lỗi cuối.
- Thêm màn hình Đơn hàng FBO/FBW có đồng bộ read-only cho Wildberries và Ozon.
- Thêm màn hình Đóng hàng FBO dựa trên catalog local, ảnh, barcode/GTIN và số KIZ sẵn sàng.
- Thêm Dashboard tài chính Wildberries đọc báo cáo quyết toán daily theo khoảng ngày.
- Hàng đợi Znack được lưu SQLite để trạng thái QUEUED/READY/ERROR không mất khi đóng ứng dụng.
- Tinh chỉnh giao diện theo token thật của WCode: #0F172A, #1E293B, #182235, #334155, #9A41FE; button radius 8 px, card radius 12 px.
- Toàn bộ sidebar được Việt hóa: Tổng quan, Tài chính, Ánh xạ KIZ.
- E2E self-test mở rộng kiểm tra sync state/run, FBO cache và Znack pipeline.

Các luồng có mutation trả phí hoặc cần chữ ký thật như mua KIZ SUZ, CryptoPro/CAdES và `LP_INTRODUCE_GOODS` vẫn fail-closed cho đến khi chạy trên máy seller có chứng thư/token thật; bản 0.6.0 không giả lập thành công các bước đó.

## Tương thích WCode 1.1.57

Bản 0.6.0 tái triển khai độc lập các hành vi công khai trong WCode:

- ComboBox giữ lựa chọn rõ ràng và tăng chiều cao dòng.
- Sidebar cố định icon + chữ bên trái.
- Nút mềm bo tròn có hover/pressed/disabled.
- Đăng ký Znack dùng hàng đợi tối đa 2 worker và lưu trạng thái queue.
- Tự động đưa mã vào lưu thông được coi là bắt buộc trong cấu hình local.
- Cấu hình Znack sắp theo omsId → omsConnection → chứng thư số.
- Hiển thị tên chủ chứng thư, INN và ngày hết hạn; parser nhận nhãn INN Nga/OID phổ biến.
- Hộp hỗ trợ có Enter gửi, Shift+Enter xuống dòng, Esc đóng.

Repo WCode công khai có mã nguồn tham chiếu tại `rupphi/test-wcode`; Marketplace Hub không sao chép cơ chế license hoặc nhận diện ứng dụng WCode và tiếp tục dùng branding riêng.
