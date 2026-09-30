# Marketplace Hub 0.6.1

Ứng dụng Windows desktop cho seller Wildberries, Ozon và Yandex, giao diện tiếng Việt theo phong cách dark navy/tím.

## Bản 0.6.1 — tái cấu trúc theo 4 màn hình tham chiếu

- Đóng hàng FBS được dựng lại theo bố cục: tab Đơn mới / Đang đóng gói / Đang giao, ô tìm kiếm, bộ lọc danh mục, bảng ảnh + Order ID + sản phẩm + giá.
- Double-click hoặc Enter trên đơn để mở màn hình Supply chi tiết.
- Màn hình Supply có nút quay lại, xuất nhãn dán, chuyển trạng thái đóng gói/giao hàng, thuộc tính Danh mục/Article/Màu/Kích cỡ và bảng nhiệm vụ.
- KIZ Mapping được dựng lại với GTIN, tên sản phẩm, trạng thái mapping, số KIZ, tồn sẵn sàng, lỗi gần nhất và nhóm thao tác xuất/thêm/xóa.
- Đăng ký Znack được dựng lại theo dạng catalog: ảnh, tên, article nguồn, giới tính, màu, size, Barcode WB/GTIN, trạng thái, phân trang và đăng ký hàng loạt.
- Queue Znack vẫn được lưu SQLite để không mất trạng thái khi đóng ứng dụng.
- Màu nền chính chỉnh đúng #0F172A, card/sidebar #1E293B, row xen kẽ #182235, border #334155, tím #9A41FE.
- Button radius 8 px, card radius 12 px.

## Chức năng nền tảng

- SQLite local + DPAPI bảo vệ token/API key.
- Multi-store Wildberries, Ozon, Yandex.
- FBS sync có cache lịch sử và trạng thái.
- Theo dõi FBO/FBW WB + Ozon.
- Dashboard tài chính WB.
- Sao chép listing và ảnh trong cùng marketplace.
- KIZ pool, mapping, gán KIZ local, xuất file KIZ.
- Cấu hình Znack + chứng thư số Windows.

## Giới hạn chủ động

Các thao tác cần tài khoản/chứng thư thật như mua KIZ SUZ, CryptoPro/CAdES, True API và LP_INTRODUCE_GOODS không được giả lập thành công. Chúng chỉ nên được bật sau khi kiểm thử trên máy seller có credential/chứng thư thật.

## Kiểm thử bộ cài

GitHub Actions chạy:

`restore → build Release → publish self-contained → E2E self-test → GUI smoke → Inno Setup → silent install → self-test sau cài → GUI smoke sau cài → upload artifact → commit installer`

Bộ cài:

`MarketplaceHub/dist/MarketplaceHub-Setup-0.6.1-win-x64.exe`
