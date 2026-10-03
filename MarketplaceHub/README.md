# Marketplace Hub 0.7.3

Bản 0.7.0 tái cấu trúc giao diện theo dashboard sáng xanh: sidebar xanh, nền mint, card trắng bo tròn và bố cục báo cáo trực quan.

## Kiến trúc được đối chiếu từ các repository công khai của rupphi

- **WB**: tách product / order / supply sync, cửa sổ 30 ngày, status batch, supply workflow và sticker 58×40.
- **Ozon**: catalog/posting sync tách riêng; FBS dùng posting_number, exemplar KIZ phải create/get → validate → set → status trước ship; nhãn là PDF chính thức của Ozon.
- **Độ ổn định UI**: các tác vụ mạng phải rời UI thread, không chạy đồng bộ chồng nhau, không giữ event handler cũ sau khi chuyển trang.
- **Finance**: dashboard đọc từ read-model riêng, không làm chặn luồng FBS.

## Thay đổi chính 0.7.0

- Gộp **Tổng quan + Tài chính** thành **Báo cáo**.
- Báo cáo có KPI, biểu đồ đơn 7 ngày, trạng thái tài chính và lịch sử các luồng sync.
- Thêm **Đồng bộ dữ liệu** chung cho Wildberries, Ozon và Yandex Market.
- Một cửa hàng chỉ có một sync chạy tại một thời điểm; phần ghi SQLite lớn chạy ngoài UI thread.
- FBS dùng chung một màn cho ba sàn.
- **Wildberries**: tạo supply → add order → SGTIN/KIZ → sticker PNG 58×40.
- **Ozon**: nếu posting cần KIZ, chạy exemplar create/get → validate → set → status rồi mới ship; nhãn là PDF chính thức Ozon.
- **Yandex Market**: gửi box layout + CIS/KIZ, chờ identifiers/status OK rồi mới chuyển READY_TO_SHIP; nhãn A9_HORIZONTALLY 58×40 PDF.
- Màn FBS hiển thị rõ loại nhãn của từng sàn và có **Tự động KIZ + in KIZ**.
- In KIZ tạo DataMatrix 58×40 từ mã đã gán.
- Sửa hiện tượng app đơ khi chuyển trang nhiều lần: hủy image request của trang cũ, bỏ Resize handler cũ, giới hạn cache ảnh và chặn sync trùng.
- Ảnh WebP/JPG/PNG vẫn dùng SkiaSharp.
- Thêm logo Marketplace Hub mới và icon cho EXE/installer.

## Bộ cài

`MarketplaceHub-Setup-0.7.3-win-x64.exe`

## Sửa sticker và quy trình in WB 0.7.2

- WB kiểm tra trạng thái thật `confirm` / `complete` trước khi lấy nhãn; đơn mới, hủy hoặc không rõ trạng thái không được in.
- Lấy sticker theo lô tối đa 100 ID số nguyên, ghép theo `orderId`. 119 đơn dùng hai request sticker; kiểm tra trạng thái và KIZ là các batch riêng. Không còn gọi sticker 119 lần.
- Giãn nhịp 250 ms; HTTP 429 chờ theo `X-Ratelimit-Retry` / `Retry-After`, tối đa ba lần thử lại. Có tiến độ và dừng tác vụ. Lỗi API dừng các lô còn lại.
- PNG chính thức được ưu tiên và kiểm tra trước khi in. Khi WB trả `barcode` + `partA` + `partB` nhưng thiếu file, dựng QR theo dữ liệu WB như wcode; không tạo sticker từ ID đơn. Thiếu cả dữ liệu chính thức thì báo hướng xử lý metadata/quyền API.
- In lại trong 10 phút dùng lại dữ liệu sticker theo đúng cửa hàng/token, vẫn đọc trạng thái mới để chặn đơn đã hủy.
- Một bộ gồm PDF nhãn 58×40 mm, phiếu nhặt A4 gộp đúng article/barcode/màu/size và manifest từng đơn. Có thứ tự sticker trước/sau, số bản nhãn sản phẩm/KIZ, xem trước, chọn máy in và lịch sử PDF.
- KIZ in từ metadata hiện hành trên WB, giữ GS/FNC1; KIZ thiếu/bị từ chối/đang kiểm tra chặn chuẩn bị khi chọn in kèm KIZ. Không mua, gán hoặc đổi KIZ trong tác vụ in.
- Barcode và size lấy đúng biến thể `chrtId`/SKU của đơn; catalog mâu thuẫn phải đồng bộ lại, không dùng size đầu tiên.
- Chuẩn bị hoàn tất toàn bộ PDF trước khi gửi một job tới máy in. Một spool copy, khổ 58×40, không để mặc định landscape/copies của driver đổi bộ nhãn. Lỗi lưu lịch sử sau khi gửi không báo nhầm thành chưa in.

Đối chiếu chi tiết: [wb-print-comparison.md](wb-print-comparison.md).


## Sửa lỗi 0.7.1

- Bố cục Báo cáo co giãn: ở cửa sổ hẹp, tài chính xuống dưới biểu đồ; bộ lọc ngày không chồng nút đồng bộ. KPI vận hành ghi rõ trạng thái hiện tại, biểu đồ theo khoảng ngày đã chọn.
- Bảng FBS dùng nền chọn mint, cột sản phẩm luôn còn chỗ đọc, thanh thao tác tự xuống dòng. Tên nhãn trong chi tiết theo đúng WB / Ozon / Yandex.
- Tự đồng bộ cửa hàng bật mỗi 60 giây. Các luồng đồng bộ cùng cửa hàng dùng chung khóa, gồm cập nhật đơn riêng và đồng bộ toàn bộ.
- Khi đổi trang, các điều khiển và ảnh cũ được giải phóng; kết quả chậm không thay trang mới. Tải/giải mã ảnh tối đa bốn luồng; ảnh trong bảng giới hạn 160 px, cache dữ liệu ảnh tối đa 64 MiB.
- Không ghi đơn WB thành “mới” khi API trạng thái lỗi; chỉ coi Ozon ship thành công sau khi đọc lại trạng thái.
- KIZ Ozon phải được xác nhận cho từng product_id / exemplar_id đã gửi, đủ mọi đơn vị; trạng thái thiếu, chờ hay từ chối đều chặn đóng đơn.
- Với đơn có nhiều dòng, chọn một dòng vẫn kiểm tra toàn bộ sản phẩm và số KIZ bắt buộc trước khi đóng posting.
- Đơn Ozon `awaiting_deliver` và Yandex `PROCESSING/READY_TO_SHIP` ở nhóm Đang đóng gói, để chọn in nhãn ngay sau khi đóng đơn; đang vận chuyển mới ở nhóm Đang giao.
- Nhãn tải về phải là PDF/PNG thật trước khi chuyển sang in.
- Nhãn KIZ dùng GS1 DataMatrix với FNC1, giữ nguyên AI và ký tự GS; kiểm thử giải mã lại ảnh sinh ra trước khi in.

## Kiểm tra và giới hạn

Các dự án `tests/MarketplaceHub.Contracts`, `tests/MarketplaceHub.Fbs`, `tests/MarketplaceHub.ProductSync`, `tests/MarketplaceHub.FbsState`, `tests/MarketplaceHub.Print` và `tests/MarketplaceHub.UI` dùng dữ liệu giả lập, không gọi tài khoản bán hàng. CI Windows chạy tất cả trước khi đóng bộ cài; EXE đã publish và EXE sau cài đặt đều phải qua self-test và khởi động giao diện.

Báo cáo quyết toán trực tiếp hiện hỗ trợ WB. Ozon / Yandex hiển thị dữ liệu sản phẩm, đơn hàng và lịch sử đồng bộ; chưa có adapter quyết toán cho hai sàn này.

WB in bộ trang trực tiếp qua máy in được chọn; PDF giữ để xem trước/in lại. Ozon/Yandex vẫn dùng lệnh Print của ứng dụng PDF mặc định trên Windows. Kiểm thử CI không xác nhận bản in vật lý hay quyền/token API thật của người bán. KIZ bắt buộc cần GTIN hợp lệ và mã sẵn có, hoặc cấu hình SUZ / chứng thư CryptoPro để mua mã.

## Shipment WB và tự gắn KIZ 0.7.3

Chọn tất cả đơn đang lọc ở tab Đơn mới sẽ hiện hai nút: Tạo shipment mới và Thêm shipment hôm nay. Dialog chỉ đưa shipment đang mở của cửa hàng này, cùng ngày Moscow. Đóng đơn hoặc xuất nhãn từ đơn mới đều đi qua dialog này; không lấy sticker khi đơn còn new.

Một lựa chọn119 đơn tạo một shipment, thêm theo lô100+19. App đọc lại membership và trạng thái confirm trước KIZ. Thất bại một phần vẫn giữ supplyId để mở/tiếp tục từ Shipment hôm nay; không tự tạo lại lô. Sau khi thêm đơn, app dùng GTIN của đúng size/chrtId/SKU, giữ riêng KIZ theo cửa hàng/đơn trước khi gửi và đọc lại metadata trước khi báo xanh. Mất kết nối sau PUT sẽ giữ mã cũ để đối soát trên lần thử tiếp theo. Nhập lại file KIZ không biến mã RESERVED/ASSIGNED thành mã sẵn sàng.

Trang Supply hiển thị mọi đơn thực có trong lô, ảnh, thông tin sản phẩm, KIZ đã xác nhận, Xuất nhãn dán và nút giao hàng riêng. Export tạo PDF/phiếu A4 rồi mở xem trước; chỉ gửi máy in khi seller nhấp In. Lô không đủ dữ liệu đơn/KIZ sẽ dừng và báo cụ thể. Các bảng SQLite bổ sung giữ membership và reservation; không xóa dữ liệu cũ khi nâng cấp.

Kiểm thử bằng API fixture và WindowsCI; không dùng token seller thật hoặc máy in vật lý.

## Nhận đơn Ozon / Yandex và đồng bộ sản phẩm 0.7.3

Chọn đơn mới → Nhận và đóng gói → tạo lượt mới hoặc thêm vào lượt hôm nay. Lượt này nhóm các posting/order đã có trên sàn; không tạo Supply WB cho Ozon/Yandex. App đọc lại toàn bộ hàng, kiểm tra trạng thái/hủy và barcode đúng SKU, giữ một KIZ cho mỗi đơn vị bắt buộc, gửi đầy đủ phân bổ rồi chờ sàn xác nhận đóng trước khi xuất PDF. Ozon cần cả `ship_available` và trạng thái chấp nhận của từng exemplar; mã đầy đủ gồm dấu GS và đuôi mật mã được giữ nguyên.

Yandex giữ mọi item và số lượng với `allowRemove=false`. Khi sàn chỉ trả mã hộp mà chưa trả phân bố hàng, seller phải xác nhận layout hộp thực tế; ứng dụng lưu layout cho lần thử tiếp theo. Ozon multibox hoặc tích hợp giao hàng ngoài Ozon được báo rõ để xử lý trên seller portal, tránh tự gộp sai thành một gói.

Mất phản hồi khi gửi KIZ vẫn giữ mã cho từng đơn vị. Lệnh ship/READY_TO_SHIP được ghi trước khi gửi; khi kết quả chưa rõ, tạo lượt mới cũng không gửi lại lệnh đó. Ở tab Đang đóng gói/Đang giao, nút **Lượt đóng hàng** mở lượt đã lưu, kể cả lượt cũ chưa hoàn tất; không thêm đơn mới vào lượt khác ngày Moscow.

Nhãn sàn giữ PDF chính thức riêng Ozon/Yandex; KIZ được xuất thành PDF 58×40, một trang mỗi đơn vị. Tự xuất lượt mới bỏ qua đơn đã có nhãn; nút Xuất nhãn cho phép xuất/in lại có chủ ý. Việc in dùng trình xem PDF trên Windows.

Trang **Đồng bộ sản phẩm** hỗ trợ cả ba sàn: chạy/tiếp tục/tạm dừng, checkpoint theo cửa hàng/tài khoản, nhịp gọi và retry quota trên đúng cursor, tìm kiếm/lọc thiếu GTIN, 50 biến thể mỗi trang. Catalog lưu tất cả size/barcode của WB, ảnh thuộc đúng SKU, mã offer Yandex ổn định khi marketSku thay đổi và giữ giá cũ nếu API chưa trả giá mới. Trang cuối không được xác nhận hoàn tất khi còn thiếu sản phẩm theo total; lỗi không xóa catalog hay mapping đã lưu. Barcode sai checksum hoặc nhiều GTIN chưa phân biệt được sẽ chặn cấp KIZ để seller đối soát.
