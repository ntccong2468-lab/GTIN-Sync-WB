# Đặc tả đợt 1: tác vụ xuất nhãn FBS và mua KIZ có phục hồi

Ngày: 02/10/2026, Europe/Moscow.
Repository: `ntccong2468-lab/GTIN-Sync-WB`.
Base: `fix/dashboard-fbs-stability`, commit `79de195ada1108bcf2837023ee683f77fddfa3d9`.
Trạng thái: đề xuất để người dùng duyệt; chưa phải xác nhận đã triển khai hoặc đã chạy API thật.

## 1. Mục tiêu và phạm vi

Người dùng đã đồng ý tiếp tục sửa RU Seller Hub theo báo cáo repo mã nguồn mở. Đợt đầu tập trung vào một subsystem: điều phối luồng FBS/KIZ và xuất nhãn cho Wildberries, Ozon, Yandex trong app Windows hiện có.

Kết quả cần có: seller bắt đầu từ shipment/batch đã nhận, bấm “Xuất nhãn”, app tiếp tục đúng tác vụ, dùng lại mã đã giữ, bổ sung KIZ khi được cấu hình cho phép, xác minh mã trên sàn và tạo file nhãn. Bấm lại, chuyển trang, lỗi mạng hoặc khởi động lại không tạo đơn mua mới để thay thế một kết quả chưa rõ.

Nhãn vận chuyển của sàn, nhãn Data Matrix và xác nhận tem đã dán lên hàng là ba kết quả khác nhau. App không báo đã in/dán chỉ vì tạo được PDF.

Phạm vi gồm job bền vững, purchase intent SUZ, phục hồi order/block, điều kiện cấp phát KIZ, policy request, UI tiến độ và test. Các module tồn kho tổng thể, finance, funnel, copy listing, AI và updater thuộc đợt sau. Không thay toàn bộ gateway hoặc toàn bộ scheduler trong đợt này.

## 2. Bối cảnh đã xác minh

- App là WinForms `net8.0-windows`; dùng SQLite, DPAPI, SkiaSharp, ZXing và CryptoPro signing hiện có.
- Đã có journal WB receive, reservation WB/non-WB, marketplace packing, exemplar Ozon và label task Ozon. Giữ chúng làm nguồn dữ liệu nghiệp vụ.
- `EnsureKizQuantityAsync` tìm mã AVAILABLE theo GTIN trong pool chung; resume order chỉ khi stage POLLING.
- Lỗi poll/download có thể đổi stage thành ERROR; catch cuối còn ghi external order ID rỗng. Lần sau có thể tạo một order mua khác.
- POST thành công nhưng thiếu/malformed order ID không luôn được phân loại kết quả chưa rõ.
- Pipeline cơ sở unique theo store/SKU, chưa đủ để phân biệt nhiều ý định mua cùng SKU.
- Mã vừa tải được gán AVAILABLE; chưa có gate rõ về legal status, pháp nhân, môi trường và hàng vật lý.
- Source công khai WCode là mẫu tham khảo hành vi, không phải thư viện được đưa nguyên vào app.

Bằng chứng: `MarketplaceHub/Services/AppServices.cs`, `AppServices.WbShipment.cs`, `AppServices.MarketplaceFbs.cs`, `Infrastructure/AppDatabase.cs` tại base nêu trên; hai báo cáo nghiên cứu WCode/FBS và báo cáo 16 repo ngày 02/10/2026.

## 3. Lựa chọn thiết kế

| Phương án | Đánh đổi | Quyết định |
|---|---|---|
| Lớp điều phối C#/SQLite bao quanh các luồng hiện có | Cần thêm model/migration, nhưng giữ được reservation và contract đang dùng | Chọn |
| Chỉ vá stage ERROR trong helper mua | Giảm lỗi trước mắt, chưa giải quyết identity nhiều job, scope và phục hồi GET codes | Không đủ cho đợt này |
| Chuyển sang Node/Go SDK hoặc backend mới | Thêm runtime, IPC, triển khai và quản lý secret | Hoãn |

Tận dụng nguyên tắc retry/transport từ seller-sdk, schema Yandex chính thức và fixture/audit Ozon. Đợt này không bắt buộc thêm Polly, Quartz, SDK Node/Go, PDFsharp hoặc framework AI. ZXing đã có, không thêm lại.

## 4. Ràng buộc chung

- App Windows độc lập; giữ `net8.0-windows` trong đợt này.
- Migrations chỉ bổ sung bảng/cột/index; không xóa hoặc tái cấp mã đã giữ.
- Quyền/license, scope cửa hàng và ranh giới Test Center hiện có tiếp tục được kiểm tra tại service boundary.
- Token, API key, omsConnection, private key và raw KIZ không xuất hiện trong log/báo cáo; raw code trong các bảng mới được DPAPI bảo vệ. Không đưa secret vào snapshot/hash đầu vào.
- Store deletion xóa dữ liệu thuộc store, dừng tác vụ thuộc store và không cho worker tái tạo store. Không gọi API hủy đơn mua từ xa chỉ vì xóa store.
- Remote ID, request key, payload hash và snapshot đã gửi là bất biến sau khi bắt đầu mutation.
- Không coi HTTP 2xx là bằng chứng hoàn tất tác vụ.
- Không tự nhận thêm đơn mới, tạo shipment WB mới hoặc giao shipment WB khi bấm Xuất nhãn.
- Installer vẫn bị khóa đến khi hoàn tất regression và vòng kiểm tra API thật đã thống nhất; bản thử là portable.

## 5. Thành phần và trách nhiệm

| Thành phần | Trách nhiệm |
|---|---|
| FbsLabelJobCoordinator | Nhận lệnh xuất/tiếp tục, lập snapshot, gọi adapter, lưu stage và kết quả từng unit |
| KizPurchaseCoordinator | Tính thiếu, giữ purchase intent, tạo/poll/phục hồi cùng order SUZ |
| KizEligibilityService | Kiểm tra GTIN/unit, owner, môi trường, legal state và bằng chứng hàng vật lý |
| OperationPolicy | Phân loại request theo tác động, timeout, quota và readback; không suy từ GET/POST |
| Adapter FBS từng sàn | Dùng methods/journals hiện có; giữ supply/posting/box/exemplar semantics riêng |
| SuzGateway và SigningProvider | Auth, ký payload, order/status/block recovery; host/profile theo cấu hình |
| SQLite repository | Transaction, unique key, checkpoint, claim worker, evidence và projection UI |
| UI tiến độ | Hiển thị kết quả, chờ/đối soát/tiếp tục và in lại; không quyết định khả năng mutation |

Các phần này nằm trong partial/file có trách nhiệm riêng, không tăng thêm nghiệp vụ vào MainForm.cs hoặc AppServices.cs nguyên khối. Đường dẫn và chữ ký cụ thể sẽ được chốt trong implementation plan sau khi đặc tả được duyệt.

## 6. Identity, dữ liệu và cạnh tranh

### 6.1. Job xuất nhãn

Một tác vụ có store, marketplace, target kind (WB supply hoặc batch non-WB), target ID, revision, snapshot hash, operation ID và generation của store.

Snapshot gồm các external order/item/unit ID, GTIN xác minh, quantity, mapping version và requirement fingerprint. Không bao gồm trạng thái biến động không liên quan. Một target chỉ có một job active; bấm lại trả về job đó. Reprint tham chiếu revision đã hoàn tất và file nhãn cũ.

Khi thành viên/quantity/GTIN thay đổi, job dừng ở SnapshotChanged. Không sửa snapshot đã có mutation. UI cho seller tạo revision cho phần nhu cầu mới sau khi đối soát unit/reservation của revision trước; unit đã có mã giữ binding cũ.

### 6.2. Purchase intent

Mỗi purchase intent có intent ID, job/revision, GTIN, số lượng thiếu cố định, profile ID/version, pháp nhân, environment, local request UUID, payload hash, remote order ID, stage, block IDs, retry-at và error code.

Unique intent theo job/revision + profile + environment + GTIN. UUID ổn định chỉ chống lặp trong app; không giả định SUZ hỗ trợ header idempotency.

Một khóa điều phối theo pháp nhân/environment/GTIN bảo vệ tính nhu cầu và tạo intent. Order mua còn chưa rõ sẽ chặn mua mới cho cùng scope/GTIN đến khi đối soát. Các job khác vẫn được đọc/poll hoặc dùng mã đã hợp lệ.

Không cộng “mã đang chờ cấp” thành mã AVAILABLE. Không tự đặt order thứ hai cho intent đã tải thiếu mã; tiếp tục order/block gốc hoặc giữ NeedsReconciliation.

### 6.3. Unit và scope KIZ

Mỗi unit có identity store + marketplace + external order + item + unit index và ánh xạ mã canonical. Ràng buộc uniqueness giữ code không thuộc hai unit active, kể cả scanner prefix/alias.

Scope pool mới gồm store, pháp nhân và environment. Mặc định không dùng chéo store; chia sẻ kho giữa store cùng pháp nhân chưa thuộc đợt này.

Tách hai trạng thái: allocation (available/reserved/assigned/quarantined) và legal (unknown/emitted/applied/in-circulation/retired/rejected). AVAILABLE cũ không được tự chuyển thành in-circulation.

Reservation/purchase/block binding và cập nhật stage phải dùng transaction. Worker claim chỉ cho một executor active trong một job. Mở app thứ hai dùng cùng DB bị từ chối bằng khóa instance của database; crash giải phóng khóa, startup chuyển operation đang gửi sang đối soát trước khi chạy tiếp.

Credential/config change tạo version mới. Job đã gửi tiếp tục với cùng owner/environment; mismatch chặn mutation cho đến khi chọn lại cấu hình tương ứng.

## 7. Luồng Xuất nhãn

1. Kiểm tra license/store hiện tại, target membership và dữ liệu remote đủ; khóa job và lập/đọc snapshot.
2. Đọc trạng thái sàn, requirements và mã đã có. Mã trên sàn không tự được coi là legal/đúng owner.
3. Giữ nguyên reservation và binding đã xác minh. Tính nhu cầu còn thiếu theo từng unit, trừ phần mã hợp lệ có thể cấp phát trong đúng scope.
4. Không cần KIZ thì đi thẳng qua bước kiểm tra nhãn, không gọi SUZ.
5. Có KIZ nhưng thiếu mã hợp lệ: chỉ tạo purchase intent khi auto-purchase bật và profile/credential/capability đã hợp lệ. Nếu chưa đủ điều kiện, trả NeedsAction với lý do, không POST.
6. Tạo/poll/phục hồi order và block theo mục 8. Mã tải về đi qua legal/physical gate ở mục 9.
7. Cấp phát mã transactionally; gọi adapter gắn và đối soát đúng mọi unit. Partial result giữ nguyên kết quả đã xác minh.
8. Thực hiện điều kiện đóng gói/nhãn theo contract riêng từng sàn; đọc lại sau mutation.
9. Lấy nhãn chính thức, kiểm tra response file, lưu hash/provenance và manifest đủ unit. Chỉ đủ bằng chứng thì LabelsReady.
10. UI cho mở/lưu/in file. Yêu cầu in lại dùng binding/file cũ; file mất/hỏng thì lấy lại nhãn qua adapter, không mua KIZ mới.

### Khác biệt từng sàn

| Sàn | Điều kiện và tác động |
|---|---|
| WB | Supply đã nhận và membership xác minh; gắn metadata KIZ; lấy sticker. Xuất nhãn không gọi deliver supply |
| Ozon | Posting/unit/exemplar đúng, không có rejection; giữ job exemplar/label task cũ. Nếu cần xác nhận packing để lấy nhãn, dùng command đóng gói đã có journal và đọc lại |
| Yandex | Giữ mọi item/unit/box và layout; đối soát identifiers. Nếu contract yêu cầu chuyển trạng thái packing để lấy nhãn, dùng command hiện có và đọc lại |
| Tất cả | Chưa rõ kết quả mutation thì không gửi lại mù quáng; không coi local batch ID là shipment ID của sàn |

UI phải nêu tác vụ sẽ chuẩn bị KIZ/đóng gói trên sàn nếu cần, trước khi seller bắt đầu. Xác nhận packing qua API không được báo là đã bàn giao hàng vật lý cho vận chuyển.

## 8. Phục hồi SUZ và policy request

| Tình huống | Hành vi bắt buộc |
|---|---|
| Chưa gửi POST | Intent Draft/Validated có thể tiến hành lần đầu |
| Bắt đầu POST | Lưu CreateSending trước mạng |
| Có order ID hợp lệ | Lưu ngay, chuyển Polling; lỗi sau đó không xóa ID |
| Timeout/5xx/cancel sau bắt đầu gửi, 2xx thiếu ID hoặc JSON lỗi | CreateUnknown; không tạo request UUID/order mới |
| Không biết đã gửi chưa sau crash | Đối soát; không tự suy diễn chưa mua |
| Đối soát trả đúng một order với bằng chứng phù hợp | Gắn remote ID cũ và tiếp tục |
| Không tìm được hoặc có nhiều order giống nhau | NeedsReconciliation; không chọn order chỉ vì cùng GTIN/số lượng |
| Poll timeout/quota | Giữ order ID, checkpoint và retry-at; tiếp tục cùng order |
| GET codes mất response | DownloadUnknown; liệt kê/phục hồi block đã cấp trước khi nhận thêm mã |
| Partial download | Lưu block và code hợp lệ ngay; deduplicate theo canonical identity; không tự mua bù |
| Order đóng/không phục hồi được block | NeedsReconciliation; giữ provenance order/block |
| Remote rejection có bằng chứng | Rejected; hiển thị lý do; tạo ý định mua mới cần quyết định mới của seller |

Binding order thủ công chỉ nằm trong màn đối soát: đọc chi tiết remote, đối chiếu owner/environment/GTIN/quantity/profile, hiển thị bằng chứng cho seller xác nhận; ghi audit. Không coi các trường này là đủ để tự động nhận diện một order giữa nhiều kết quả giống nhau.

OperationPolicy phân biệt SafeRead, StatefulRead, Mutation và Reconcile. GET nhận mã SUZ là StatefulRead. Retry-After dạng giây/ngày được lưu bằng UTC deadline; không gọi lại trước deadline sau restart. Nếu không có Retry-After, SafeRead dùng tối đa 3 attempts với exponential delay+jitter; mutation và StatefulRead chỉ một attempt rồi đối soát. Giá trị timeout của từng endpoint lấy từ operation catalog trong plan, không áp một timeout chung cho mọi bước.

Bấm Dừng hoặc đóng cửa sổ tiến độ chỉ dừng worker; không hủy order SUZ từ xa, không trả reservation chưa đối soát về pool. Chuyển trang không hủy job; UI unsubscribe, worker tiếp tục và kết quả không thay nội dung trang mới.

## 9. Profile, legal gate và tem vật lý

Profile phải do seller chọn theo pháp nhân, environment, product group và loại nghiệp vụ. Không mặc định mọi seller là nhà sản xuất, không hardcode OWN_PRODUCTION/PRODUCTION cho hàng nhập khẩu/mua lại.

Đợt này xây gate và cấu hình capability. Chỉ bật auto-purchase cho profile có payload/contract và kiểm thử phù hợp; profile chưa hỗ trợ hiển thị rõ “Chưa hỗ trợ tự động”, không gửi payload gần đúng. Không xây mọi loại tài liệu pháp lý nhập khẩu/sản xuất trong một đợt.

Một mã chỉ có thể cấp cho unit để hoàn tất nhãn khi:
- canonical code/GTIN checksum và GTIN exact variant đúng;
- scope owner/environment/store được xác minh;
- legal status đủ điều kiện theo contract đã kiểm tra, có thời điểm/source proof;
- mã chưa bị unit khác giữ;
- physical unit đã có tem tương ứng hoặc đang chờ seller thực hiện bước dán được hiển thị rõ.

Mã vừa tải không thành AvailableToAllocate chỉ vì HTTP GET thành công. Nếu thiếu bước đưa vào lưu thông hoặc capability xác minh legal status, job ở AwaitingLegalState và không báo nhãn giao hàng sẵn sàng.

Seller có thể xuất nhãn Data Matrix cho mã của mình ở bước AwaitingPhysicalMark, thực hiện in/dán rồi xác nhận unit. Checkbox này chỉ ghi bằng chứng thao tác vật lý do seller xác nhận, không thay thế legal proof. Không cấp mã mới cho hàng đã có tem. Nếu chưa biết mã trên hàng, yêu cầu scan/map trước.

Giữ mã cho job để in/dán là reservation chuẩn bị, chưa phải cấp phát sẵn sàng giao hàng. Profile có thể yêu cầu AwaitingPhysicalMark trước AwaitingLegalState; coordinator theo prerequisites của profile thay vì ép một thứ tự chung. Không tự gửi tài liệu đưa vào lưu thông khi chưa có implementation/contract phù hợp; job chờ seller hoàn tất bước đó rồi đọc lại bằng chứng.

Legacy pool/reservations giữ nguyên dữ liệu. Mã chưa có scope/legal proof ở NeedsVerification. Legacy pipeline còn remote ID được nhập thành intent phục hồi; ERROR có ID không tạo lại order. ERROR không ID nhưng không có bằng chứng chưa gửi ở NeedsReconciliation.

## 10. UI và trạng thái

UI dùng panel/dialog tác vụ hiện có: target, store/sàn, bước hiện tại, số unit xác minh/tổng, lỗi từng đơn, lần cập nhật và hành động phù hợp.

Trạng thái chung: Queued, Validating, AwaitingPurchase, Purchasing, AwaitingCodes, Recovering, AwaitingLegalState, AwaitingPhysicalMark, Attaching, Verifying, LabelsReady, Partial, Paused, NeedsReconciliation, SnapshotChanged, Failed, Cancelled.

Sub-state remote giữ riêng trong adapter/journal; trạng thái common chỉ để điều phối và hiển thị.

- “Tiếp tục” cùng job; “In lại” cùng revision đã hoàn tất.
- Partial không hiển thị toàn bộ xanh; unit không đủ bằng chứng không nằm trong manifest nhãn sẵn sàng.
- Không nhảy 100% khi chỉ POST/PUT thành công. Hiển thị số unit và stage; xanh khi LabelsReady.
- Safe auto-resume chỉ đọc/poll/phục hồi đã biết target. Những mutation chưa rõ luôn qua đối soát.
- Background IO không chạy chặn UI; sync 60 giây hiện có tiếp tục với quota/cancellation riêng.
- Danh sách job hiện trong phạm vi shipment/batch; chưa xây một Job Center cho mọi module.

## 11. Kiểm thử và nghiệm thu

TDD cho logic/persistence mới, dùng fixture isolated và fake HTTP handler hiện có; chưa bắt buộc WireMock.Net. Regression Windows chạy qua GitHub Actions như dự án hiện tại.

| Ca nghiệm thu | Kết quả cần đạt |
|---|---|
| Đủ mã legal/physical | Không POST SUZ; gắn đúng mã và nhãn |
| Quantity 0 hoặc âm | 0 không mua; âm từ chối input |
| Hai lần bấm/two tasks đồng thời | Cùng job/intent, một POST mua |
| Hai store cùng GTIN | Không dùng chéo pool/scope; unknown purchase chặn mua mới đúng scope |
| Crash sau POST trước persist response | Unknown; không POST lần hai |
| 2xx thiếu ID/JSON lỗi | CreateUnknown, giữ payload/request key |
| Lỗi poll sau có order ID | ID giữ nguyên; restart tiếp tục poll |
| Mất response GET codes | Khôi phục block; không tạo order mới |
| Tải thiếu/trùng mã | Lưu mã đúng, không cấp trùng hoặc mua bù tự động |
| Quota rồi restart | Không request trước retry-at; checkpoint không đổi |
| Wrong GTIN/owner/environment/legal state | Chặn cấp phát và báo lý do |
| Đã tải nhưng chưa legal/physical | Chờ đúng stage; không báo LabelsReady |
| Mã đã có trên hàng | Giữ code cũ; không cấp mã thứ hai |
| Partial attach/response mất | Reservation giữ nguyên, remote readback quyết định |
| Requirements/items đổi | SnapshotChanged; không ship/in snapshot cũ |
| Legacy ERROR có ID | Resume intent cũ, không tạo lại |
| Legacy không rõ ID | NeedsReconciliation |
| Credential đổi/store xóa | Chặn stale worker; không tái tạo dữ liệu store |
| App thứ hai/restart | Khóa cùng DB; recovery trước mutation |
| Reprint/file mất | Lấy lại nhãn, không mua/giữ mã mới |
| Chuyển trang/Dừng | UI không đơ, không lạc trang, giữ journal |
| Pack/label từng sàn | Endpoint, body và readback riêng; WB không deliver |
| Log/diagnostic | Không lộ token/omsConnection/raw KIZ |

Mốc hoàn thành code: regression liên quan và full suite Windows qua trên commit mới, thêm test nêu trên, đọc ảnh UI cuối, ghi rõ những gì chỉ kiểm tra bằng fixture. Không tái dùng kết quả test của commit cũ để báo đợt này xanh.

Vòng API thật sau mock: seller nhập credential trực tiếp trong Test Center; chạy read-only trước, chọn đúng một mục tiêu và xem preview tác động. Mua SUZ là thao tác có chi phí; chỉ mở probe mua sau xác nhận mục tiêu, số lượng/profile và hành động mua trên máy seller. Không yêu cầu gửi secret qua chat.

## 12. Ranh giới và bước tiếp theo

Đặc tả này chuyển báo cáo nghiên cứu thành phạm vi code cụ thể. Sau người dùng duyệt, viết implementation plan với file/signature/migration, fixture RED→GREEN và các lệnh Windows CI. Phương thức thực thi native trong phiên hiện tại đã được ghi trong kế hoạch cũ; giữ phương thức này, không yêu cầu chọn lại.

Hạng mục ngoài đợt 1 được giữ trong báo cáo hợp nhất: kho seller/kho sàn, finance Ozon/Yandex, funnel, copy listing các hướng còn lại, pricing và AI. Chúng có spec/plan riêng sau nền FBS/KIZ.

## Nguồn

- [Source app tại base](https://github.com/ntccong2468-lab/GTIN-Sync-WB/tree/79de195ada1108bcf2837023ee683f77fddfa3d9).
- [Tiến độ và ranh giới API thật hiện có](https://github.com/ntccong2468-lab/GTIN-Sync-WB/blob/79de195ada1108bcf2837023ee683f77fddfa3d9/docs/superpowers/plans/2026-10-02-execution-status.md).
- [Yandex OpenAPI chính thức](https://github.com/yandex-market/yandex-market-partner-api).
- [seller-sdk, transport/retry tham khảo](https://github.com/dev-ik/seller-sdk).
- [Ozon contract/test tham khảo](https://github.com/ucoms-dev/ozon-api-client).
- WCode_Mua_Gan_KIZ_Khi_Xuat_Nhan_2026-10-02.md và RU_Seller_Hub_Repo_Nguon_Mo_Va_Ke_Hoach_Hop_Nhat_2026-10-02.md: hai báo cáo nguồn đã bàn giao trong cuộc hội thoại, không được xem là bằng chứng API production đã chạy.
