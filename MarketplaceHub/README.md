# Marketplace Hub 0.2.0

Ứng dụng Windows desktop cho seller Nga, phát triển trên nhánh `marketplace-hub-v2`.

## Giao diện

Bản 0.2.0 chuyển sang phong cách WCode/Packing Tool: sidebar tối, bảng dữ liệu lớn, thanh thao tác gọn, FBS scanner-first và console log trạng thái ở dưới.

## Chức năng đã nối vào luồng thật

- Lưu nhiều cửa hàng WB/Ozon/Yandex bằng SQLite.
- API key/token được mã hóa bằng Windows DPAPI CurrentUser.
- Test kết nối API theo từng cửa hàng.
- Đồng bộ sản phẩm từ API seller vào local database.
- Thay giá một SKU có bước xác nhận trước khi gửi.
- Đồng bộ hàng đợi FBS.
- Quét/tìm order hoặc SKU trong Packing Station.
- Quét DataMatrix/KIZ, tách GTIN và lưu KIZ pool cục bộ.
- Liệt kê certificate từ Windows CurrentUser certificate store.
- Tải nhãn FBS theo adapter marketplace.
- Copy listing cùng marketplace cho WB và Yandex; Ozon/cross-marketplace chỉ được phép publish khi đã có đủ category/attribute mapping, không giả thành công.
- Audit log cho các thao tác ghi quan trọng.

## Giới hạn xác minh

CI không chứa API key seller, CryptoPro/Rutoken hoặc УКЭП thật, nên không thể acceptance-test dữ liệu tài khoản thật. Pipeline chỉ xác minh build, SQLite/DPAPI/local self-test, GUI launch, installer, silent install và mở lại app đã cài.

Các endpoint marketplace có thể thay đổi; trước khi chạy batch lớn nên test bằng một store/SKU có kiểm soát.

## Build Windows

```powershell
dotnet publish .\MarketplaceHub.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false
```

Installer:

`MarketplaceHub-Setup-0.2.0-win-x64.exe`

Workflow `.github/workflows/build-marketplace-hub.yml` chạy:
restore → build → publish → functional self-test → GUI smoke → Inno Setup → silent install → installed self-test → installed GUI launch → artifact/commit installer.

## Security

Không commit token/API key/private key/PIN. Token/API key được mã hóa DPAPI theo Windows user. Private key УКЭП không được lưu trong database; ứng dụng chỉ đọc metadata certificate từ Windows Certificate Store.
