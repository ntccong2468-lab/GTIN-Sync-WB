# Đối chiếu mã nguồn và các thiếu sót — 03/10/2026

## Nguồn và giới hạn

Đã tải 538 tệp văn bản của ứng dụng và ba kho tham chiếu; kiểm tra cấu trúc module, contract API, các luồng liên quan và bộ kiểm thử. Không coi việc tải toàn bộ cây thư mục là đã đọc từng dòng của mọi tệp. Không thực thi mã tham chiếu hoặc bản cài không rõ nguồn.

| Kho | Snapshot đã khảo sát | Phần áp dụng |
|---|---|---|
| `rupphi/sellerpro-backend` | `4dba3341761cbb160c0d4c4b9dd80364864a117a` | Đọc có kiểm soát, phân biệt thiếu dữ liệu và số 0, số tiền decimal, kỳ báo cáo, tránh cộng phí hai lần |
| `rupphi/sellerpro-frontend` | `8c9e8e37f50a79824f664fc28e59c39707b6e1e5` | Giữ trạng thái tải/lỗi rõ ràng; báo cáo hiển thị đúng giới hạn dữ liệu |
| `rupphi/jeditor` | `35dddcacf06eb4d0332a8d6727ae84ec8903581a` | Nguyên tắc không chặn UI, xuất tệp an toàn, không để request cũ ghi đè trạng thái mới |
| `rupphi/relatest-wcode` | Release chính thức `v1.1.67` | Barcode WB riêng với GTIN đăng ký; size WB theo seller, size Ozon theo nhà sản xuất; phục hồi mã đã cấp từ buffer hết hạn |
| `rupphi/test-wcode` | Nhánh `znack-registration-test`, pom `1.1.32` | Contract thuộc tính Ozon, phiếu nhặt, nhãn chính thức và API khôi phục block SUZ |
| Ứng dụng đang sửa | `79de195ada1108bcf2837023ee683f77fddfa3d9` | Windows/.NET 8, SQLite, WB/Ozon/Yandex |

Link backend người dùng gửi hai lần là cùng một kho. `relatest-wcode` chỉ công khai README và release; README dẫn đến `source-wcode`, hiện chưa có quyền đọc nguồn mới nhất. Không gọi mã `test-wcode` 1.1.32 là nguồn 1.1.67. Release fixture CI 1.1.901 không phải bản cập nhật chính thức. Đã khôi phục và xem ba ảnh Đơn mới / Shipment / Chi tiết shipment người dùng gửi.

Sellerpro là hệ thống giá, bán hàng và quyết toán WB/Ozon trên NestJS/Next.js; không có bộ xử lý FBS/KIZ thay thế cho WCode. Jeditor là IDE Java/WebView, không có tích hợp marketplace. Không đưa hệ thống tài khoản cloud, quản trị hoặc IDE của các kho này vào ứng dụng Windows.

## Lỗi xác nhận trong luồng đã được duyệt

| Vấn đề | Nguyên nhân trong ứng dụng | Thay đổi cần kiểm chứng |
|---|---|---|
| Retry tải KIZ có thể mua thêm | Chỉ tái sử dụng external order khi stage `POLLING`; lỗi lại lưu `ERROR` và có nhánh xóa external ID | Giữ checkpoint, tiếp tục order đã biết, chặn mua trùng khi kết quả chưa xác định |
| Buffer SUZ hết hạn | Chỉ polling hoặc đọc buffer; chưa đọc các block đã cấp | Đọc danh sách block và retry từng block, nhập idempotent, giữ chủ sở hữu KIZ đã gán |
| KIZ/GTIN sai có thể vào pool | Chuẩn hóa bằng xóa mọi ký tự không phải số, không checksum; mã tải về chưa kiểm tra GTIN | Kiểm tra GTIN hợp lệ và mã GS1 trước khi nhập; chỉ trả mã AVAILABLE chưa có chủ |
| Barcode WB bị thay bằng GTIN mapping | Builder dùng GTIN đã map làm barcode nhãn sản phẩm | Giữ barcode đúng variant WB; GTIN dùng độc lập cho KIZ |
| Size Ozon trống/sai | Chỉ gọi product/info/list, không lấy thuộc tính có tên của category/type | Đọc attributes và định nghĩa tên; ưu tiên size nhà sản xuất, kiểm tra đúng product/offer |
| Thiếu phiếu nhặt Ozon/Yandex | Nhánh export chỉ xuất PDF chính thức và KIZ; bỏ các dòng không yêu cầu KIZ khỏi dữ liệu in | Phiếu A4 tổng hợp mọi biến thể và số lượng, kể cả sản phẩm không KIZ |
| Báo cáo có thể hiển thị số 0/ròng sai | Thiếu field trở thành 0; không xác minh cấu trúc/tiền tệ/kỳ; UI tiếp tục trừ phí khỏi khoản forPay | Hiển thị thiếu dữ liệu là chưa có; xác minh báo cáo; không tự nhận khoản forPay là lợi nhuận ròng |
| Bảng FBS không theo kích thước cửa sổ | Handler resize của tabs ghi đè handler của bảng con | Giữ cả hai handler và kiểm tra resize thật |

Các luồng đã có được giữ và chạy lại kiểm thử: nhận đơn vào shipment đúng ngày, loại đơn đã nhận khỏi Đơn mới, packing trước khi gán KIZ/in, nhật ký khôi phục WB/Ozon/Yandex, polling nhãn Ozon cùng job, chặn Retry-After, mapping 50 dòng, toàn bộ size WB khi ghi GTIN, license form nhanh và online trước khi lưu, bảng kiểm tra API thật có che khóa.

## Kiểm chứng

Đợt này bắt đầu bằng các ca tái hiện mới trên Windows. Chưa coi các sửa đổi mới là đạt kiểm thử trước khi có kết quả CI. Mốc trước: 196/196 ca trên Windows ở commit `92220145cae66d79242e60dffb263d984a71c584`; không dùng kết quả đó để chứng minh bản sửa mới.

Không xóa cửa hàng, đơn, mapping, KIZ đã gán hoặc lịch sử in. Chỉ cập nhật nhánh `fix/dashboard-fbs-stability`, chưa merge nhánh chính. Bản cài chính thức vẫn chờ kiểm tra API thật; trước đó cung cấp bản portable để kiểm thử.

## Rà soát độc lập và lượt sửa bổ sung

Đã rà soát độc lập, không dùng API thật. Các ca RED tiếp tục xác nhận:

- Chuẩn bị đăng ký Znack dùng chung nhật ký mua và xóa external order: tách bảng `znak_registration_preparation` bổ sung, giữ nhật ký mua nguyên vẹn. Trạng thái chuẩn bị không có nghĩa đã đăng ký thật.
- Hai instance có thể mua đồng thời: claim bằng SQLite atomic statement và so sánh đúng generation `updated_at` đã đọc; đọc lại pool sau xác thực. Build từng bắt được race khi instance thứ hai xác thực sau khi instance thứ nhất đã hoàn tất; điều kiện generation sửa tình huống này.
- SUZ từ chối HTTP400: giữ lỗi xác định có thể sửa cấu hình và retry; timeout/5xx/missing order ID vẫn chặn mua lại.
- SUZ429: lưu deadline theo tài khoản OMS, chặn cả sau khởi động lại; đọc Retry-After ở auth, order, status, codes và phục hồi block.
- CryptoPro verbose: đọc cả stdout/stderr trước khi chờ exit; tránh pipe deadlock, vẫn kill khi hủy/timeout.
- Yandex hai item cùng offer: phiếu nhặt chiếu từng item ID/số lượng, không dùng Single theo SKU.
- Barcode WB nhiều lựa chọn: không chọn mã đầu tiên khi đơn không xác định được đúng barcode.
- Báo cáo dùng cùng ngày Moscow cho kỳ mặc định, nút nhanh và validator; trường quyết toán dài có vùng cuộn.
- Resize test dùng viewport thật, tránh giới hạn kích thước desktop của Windows runner; đã quan sát RED khi handler bảng bị thay.

Rulings: sửa trong phạm vi thiết kế và quyền push đã được xác nhận; giữ source WCode 1.1.67 chưa truy cập là giới hạn đối chiếu; chưa thực hiện đăng ký thẻ National Catalog mới hoặc thử API thật; giữ nhánh/PR hiện tại và không merge; chưa xây installer. Fixture WB retry được sửa checksum GTIN hợp lệ (04608888888886), giữ nguyên kiểm tra PUT bị mất response và đọc lại KIZ.

Bản 847abd7 có một runner đạt 222/222 nhưng build runner bắt được race nói trên. Chỉ dùng kết quả của commit cuối sau sửa generation để nghiệm thu.

## Kết quả cuối

Commit code được nghiệm thu: `09704bcac282d366b4342dc3c76ad17e4e6ac77d`.

| Suite Windows | Kết quả |
|---|---:|
| API Contracts | 41/41 |
| Ozon/Yandex FBS | 22/22 |
| ProductSync | 11/11 |
| FbsState / SUZ / SQLite | 44/44 |
| License | 14/14 |
| Mock API end-to-end | 35/35 |
| UI | 41/41 |
| Print/PDF | 14/14 |
| Tổng | 222/222 |

Build Windows `37092774111`, regression push `37092774168` và regression PR `37092778781` đều thành công. Build/publish self-contained win-x64 thành công, zero test failures; các bước installer đều skipped. Chưa thử API thật. Không gọi bộ kiểm thử bằng dữ liệu giả là kiểm chứng kết nối thật với sàn.

Preview: https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/37092774111/artifacts/11263182438

ZIP preview 75,808,110 byte; SHA-256 artifact archive `1be25d1e2749d1fab45342a2670c7cb747671c478959a7a354a250c037ad2437`.

Ảnh UI: artifact `11263237128`; mẫu PDF: artifact `11262926699` cùng run. Nhật ký RED cho các lỗi gốc: `37090824647`, `37091049526`; review RED: `37091917875`; viewport RED: `37092225070`.

Đã giải nén artifact cuối và xem ảnh màn hình Windows cùng PDF đã render: shipment WB có nút xuất xanh, KIZ Mapping tách Barcode sàn/GTIN và giữ phân trang, Báo cáo có vùng cuộn cho quyết toán. Phiếu nhặt Ozon A4 có hai dòng XL=5, XXL=1, tổng 6 sản phẩm của 2 đơn; mẫu WB giữ size seller 48. Không thấy chữ chồng hoặc bảng vượt trang ở những mẫu đã kiểm tra. Ảnh sản phẩm trống trong fixture không chứng minh ảnh SKU thật; máy in vật lý và độ giống WCode từng pixel chưa được kiểm chứng.

Cách thử thật: giải nén preview, mở `MarketplaceHub.exe`, chọn cửa hàng, mở Kiểm tra tích hợp (WB/National Catalog) hoặc kiểm tra Ozon chuyên sâu từ form cửa hàng. Nhập khóa ở ô che, chạy chỉ đọc trước, gửi báo cáo đã che. Sau xác minh, thử đúng một đơn/biến thể; cần cả kết quả đóng gói, readback KIZ và PDF chính thức. Không gửi API secret vào hội thoại. Đăng ký thẻ Znack mới chưa được triển khai như một remote mutation; trang chuẩn bị ghi rõ giới hạn này.

Trạng thái bàn giao: các sửa lỗi đã xác nhận và phần kiểm thử giả lập của kế hoạch đã hoàn tất; PR #2 được cập nhật, nhánh/workspace được giữ để tiếp tục sửa từ báo cáo API thật. Không merge hoặc tạo installer trong đợt này. Nhật ký thực thi được lưu tại `2026-10-03-reference-gap-execution-ledger.md` cùng thư mục.
