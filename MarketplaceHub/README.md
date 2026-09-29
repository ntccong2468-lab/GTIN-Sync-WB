# Marketplace Hub 0.1.0

Windows app scaffold mới cho seller Nga, tách trong nhánh `marketplace-hub-v2`.

Mục tiêu production: thay đổi giá hàng loạt; copy listing WB/Ozon/Yandex; FBS packing station; Честный ЗНАК/KIZ/УКЭП; multi-store; job engine + audit/rollback.

**0.1.0 là UI/architecture scaffold chạy được, chưa phải bản production và chưa bật write API thật.** Điều này chủ ý để không ghi nhầm vào tài khoản seller khi chưa có credential/mapping/test controlled SKU.

Xem `docs/ARCHITECTURE.md` và `PROMPT.md`.

Build:
```powershell
dotnet publish .\MarketplaceHub.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false
```

Workflow `.github/workflows/build-marketplace-hub.yml` build self-contained, cài thử, mở thử và tạo Inno Setup installer.

Security: không commit token/API key/private key/PIN. Marketplace secrets production dùng Windows DPAPI; УКЭП private key ở CryptoPro/Windows Certificate Store/Rutoken.
