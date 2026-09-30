# Marketplace Hub 0.6.2

Bản Windows desktop cho seller Wildberries, Ozon và Yandex Market, giao diện tiếng Việt theo phong cách dark navy/tím.

## Sửa lỗi và tái cấu trúc 0.6.2

- **Chọn đơn mới FBS:** checkbox trong bảng FBS đã chuyển sang trạng thái chỉnh sửa được, commit ngay khi bấm. Có thể chọn nhiều đơn mới rồi bấm **Tạo shipment**.
- **Shipment Wildberries:** ứng dụng tạo supply, thêm tối đa 100 order vào supply và chuyển order sang `confirm`. Với đơn cần KIZ, ứng dụng chuẩn bị KIZ trước, tạo shipment rồi gắn SGTIN vào order WB sau khi order đã ở `confirm`.
- **Nhãn WB:** nhãn PNG 58×40 được tải từ WB và có thể gửi trực tiếp tới máy in mặc định. Có nút **In nhãn đã chọn** và **In nhãn dán WB** ở màn hình Supply.
- **Tự động mua KIZ:** nếu kho local thiếu KIZ, ứng dụng có thể dùng CryptoPro + chứng thư đã chọn + omsId/omsConnection để xác thực, tạo order SUZ, chờ mã sẵn sàng, tải KIZ và lưu vào SQLite. Nếu kết quả POST tạo order không chắc chắn do lỗi mạng/server, ứng dụng chặn tự động mua lại để tránh bị trừ tiền hai lần.
- **Ảnh sản phẩm:** sửa cách đọc URL ảnh cho WB/Ozon/Yandex, bổ sung fallback đọc từ RawJson, User-Agent và cache ảnh. Các màn FBS, Supply, FBO, Znack, Thay đổi giá và xem trước Sao chép bài đăng dùng chung bộ tải ảnh mới.
- **Thay đổi giá:** tách khỏi mục Thiết kế mẫu thành một mục riêng ở sidebar.
- **Sao chép bài đăng:** tách thành một mục riêng ở sidebar, có xem trước ảnh sản phẩm nguồn.
- **Đồng bộ Ozon:** thêm luồng riêng Sản phẩm → FBS → FBO/FBW, hiển thị lần đồng bộ và lỗi gần nhất theo từng cửa hàng.
- **Đồng bộ Yandex Market:** thêm luồng riêng Sản phẩm → Đơn hàng, có paging sản phẩm bằng pageToken.
- Sidebar được chuyển sang vùng cuộn để các module mới không chồng lên Lịch sử in/Cài đặt.

## An toàn KIZ

Tự động mua KIZ là thao tác thật trên SUZ khi máy seller có CryptoPro, chứng thư hợp lệ và cấu hình OMS thật. Ứng dụng không lưu PIN/private key. Bản này **mua và tải KIZ + gắn SGTIN vào đơn WB**; bước pháp lý đưa mã vào lưu thông qua `LP_INTRODUCE_GOODS` vẫn là pipeline riêng và chưa được coi là hoàn tất chỉ vì đã tải KIZ.

## Nền tảng

- SQLite local + DPAPI bảo vệ token/API key.
- Multi-store Wildberries, Ozon, Yandex.
- FBS cache lịch sử + sync state/run.
- FBO/FBW WB + Ozon.
- Dashboard tài chính WB.
- KIZ pool / mapping / gán order.
- Znack + Windows certificate/CryptoPro.
- Sao chép listing cùng marketplace và đồng bộ ảnh khi API đích hỗ trợ.

## Kiểm thử bộ cài

GitHub Actions chạy:

`restore → build Release → publish self-contained → E2E self-test → GUI smoke → Inno Setup → silent install → self-test sau cài → GUI smoke sau cài → upload artifact → commit installer`

Bộ cài:

`MarketplaceHub/dist/MarketplaceHub-Setup-0.6.2-win-x64.exe`
