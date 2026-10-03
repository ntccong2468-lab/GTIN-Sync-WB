# Kế hoạch sửa các thiếu sót sau đối chiếu mã nguồn

> **For agentic workers:** Use superpowers:executing-plans; tiếp tục trong phiên hiện tại theo phạm vi đã được người dùng xác nhận.

**Goal:** Sửa các lỗi có thể tái hiện trong nhận đơn, KIZ, sản phẩm, in và báo cáo của ứng dụng hiện có.

**Architecture:** Giữ .NET 8/WinForms/SQLite. Bổ sung contract đọc thuộc tính Ozon và phục hồi block SUZ vào gateway/service hiện hữu; không chuyển sang NestJS/Next.js hay thêm IDE. Mọi output sàn vẫn lấy từ API chính thức.

**Tech Stack:** C#/.NET 8, WinForms, SQLite, SkiaSharp, GitHub Actions Windows.

**Spec:** `docs/superpowers/specs/2026-10-02-order-truth-fbo-kiz-gtin-design.md`; yêu cầu tiếp tục và đối chiếu toàn bộ các kho ngày 03/10; phạm vi cụ thể trong `docs/reviews/2026-10-03-reference-gap-audit.md`.

## Ràng buộc chung

- Không xóa dữ liệu seller hoặc giải phóng KIZ đã gán khi replay.
- Không mua lại SUZ khi order trước có kết quả chưa xác định; giữ external order qua lỗi mạng.
- Không giả nhãn marketplace; không đánh dấu xuất xong khi thiếu bất kỳ tệp bắt buộc.
- Nhánh `fix/dashboard-fbs-stability`; không merge main, không phát hành bộ cài trước mốc API thật.
- Kiểm thử Windows CI vì môi trường chỉnh mã hiện tại không có .NET/WinForms runtime.

## Trọng tâm rà soát

- KIZ đã ASSIGNED xuất hiện lại trong block: vẫn thuộc đơn cũ, không trả cho đơn mới.
- SUZ trả mã sai GTIN hoặc auth lỗi: không nhập pool, giữ checkpoint.
- Ozon trả attributes của product/offer khác: không công bố trang thiếu hoặc ảnh/size ghép nhầm.
- Quyết toán thiếu field, trùng ID hoặc khác currency: không hiển thị tổng RUB sai.
- Resize cửa sổ sau nhiều lần chuyển tab: cả bảng con và tabs vẫn được bố trí.

## Task 1: Phục hồi SUZ và GTIN

Files: `MarketplaceHub/Services/AppServices.cs`, `tests/MarketplaceHub.FbsState/Program.cs`.
Interfaces: giữ `EnsureKizQuantityAsync(long,string,string,int,CancellationToken)` và tuple result; `NormalizeGtin14` dùng validator GTIN chung. Helpers nội bộ đọc `/order/codes/blocks` và `/order/codes/retry` qua clientToken.

- [x] Thêm ca checksum, ERROR có order, expired block, auth lỗi và sai GTIN.
- [x] Quan sát RED trên Windows: ca retry/expired/checkpoint hiện thất bại.
- [x] Sửa: tái sử dụng external ID ở stage chưa hoàn tất; giữ ID trong catch; kiểm tra toàn bộ mã trước khi UPSERT; đọc lại pool AVAILABLE không có owner.
- [x] GREEN suite FbsState 44/44 tại 09704bc và commit.

## Task 2: Danh tính, size và barcode sản phẩm

Files: `MarketplaceHub/Services/MarketplaceProductSync.cs`, `MarketplaceHub/UI/MainForm.WbPrint.cs`, `MarketplaceHub/UI/MainForm.cs`; suites ProductSync/UI.
Interfaces: giữ `ProductCatalogEntry`; lưu metadata được giải theo tên category vào JSON card; `BuildWbPrintOrder` giữ barcode variant/order độc lập với GTIN.

- [x] Thêm ca Ozon manufacturer XXL so với Russian 52, attributes foreign; WB mapping khác barcode và barcode kỹ thuật chưa đăng ký GTIN.
- [x] Quan sát RED.
- [x] Đọc attributes đúng product + offer, category definitions theo category/type, ưu tiên manufacturer; builder WB giữ techSize và barcode sàn.
- [x] GREEN ProductSync 11/11, UI 41/41 tại 09704bc và commit.

## Task 3: Phiếu nhặt và resize FBS

Files: `MarketplaceHub/Services/WbPrintBundleService.cs`, `MarketplaceHub/UI/MainForm.MarketplaceFbs.cs`, `MarketplaceHub/UI/MainForm.FbsV2.cs`; suites Print/UI.
Interfaces: `public static string WritePickingList(string marketplace,string shop,IReadOnlyList<WbPrintOrder> orders,string folder,CancellationToken ct=default)`; exporter truyền tất cả item, KIZ PDF chỉ nhận item yêu cầu KIZ.

- [x] Thêm ca phiếu A4 Ozon gom XL 2+3 thành 5 và XXL 1; resize tăng rộng bảng.
- [x] Quan sát RED.
- [x] Xuất PDF tạm rồi move; giữ mỗi posting/item đúng identity; đếm đơn distinct; compose handler tabs với handler child.
- [x] GREEN Print 14/14, UI 41/41 tại 09704bc và commit.

## Task 4: Báo cáo đúng dữ liệu và nghiệm thu

Files: `MarketplaceHub/Core/Models.cs`, `MarketplaceHub/Services/MarketplaceGateway.cs`, `MarketplaceHub/UI/MainForm.cs`; suite Contracts và toàn bộ suites.
Interfaces: trường tiền `FinanceSnapshot` nullable decimal; trả count excluded; giới hạn đọc 1–45 ngày từ 2025 đến hôm nay Moscow; chỉ report daily, RUB.

- [x] Thêm ca field thiếu, signed decimal, duplicate/currency/malformed, period ngoài phạm vi và report vượt kỳ.
- [x] Quan sát RED.
- [x] Validator chặt, không công bố trang chưa hoàn tất, không lấy missing thành 0; UI trình bày khoản báo cáo riêng, bỏ ròng tự tính.
- [x] Chạy cả 8 suites Windows (222/222) và build portable (37092774111); rà soát độc lập diff cuối, lưu số ca/log/artifact trong audit.
- [x] Cập nhật preview 11263182438 và hướng dẫn kiểm tra API thật; không xuất bộ cài chính thức.
