# Marketplace Hub 0.3.0

Ứng dụng Windows desktop theo workflow WCode/FBS Packing Tool cho seller WB, Ozon và Yandex Market.

## Giao diện 0.3.0

- Sidebar tối, module nghiệp vụ cố định như công cụ WCode.
- Khởi động thẳng ở FBS Packing Station.
- Thanh chọn store + TEST API luôn hiển thị.
- Bảng dữ liệu lớn, thao tác bằng scanner/bàn phím.
- Console log ở dưới để thấy request/job/error ngay.
- PRICE dùng layout hai cột: sản phẩm bên trái, công thức/giá mới bên phải.
- FBS có SYNC, scan, PACK/READY và tải nhãn.

## Chức năng đã nối vào luồng thật

- SQLite local database.
- Token/API key mã hóa bằng Windows DPAPI CurrentUser.
- Test API:
  - Wildberries: official /ping.
  - Ozon: seller info.
  - Yandex: auth token info.
- Đồng bộ sản phẩm từ cả ba sàn.
- Wildberries đồng bộ card + giá seller.
- Ozon phân trang catalog và lấy product info.
- Thay giá thật theo API WB/Ozon/Yandex, có xác nhận trước khi gửi.
- Tính giá nhanh theo % ngay trên màn hình PRICE.
- Copy listing cùng marketplace:
  - WB: create product card từ dữ liệu card nguồn.
  - Yandex: offer-mappings/update.
  - Ozon: import-by-sku từ Ozon SKU nguồn.
- Đồng bộ FBS:
  - WB orders/new.
  - Ozon posting/fbs/list.
  - Yandex campaign orders.
- PACK/READY:
  - WB: tạo supply và đưa assembly order vào supply → confirm.
  - Ozon: /v4/posting/fbs/ship + verify posting.
  - Yandex: READY_TO_SHIP.
- Nhãn:
  - WB giải mã sticker base64 thành PNG 58×40.
  - Ozon tải PDF package label.
  - Yandex tải PDF A9 58×40.
- Quét DataMatrix/KIZ, tách GTIN-14 và lưu KIZ pool.
- Liệt kê certificate Windows CurrentUser để kiểm tra УКЭП.
- Audit log cho price/copy/FBS/KIZ.

## Giới hạn cần hiểu đúng

CI không có token seller, CryptoPro/Rutoken hay УКЭП thật, nên CI không thể xác nhận dữ liệu tài khoản cụ thể. Các lệnh ghi API chỉ thực thi sau khi bạn nhập credential và xác nhận thao tác.

Copy khác marketplace vẫn bị chặn nếu chưa có category/attribute mapper tương ứng, vì gửi thiếu thuộc tính có thể tạo card lỗi. Đây là chặn an toàn, không phải nút giả.

Честный ЗНАК hiện có local KIZ pool + certificate health; True API crypto verification và ký УКЭП tự động cần certificate/CryptoPro thật để acceptance-test trên máy seller.

## Build Windows

```powershell
dotnet publish .\MarketplaceHub.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false
```

Installer:

`MarketplaceHub-Setup-0.3.0-win-x64.exe`

Workflow kiểm tra:
restore → build → publish → functional self-test → GUI smoke → Inno Setup → silent install → installed self-test → installed GUI launch → artifact + commit installer.

## Security

Không commit token/API key/private key/PIN. Marketplace credentials được mã hóa DPAPI theo Windows user. Private key УКЭП không được lưu trong database.
