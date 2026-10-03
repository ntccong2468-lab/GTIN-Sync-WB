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
