# Kế hoạch triển khai dữ liệu đơn chính xác, FBO, KIZ Mapping và đồng bộ GTIN

> **Dành cho tác nhân triển khai:** BẮT BUỘC dùng kỹ năng phụ `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực hiện lần lượt từng nhiệm vụ. Các bước sử dụng ô đánh dấu (`- [x]`) để theo dõi tiến độ.

**Mục tiêu:** Làm cho số liệu FBS và quy trình nhận đơn vào shipment WB phản ánh đúng trạng thái hiện tại trên sàn; sau đó bổ sung chuẩn bị FBO theo đúng biến thể và quy trình bền vững Честный ЗНАК → KIZ Mapping → cập nhật GTIN lên WB.

**Kiến trúc:** Tạo một lớp chuẩn hóa trạng thái đơn duy nhất để cả Báo cáo và trang FBS cùng sử dụng. Quy trình nhận đơn WB sẽ phân loại từng đơn, cho phép thành công một phần an toàn, xác minh lại từ máy chủ và trả kết quả riêng cho từng đơn. Bổ sung các repository SQLite độc lập, chỉ thêm mới, để lưu mapping theo đúng biến thể và checkpoint đồng bộ GTIN; giữ nguyên gateway marketplace, quy tắc sở hữu KIZ và luồng in riêng của từng sàn.

**Công nghệ:** .NET 8, WinForms, Microsoft.Data.Sqlite, các HttpClient gateway hiện có, SkiaSharp/ZXing và GitHub Actions trên Windows.

**Đặc tả:** `docs/superpowers/specs/2026-10-02-order-truth-fbo-kiz-gtin-design.md`

## Các ràng buộc chung

- Tách riêng ID shipment và state machine của WB, Ozon và Yandex; không dùng quy tắc WB cho hai sàn còn lại.
- Migration cơ sở dữ liệu chỉ được bổ sung; không xóa hoặc ghi đè cửa hàng, sản phẩm, đơn hàng, KIZ, mapping, lịch sử in hoặc nhật ký kiểm toán hiện có.
- Không commit, ghi log hoặc hiển thị API key/token không được che.
- Chỉ báo thao tác từ xa thành công sau khi đọc lại đúng endpoint và xác minh kết quả.
- Một đơn đã hủy hoặc không hợp lệ không được làm chặn các đơn hợp lệ khác trong cùng lượt chọn.
- Không giải phóng hoặc thay thế KIZ đã giữ khi kết quả thao tác từ xa chưa xác định.
- Tuân thủ `Retry-After`, lưu checkpoint của đúng endpoint và tiếp tục từ trang/batch đang dở.
- Không tạo bộ cài mới trước khi toàn bộ test mock đạt và hoàn tất kiểm tra bằng API thật.

## Trọng tâm rà soát

- Một external order có nhiều dòng SKU: chỉ đếm và phân loại một lần nhưng vẫn giữ đủ mọi dòng để đóng gói.
- WB không trả một đơn đã chọn trong payload trạng thái hoặc membership: chỉ từ chối đơn đó, không tự suy đoán thành công.
- Shipment đang mở được tạo hôm nay nhưng một đơn đã thuộc shipment khác: không di chuyển hoặc tạo membership trùng.
- Seller đổi GTIN của đúng biến thể trong lúc job Znack/WB đang chờ: giữ giá trị seller và chỉ vô hiệu hóa job cũ.
- Ứng dụng đóng sau khi gửi lệnh từ xa nhưng trước khi ghi thành công cục bộ: khi mở lại phải đọc lại trạng thái, không gửi trùng lệnh.

---

### Nhiệm vụ 1: Chuẩn hóa trạng thái đơn và số liệu FBS chính xác

**Tệp:**
- Tạo: `MarketplaceHub/Core/OrderTruth.cs`
- Tạo: `MarketplaceHub/Infrastructure/AppDatabase.OrderTruth.cs`
- Tạo: `MarketplaceHub/Services/OrderTruthService.cs`
- Sửa: `MarketplaceHub/Infrastructure/AppDatabase.cs`
- Sửa: `MarketplaceHub/UI/MainForm.cs`
- Sửa: `MarketplaceHub/UI/MainForm.FbsV2.cs`
- Kiểm thử: `tests/MarketplaceHub.FbsState/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.UI/Program.cs`

**Giao diện:**
- Tạo ra: `OrderTruthState`, `OrderTruthRow`, `OrderTruthSnapshot` và `OrderTruthService.Build(StoreProfile, IReadOnlyList<FbsOrderRow>, IReadOnlySet<string>, IReadOnlyDictionary<string, OrderRemoteState>)`.
- Tạo ra: `AppDatabase.UpsertOrderRemoteStates(...)`, `AppDatabase.OrderRemoteStates(long storeId)` và `AppDatabase.MarkOrderRemoteState(...)`.
- Được sử dụng bởi: Nhiệm vụ 2, Nhiệm vụ 3, trang Báo cáo và trang FBS.

- [x] **Bước 1: Viết các test lỗi về trạng thái và cách đếm**

Thêm các test: `Canonical truth counts one posting with several lines once`, `Shipment members are not new`, `Cancelled remote state is not new`, `Unknown current state is partial rather than authoritative` và `Ozon/Yandex batch membership uses the same projection without WB semantics`. Kiểm tra số đơn distinct và trạng thái chuẩn hóa chính xác.

- [x] **Bước 2: Chạy test tập trung và xác nhận trạng thái ĐỎ**

Chạy: `dotnet run --project tests/MarketplaceHub.FbsState -c Release` và `dotnet run --project tests/MarketplaceHub.UI -c Release`.

Kỳ vọng: các assertion mới thất bại vì chưa có model chuẩn hóa và nhãn độ mới dữ liệu.

- [x] **Bước 3: Cài đặt lớp chuẩn hóa thuần và bảng trạng thái từ xa chỉ-thêm-mới**

Định nghĩa `OrderTruthState { New, InShipment, Packing, Shipping, Completed, Cancelled, Unknown }`. Lưu cặp supplier/status mới nhất, cờ dữ liệu đầy đủ và thời điểm quan sát theo `(store_id, marketplace, external_order_id)`. Gom các dòng sản phẩm theo external order ID trước khi phân loại.

- [x] **Bước 4: Thay cách đếm trực tiếp bằng `IsNew` trên Báo cáo và lưới đơn**

Cho `ShowReport` và `BuildFbsNewOrders` cùng sử dụng một `OrderTruthSnapshot`; hiển thị số đơn `New` distinct và trạng thái dữ liệu mới/không đầy đủ. Giữ mọi dòng sản phẩm phía sau mỗi external order có thể chọn.

- [x] **Bước 5: Xác nhận trạng thái XANH và commit**

Chạy lại hai bộ test; toàn bộ test cũ và mới phải đạt. Commit: `fix: derive FBS counts from canonical order truth`.

---

### Nhiệm vụ 2: Phân loại nhận đơn WB và kết quả riêng từng đơn

**Tệp:**
- Sửa: `MarketplaceHub/Core/Models.cs`
- Sửa: `MarketplaceHub/Services/WbShipments.cs`
- Sửa: `MarketplaceHub/Services/AppServices.WbShipment.cs`
- Kiểm thử: `tests/MarketplaceHub.Contracts/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.FbsState/Program.cs`

**Giao diện:**
- Sử dụng: trạng thái từ xa của Nhiệm vụ 1 và membership bền vững từ `WbReceivedOrderIds`.
- Tạo ra: `WbReceiveDisposition`, `WbReceiveOrderResult`, `WbReceiveResult` và `MarketplaceGateway.ReceiveWbShipmentAsync(...)`.
- `WbReceiveResult` gồm `Success`, `SupplyId`, `Created`, `VerifiedMemberCount`, `Orders` và `Message`. `Success` có nghĩa mọi đơn hợp lệ hoặc đã là thành viên đều được xác minh, không có nghĩa đơn hủy đã được thêm.

- [x] **Bước 1: Viết contract test lỗi cho trạng thái hỗn hợp và chia batch**

Kiểm thử: `new/waiting` hợp lệ đi cùng `new/canceled_by_client`; tất cả đơn hủy thì không tạo shipment; `confirm` chỉ hợp lệ khi đã thuộc shipment đang chọn; đơn thuộc shipment khác bị từ chối; thiếu dòng trạng thái chỉ từ chối đúng đơn đó; 205 ID hợp lệ chỉ tạo một shipment và PATCH theo `100, 100, 5`.

- [x] **Bước 2: Chạy Contracts và xác nhận trạng thái ĐỎ**

Chạy: `dotnet run --project tests/MarketplaceHub.Contracts -c Release`.

Kỳ vọng: logic trả về sớm hiện tại từ chối toàn bộ lựa chọn hỗn hợp và chưa thể trả kết quả từng đơn.

- [x] **Bước 3: Cài đặt phân loại trước khi gửi lệnh thay đổi**

Phân loại từng ID distinct thành `EligibleNew`, `AlreadyMember`, `Cancelled` hoặc `Rejected`. Chỉ tạo shipment nếu có `EligibleNew` và seller chưa chọn shipment cũ. Không gửi ID đã hủy hoặc bị từ chối.

- [x] **Bước 4: Cài đặt batch có xác minh và an toàn khi thành công một phần**

PATCH tối đa 100 ID, đọc lại toàn bộ membership sau mỗi batch, giữ `SupplyId` và kết quả đã xác minh nếu batch sau lỗi; cuối cùng đọc lại trạng thái và membership. Không coi ID bị thiếu trong phản hồi là đã nhận.

- [x] **Bước 5: Lưu kết quả, xác nhận trạng thái XANH và commit**

`AppServices.ReceiveWbOrdersAsync` lưu trạng thái hiện tại cho đơn hủy/bị từ chối và membership cho đơn đã xác minh trong transaction cục bộ phù hợp. Chạy Contracts và FbsState. Commit: `fix: receive eligible WB orders without cancelled batch abort`.

---

### Nhiệm vụ 3: Chọn đơn mới, giao diện kết quả và phục hồi sau khi khởi động lại

**Tệp:**
- Tạo: `MarketplaceHub/UI/WbReceiveResultDialog.cs`
- Sửa: `MarketplaceHub/UI/MainForm.FbsV2.cs`
- Sửa: `MarketplaceHub/UI/MainForm.WbShipment.cs`
- Sửa: `MarketplaceHub/Infrastructure/AppDatabase.WbShipment.cs`
- Kiểm thử: `tests/MarketplaceHub.UI/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.FbsState/Program.cs`

**Giao diện:**
- Sử dụng: `WbReceiveResult` của Nhiệm vụ 2 và lớp chuẩn hóa của Nhiệm vụ 1.
- Tạo ra: dialog kết quả có một dòng cho mỗi external order và nhật ký nhận đơn bền vững theo khóa `(store_id, operation_id, order_id)`.

- [x] **Bước 1: Viết test lỗi cho UI và phục hồi**

Kiểm thử checkbox đầu bảng với dòng không hợp lệ, hiển thị kết quả hỗn hợp, đơn đã xác minh rời “Đơn mới”, đơn hủy không còn chọn được, shipment xuất hiện ở “Đang đóng gói”, và sau khi mất phản hồi/khởi động lại vẫn dùng lại shipment cũ.

- [x] **Bước 2: Chạy UI/FbsState và xác nhận trạng thái ĐỎ**

Kỳ vọng: chọn tất cả vẫn lấy mọi dòng cache, chưa có dialog theo từng đơn và chưa có cơ chế phục hồi nhật ký nhận đơn.

- [x] **Bước 3: Cài đặt điều kiện được chọn và dialog kết quả**

Chỉ dòng có trạng thái chuẩn hóa `New` mới có checkbox được bật. Hiển thị `Đã thêm`, `Đã có trong shipment`, `Khách đã hủy` hoặc lỗi chính xác; luôn giữ Supply ID để tiếp tục.

- [x] **Bước 4: Bổ sung nhật ký nhận đơn chỉ-thêm-mới và đọc lại khi phục hồi**

Trước POST/PATCH lưu ý định thao tác; sau mỗi lần đọc lại lưu trạng thái đã xác minh. Khi mở lại hoặc thử lại, đọc danh sách shipment/membership từ máy chủ trước mọi POST mới và tiếp tục shipment được giữ.

- [x] **Bước 5: Xác nhận trạng thái XANH và commit**

Chạy UI và FbsState. Commit: `feat: show recoverable WB receive outcomes`.

---

### Nhiệm vụ 4: Chuẩn bị FBO theo đúng biến thể

**Tệp:**
- Tạo: `MarketplaceHub/UI/MainForm.Fbo.cs`
- Sửa: `MarketplaceHub/UI/MainForm.cs`
- Sửa: `MarketplaceHub/Services/WbPrintBundleService.cs`
- Kiểm thử: `tests/MarketplaceHub.Print/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.UI/Program.cs`

**Giao diện:**
- Sử dụng: `AppDatabase.ProductVariants`, kho KIZ và các dịch vụ in riêng của từng sàn hiện có.
- Tạo ra: `FboPreparationRow` theo đúng biến thể và `BuildFboPreparationLabels(...)` cho số lượng đã chọn.

- [x] **Bước 1: Viết test lỗi cho biến thể, UI và in**

Kiểm thử hai size/màu cùng SKU vẫn tách riêng; số lượng không vượt quá KIZ sẵn sàng khi sản phẩm bắt buộc KIZ; chọn nhiều dòng vẫn giữ thứ tự; không dựng lại nhãn marketplace chính thức từ dữ liệu catalog.

- [x] **Bước 2: Chạy Print/UI và xác nhận trạng thái ĐỎ**

Kỳ vọng: trang hiện tại chỉ tạo một dòng sản phẩm và không có chọn, số lượng hoặc thao tác xuất nhãn.

- [x] **Bước 3: Chuyển trang FBO sang partial riêng và hiển thị đúng biến thể**

Hiển thị checkbox, ảnh, sản phẩm, màu, size, SKU, GTIN/barcode, số lượng và KIZ sẵn sàng. Dùng danh tính của product variant, không dùng barcode đầu tiên của sản phẩm.

- [x] **Bước 4: Kết nối xuất nhãn an toàn**

Chỉ tạo nhãn chuẩn bị sản phẩm/KIZ nếu chưa có nhãn chính thức từ marketplace; xác minh số lượng và quyền sở hữu KIZ trước khi xuất file.

- [x] **Bước 5: Xác nhận trạng thái XANH và commit**

Chạy Print/UI. Commit: `feat: prepare FBO labels by exact variant`.

---

### Nhiệm vụ 5: Repository KIZ Mapping bền vững và giao diện phân trang

**Tệp:**
- Tạo: `MarketplaceHub/Infrastructure/AppDatabase.GtinMapping.cs`
- Tạo: `MarketplaceHub/UI/MainForm.KizMapping.cs`
- Sửa: `MarketplaceHub/Infrastructure/AppDatabase.cs`
- Sửa: `MarketplaceHub/UI/MainForm.cs`
- Kiểm thử: `tests/MarketplaceHub.ProductSync/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.UI/Program.cs`

**Giao diện:**
- Tạo ra: `GtinMappingRow`, `GtinMappingPage`, `UpsertSellerGtinMapping`, `GetGtinMappingPage(store, offset, 50, query, filter)` và xóa mapping/job liên quan khi người dùng chủ động xóa cửa hàng.
- Được sử dụng bởi: Nhiệm vụ 6.

- [x] **Bước 1: Viết test lỗi cho repository**

Kiểm thử migration chỉ-thêm-mới, khóa theo đúng biến thể, GTIN do seller nhập có ưu tiên cao nhất, mapping đã xác nhận không mất khi lần đồng bộ sau lỗi, mapping đã xác nhận được xếp đầu, phân trang 50 dòng và cách ly theo cửa hàng.

- [x] **Bước 2: Chạy ProductSync và xác nhận trạng thái ĐỎ**

Kỳ vọng: chưa có các bảng mapping và truy vấn phân trang.

- [x] **Bước 3: Cài đặt bảng và phương thức repository**

Tạo bảng mapping, job/checkpoint GTIN và index cho `(store_id, marketplace, confirmed, gtin)` cùng khóa tra cứu đúng biến thể. Không thay đổi dữ liệu KIZ/sản phẩm hiện có.

- [x] **Bước 4: Cài đặt partial KIZ Mapping phân trang**

Hiển thị số rule mapping, tổng KIZ theo từng trạng thái, stage Znack, lỗi gần nhất và các thao tác sửa/đồng bộ/xuất/thêm/lưu trữ. Giữ nguyên trang và nội dung tìm kiếm khi làm mới.

- [x] **Bước 5: Xác nhận trạng thái XANH và commit**

Chạy ProductSync/UI. Commit: `feat: add durable paged KIZ mapping`.

---

### Nhiệm vụ 6: Đồng bộ Честный ЗНАК và cập nhật GTIN lên WB có xác minh

**Tệp:**
- Tạo: `MarketplaceHub/Services/GtinMappingSyncService.cs`
- Sửa: `MarketplaceHub/Services/MarketplaceProductSync.cs`
- Sửa: `MarketplaceHub/Services/AppServices.ProductSync.cs`
- Sửa: `MarketplaceHub/Infrastructure/AppDatabase.GtinMapping.cs`
- Kiểm thử: `tests/MarketplaceHub.ProductSync/Program.cs`
- Kiểm thử: `tests/MarketplaceHub.MockApi/Program.cs`

**Giao diện:**
- Sử dụng: repository mapping/job của Nhiệm vụ 5, xác thực Znack hiện có và catalog product variant.
- Tạo ra: `SyncZnackGtinAsync`, `QueueWbGtinWriteback`, `ResumeWbGtinWritebackAsync`. Checkpoint của từng endpoint lưu cursor/batch, thời gian thử lại và biến thể đã xác minh cuối cùng.

- [x] **Bước 1: Viết test lỗi cho pipeline**

Kiểm thử checksum không hợp lệ, barcode mơ hồ, ưu tiên GTIN do seller nhập, nhiều size của một `nmID`, tối đa 50 card/request, batch thứ hai lỗi nhưng batch đầu được giữ, tiếp tục đúng batch sau HTTP 429/`Retry-After`, và sai readback phải giữ trạng thái pending.

- [x] **Bước 2: Chạy ProductSync/MockApi và xác nhận trạng thái ĐỎ**

Kỳ vọng: chưa có coordinator hoàn chỉnh cho mapping và writeback.

- [x] **Bước 3: Cài đặt đồng bộ sản phẩm Znack và xác nhận đúng biến thể**

Chuẩn hóa GTIN-14, đọc danh tính/trạng thái đăng ký từ National Catalog, lưu metadata phản hồi đã được làm sạch và chỉ xác nhận đúng biến thể. Không tự động thay mapping do seller xác nhận.

- [x] **Bước 4: Cài đặt cập nhật WB theo 50 card và phục hồi checkpoint**

Gom theo `nmID`, dựng lại đầy đủ mảng sizes của từng card, gửi tối đa 50 card, đọc lại card, chỉ đánh dấu biến thể khớp là đã xác minh và lưu thời điểm thử lại khi 429 mà không quét lại batch đã hoàn tất.

- [x] **Bước 5: Xác nhận trạng thái XANH và commit**

Chạy ProductSync và MockApi. Commit: `feat: sync Znack GTIN and verify WB writeback`.

---

### Nhiệm vụ 7: Trung tâm kiểm tra, xác minh Windows đầy đủ và bàn giao sang API thật

**Tệp:**
- Tạo: `MarketplaceHub/UI/MainForm.IntegrationTestCenter.cs`
- Sửa: `MarketplaceHub/UI/MainForm.cs`
- Sửa: `tests/MarketplaceHub.MockApi/Program.cs`
- Sửa: `tests/MarketplaceHub.UI/Program.cs`
- Sửa: `.github/workflows/build-marketplace-hub.yml`
- Sửa: `.github/workflows/marketplace-regression.yml`

**Giao diện:**
- Sử dụng: toàn bộ nhiệm vụ trước.
- Tạo ra: giao diện kiểm tra WB/Znack chỉ đọc trên máy local và thao tác thay đổi riêng biệt, chỉ được bật sau khi chọn đúng một đơn/sản phẩm thử nghiệm và xác nhận rõ ràng.

- [x] **Bước 1: Viết test lỗi cho 205 đơn và an toàn của Trung tâm kiểm tra**

Xác minh ba batch WB, trạng thái hủy hỗn hợp, không lộ credential trong log/ảnh UI, mặc định chỉ đọc, nút thay đổi bị vô hiệu hóa khi chưa chọn mục tiêu và xác nhận, đồng thời không chạy bước tạo installer trong milestone này.

- [x] **Bước 2: Chạy toàn bộ contract suite và xác nhận trạng thái ĐỎ cho kiểm tra an toàn mới**

Chạy mọi executable `tests/MarketplaceHub.*` ở cấu hình Release trên Windows CI.

- [x] **Bước 3: Cài đặt Trung tâm kiểm tra local và cổng CI**

Dùng ô nhập đã che hoặc credential cửa hàng được bảo vệ bằng DPAPI. Hiển thị endpoint, trạng thái, thời gian và hướng xử lý đã làm sạch. Giữ điều kiện tạo installer ở trạng thái tắt trong milestone này.

- [x] **Bước 4: Chạy xác minh đầy đủ**

Các suite bắt buộc phải xanh: Contracts, Fbs, ProductSync, FbsState, License, MockApi, Print và UI. Kiểm tra ảnh Báo cáo/FBS/FBO/KIZ, bảo đảm không có control trùng hoặc bị cắt.

- [x] **Bước 5: Chỉ yêu cầu API thật sau khi có bằng chứng mock xanh**

Yêu cầu người dùng nhập credential WB và Честный ЗНАК trực tiếp trong Trung tâm kiểm tra; chạy kiểm tra chỉ đọc trước, sau đó mới yêu cầu một đơn/sản phẩm chuyên dùng để thử thao tác thay đổi. Không yêu cầu người dùng dán secret vào cuộc trò chuyện.

- [x] **Bước 6: Commit Trung tâm kiểm tra đã xác minh**

Commit: `test: add live-safe marketplace validation center`. Dừng trước bước tạo installer cho đến khi kết quả API thật được rà soát.

## Kết quả tự rà soát kế hoạch

- Phạm vi đặc tả: trạng thái đơn chuẩn hóa, nhận WB an toàn một phần, báo cáo chính xác, FBO theo biến thể, KIZ Mapping phân trang, pipeline Znack/WB GTIN, mock 100+/205 đơn và ranh giới API thật đều có nhiệm vụ chịu trách nhiệm.
- Tính nhất quán kiểu dữ liệu: Nhiệm vụ 2–3 cùng dùng `WbReceiveResult`; Nhiệm vụ 5–6 cùng dùng `GtinMappingRow` theo đúng biến thể; Báo cáo và lưới đơn cùng dùng `OrderTruthSnapshot` của Nhiệm vụ 1.
- Trọng tâm rà soát: cả năm tình huống rủi ro đều có test được chỉ định trong Nhiệm vụ 1, 2, 3 hoặc 6.
- Tỷ lệ nội dung: kế hoạch ghi rõ interface, test và lệnh thực thi nhưng không chép sẵn phần cài đặt production.
