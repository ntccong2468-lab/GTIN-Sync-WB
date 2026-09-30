# Marketplace Hub 0.6.3

Bản sửa lỗi tập trung vào sao chép WB → Ozon/Yandex, ảnh sản phẩm và thao tác chọn đơn FBS.

## 0.6.3

- Thêm luồng **WB → Yandex Market**: dùng dữ liệu card WB đã đồng bộ, tự tìm danh mục lá Yandex, chuyển tên/mô tả/brand/barcode/giá/ảnh và tạo offer ở Business đích.
- Thêm luồng **WB → Ozon**: tự đọc cây danh mục + type Ozon, ánh xạ danh mục theo card WB, lấy danh sách thuộc tính bắt buộc và cố gắng ánh xạ từ characteristics WB trước khi gửi `/v3/product/import`.
- Copy khác sàn **không gọi lại API WB nguồn** nếu card đã nằm trong SQLite, vì vậy token WB nguồn không còn là điều kiện để copy card đã đồng bộ sang Ozon/Yandex.
- Lỗi 401/403 được đổi thành thông báo rõ cửa hàng nào thiếu/sai token/API key hoặc thiếu quyền quản lý sản phẩm.
- Yandex kiểm tra API chuyển sang `GET /v2/campaigns`.
- Ảnh WB hiện thường là **WebP**; UI chuyển sang bộ giải mã SkiaSharp nên ảnh WebP/JPG/PNG hiển thị được trong FBS, Supply, FBO, Znack, Thay đổi giá và Sao chép bài đăng.
- Khi gửi ảnh WB sang Ozon/Yandex, ứng dụng thử URL `.jpg` tương ứng nếu nguồn là `.webp`, rồi fallback sang link gốc.
- Màn **Đơn mới FBS** có checkbox **Chọn tất cả đơn mới đang hiển thị**.
- Giữ nguyên cơ chế tạo shipment, in nhãn WB, KIZ, đồng bộ Ozon/Yandex và các chức năng 0.6.2.

### Lưu ý khi copy WB → Ozon

Ozon bắt buộc category/type, dimensions và một số thuộc tính theo từng danh mục. Ứng dụng không tự bịa dữ liệu. Nếu WB thiếu một thuộc tính bắt buộc hoặc kích thước/khối lượng đóng gói, app sẽ nêu tên trường còn thiếu thay vì gửi dữ liệu sai sang Ozon.

## Bộ cài

`MarketplaceHub/dist/MarketplaceHub-Setup-0.6.3-win-x64.exe`
