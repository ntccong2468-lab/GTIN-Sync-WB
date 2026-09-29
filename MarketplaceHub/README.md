# Marketplace Hub 0.4.1

Bản Windows desktop thiết kế theo workflow WCode, toàn bộ giao diện và nhãn chức năng sử dụng tiếng Việt.

## Giao diện

- Sidebar tối dạng công cụ kho.
- Khu vực làm việc sáng, bảng dữ liệu lớn.
- Thanh chọn cửa hàng + kiểm tra API cố định phía trên.
- Nhật ký hoạt động ở cạnh dưới.
- Toàn bộ nút/menu/hướng dẫn bằng tiếng Việt.
- Các trạng thái thô do marketplace trả về vẫn được giữ trong dữ liệu kỹ thuật để đối chiếu lỗi.

## Mục chính

- Tổng quan.
- Đóng hàng FBS:
  - Đơn chờ đóng.
  - Quét & đóng hàng.
  - Nhãn & in.
  - Lô giao hàng.
- Mã KIZ.
- Sản phẩm.
- Giá sản phẩm.
- Sao chép bài đăng.
- Đăng ký Честный ЗНАК.
- Cấu hình Честный ЗНАК.
- Cửa hàng / API.
- Lịch sử.
- Cài đặt.

## Chức năng hoạt động

- SQLite local database.
- Mã hóa token/API key bằng Windows DPAPI.
- Kết nối nhiều cửa hàng WB/Ozon/Yandex.
- Kiểm tra API.
- Đồng bộ sản phẩm.
- Đồng bộ đơn FBS.
- Thay giá có xác nhận và tính nhanh theo %.
- Sao chép listing cùng marketplace.
- Quét DataMatrix/KIZ và lưu kho KIZ.
- Đóng hàng FBS theo adapter từng sàn.
- Tải nhãn WB/Ozon/Yandex.
- Đăng ký Честный ЗНАК: kiểm tra chứng thư và mở cổng đăng ký chính thức.
- Cấu hình Честный ЗНАК:
  - INN doanh nghiệp.
  - Production/Test.
  - Chọn chứng thư có private key.
  - Chế độ ký thủ công/bán tự động/tự động theo danh sách cho phép.
  - Lưu cấu hình vào SQLite.
  - Kiểm tra hiệu lực chứng thư.
- Lịch sử thao tác.

## Giới hạn kiểm thử

GitHub Actions không có token seller thật, CryptoPro/Rutoken hoặc УКЭП của seller. Vì vậy CI xác minh được build/cài/chạy và các chức năng cục bộ, nhưng không thể acceptance-test tài khoản marketplace hoặc chữ ký thật của người dùng.

Tích hợp True API ký challenge tự động chỉ nên bật sau khi kiểm thử trực tiếp trên máy Windows có CryptoPro/Rutoken/УКЭП thật.

## Bộ cài

`MarketplaceHub-Setup-0.4.1-win-x64.exe`

Pipeline:
restore → build → publish → tự kiểm tra local → mở GUI → Inno Setup → cài im lặng → tự kiểm tra sau cài → mở app sau cài → upload/commit installer.
