# Tiến độ triển khai kế hoạch 2026-10-02-order-truth-fbo-kiz-gtin

Đặc tả và kế hoạch đã được người dùng xác nhận. Nhánh: `fix/dashboard-fbs-stability`, repository: `ntccong2468-lab/GTIN-Sync-WB`.

- Nhiệm vụ 1 đã hoàn tất: chuẩn hóa trạng thái, đếm external order duy nhất, loại membership và hủy khỏi Đơn mới. Windows CI xanh tại `5472ed3`, regression run `36936280076`.
- Nhiệm vụ 2 đã hoàn tất: kết quả theo từng đơn, selection hỗn hợp không chặn đơn hợp lệ, batch `100/100/5` cho 205 đơn. Windows CI xanh tại `acfe9b5`, regression run `36937180622`.
- Nhiệm vụ 3 hoàn tất: nhật ký nhận đơn, phục hồi và dialog kết quả. RED `36959052332`; Windows xác nhận 24/24 FbsState (kể cả phục hồi không POST/PATCH trùng) và test dialog xanh tại `74a5d46`. Suite UI còn test FBO đỏ của nhiệm vụ 4.
- Nhiệm vụ 4 hoàn tất: RED `74a5d46`, GREEN `ece45c9` trên Windows run `36960714781`: 13/13 Print, 31/31 UI, 24/24 FbsState. Nhãn chuẩn bị theo biến thể, số lượng, màu và KIZ; nhật ký FBO giữ mã khi lỗi.
- Nhiệm vụ 5 hoàn tất: RED `36960836857`; GREEN nhiệm vụ 5 tại `275a1f7`, 26/26 FbsState, 32/32 UI. Test mapping được cách ly khỏi mã KIZ của scenario quyền sở hữu phía sau.
- Nhiệm vụ 6 hoàn tất: GREEN `52e57ab` run `36962953337`: 9/9 ProductSync, 26/26 MockApi, 26/26 FbsState, 13/13 Print, 32/32 UI. Mock 51 card chứng minh batch 50/1, 429, restart, readback sai; NK 25 GTIN/request/checkpoint riêng và metadata được che credential.
- Nhiệm vụ 7 hoàn tất kiểm thử giả lập, đang bàn giao API thật: RED `36963378571`/`36963679346` cho Trung tâm kiểm tra, single-target writeback và membership không được xóa khi refresh thiếu. Có preview Windows portable riêng; installer bị tắt kể cả manual dispatch.

## Các quyết định thực thi

- Dùng bản sao làm việc chuyên dụng trên nhánh tính năng trong scratch. Không triển khai trên nhánh chính.
- Máy Linux hiện tại không có .NET SDK; thực thi kiểm thử trên Windows GitHub Actions. Không báo test xanh khi chỉ kiểm tra tĩnh.
- Quyền push lên repository/nhánh nêu trên đã được người dùng xác nhận rõ ràng. Chưa có quyền merge vào nhánh chính.
- Installer tiếp tục bị khóa cho đến khi mock và kiểm tra API thật hoàn tất. API key được nhập trong ứng dụng, không gửi trong hội thoại.
- Sau khi workspace tạm được khởi tạo lại, khôi phục từ GitHub `acfe9b5`; các test nhiệm vụ 3 chưa commit được dựng lại từ ngữ cảnh.
- Nhiệm vụ 4 bổ sung nhật ký FBO trong SQLite để phục hồi đúng biến thể/số lượng và KIZ đã giữ sau lỗi. Nhãn chuẩn bị có manifest `OfficialMarketplaceLabels=false`; không đóng giả sticker của sàn.
- Nhiệm vụ 5: kiểm thử SQLite dùng suite FbsState (tham chiếu app thật) thay vì ProductSync portable chỉ link gateway; giữ ProductSync cho contract/checksum API. Quyết định tránh đưa WinForms/DPAPI vào suite portable.
- Ảnh tham chiếu 041438/062902/062912 chưa khôi phục được từ ngữ cảnh/file; không tuyên bố độ giống hình ảnh 100%. Tiếp tục theo cấu trúc và hành vi WCode đã xác minh bằng nguồn công khai.
- WCode công khai: `rupphi/relatest-wcode` phát hành 1.1.66; `test-wcode` chỉ có nguồn 1.1.32, `source-wcode` trả 404. Không gán nguồn cũ là nguồn 1.1.66. Pipeline dùng contract trong spec cùng tài liệu WB/National Catalog hiện tại.
- WB chỉ thêm GTIN vào skus, giữ barcode hiện có và đầy đủ sizes; đọc card mới trước mọi retry rồi đọc lại từng size sau ghi. Metadata National Catalog chỉ lưu các trường cần thiết đã che secret, không lưu response thô.
- TestCenter chỉ mở ghi sau read-only, đúng một mục tiêu và checkbox xác nhận; credential thay đổi làm mất xác minh. Nhận WB chỉ nhận đơn đã chọn, rồi seller vào shipment để KIZ/in; probe GTIN từ chối tiếp tục một job khác đang pending.
- Nhiệm vụ 7: suite dùng SQLite fixture riêng trong thư mục temp, không dùng database cửa hàng của người chạy test. Membership WB chỉ bổ sung/đối soát, không xóa thành viên đã xác minh từ phản hồi thiếu.

## Quy tắc phục hồi nhận đơn

- Trước POST/PATCH lưu ý định và mã thao tác. Supply ID được lưu ngay khi server trả về, membership được lưu sau từng lần readback.
- Nếu kết quả tạo shipment chưa rõ, chỉ nhận diện lại bằng tên chứa mã thao tác duy nhất. Không tìm thấy hoặc thấy nhiều kết quả thì giữ trạng thái cần đối soát và chặn POST mới.
- Nếu đã giữ Supply ID, tiếp tục đúng supply sau khi đọc lại trạng thái/membership; shipment cũ hoặc đã đóng không nhận thêm đơn.

## Rà soát cuối nhánh và lượt sửa

Reviewer độc lập đã đọc `2eb7dfb..821084a`; không có Critical, có 8 Important. Test tái hiện ở remote `e2a058a`, RED đã xác nhận trên Windows regression `36965664915`/`36965670520`: cache New, mất journal, thiếu exact-target, mất deadline, NK quên mismatch, thiếu proof và packaging, FBO override. UI compile đã sửa.

1. Important được xác nhận: helper UI không tồn tại làm suite không compile. Đổi sang handler async sẵn có trong test.
2. Important được xác nhận: đồng bộ bỏ qua đơn active đã biến mất khỏi queue; đối soát cache theo endpoint từng sàn và đánh dấu chưa đủ dữ liệu trước mọi lượt refresh.
3. Important được xác nhận: lỗi GET supply cũ có thể đóng journal. Giữ Supply ID trước request, chỉ terminal khi có bằng chứng trạng thái thật.
4. Important được xác nhận: probe một đơn có thể mở rộng journal nhiều đơn. Thêm boundary exact-target bên trong semaphore nhận đơn.
5. Important được xác nhận: NK Success toàn job không chứng minh mục tiêu đã chọn; lỗi size batch trước bị quên sau restart. Kiểm tra proof chính xác và tính kết quả từ trạng thái đã lưu.
6. Important được xác nhận: NK thiếu kiểm tra cấp đóng gói/technical. Chỉ chấp nhận identifier trade-unit/multiplier 1 và explicit is_tech_gtin=false.
7. Important được xác nhận: checkbox FBO có thể tắt KIZ bắt buộc. Khóa ô đã biết bắt buộc và bảo vệ cả boundary export.
8. Important được xác nhận: Retry-After nhận WB chỉ ở memory. Migration bổ sung deadline/endpoint vào journal; restart không gọi lại trước deadline.

Ruling: Chỉ một lượt sửa tập hợp sau review, mỗi lỗi có RED→GREEN và suite Windows đầy đủ — theo executing-plans — không gọi reviewer mới để tạo vòng lặp.
Ruling: Bulk WB ghi thật tiếp tục chưa có UI cho đến khi thử một biến thể bằng API thật — ranh giới milestone đã duyệt — chi phí là seller chưa chạy bulk trong bản preview.
Minor về thời điểm dữ liệu: hiển thị timestamp quan sát đã xác minh cùng nhãn một phần; không dùng chữ “Hiện tại” đơn độc cho cache.

- Lượt sửa đang chờ GREEN: đối soát active cache cả WB/Ozon/Yandex; quan sát từ xa bắt buộc cho projection, không suy diễn cache là current. Fixtures UI dùng quan sát explicit.
- Deadline nhận WB lưu additive `retry_at`/`retry_endpoint`; khi quota xảy ra không thực hiện các readback tiếp theo. Test quota reset nhịp memory sau scenario để tránh ảnh hưởng scenario kế tiếp.
- Proof National Catalog revision 2 giữ cấp trade-unit, multiplier 1 và nontechnical explicit. Kết quả resumed job được tính trên proof đã lưu cho toàn snapshot, không dựa counter trong memory.
- Test Center kiểm tra proof đúng store/scope/SKU/variant/GTIN/size ở cả mở gate và trước mutation; journal nhiều đơn bị chặn bên trong semaphore dù seller chỉ chọn một đơn.

- GREEN phần logic tại remote `83decc0` regression `36966242058`: 31/31 FbsState, 33/33 MockApi, 13/13 Print; 33/34 UI. UI còn assertion cũ `requests==1` không còn đúng khi cần đọc chi tiết đơn biến mất.
Ruling: Cập nhật test khóa sync để trả chi tiết thật và yêu cầu đúng hai request orders (queue + reconcile), đồng thời full sync vẫn bị chặn — đây là điều chỉnh fixture theo hành vi mới đã duyệt, không bỏ kiểm tra khóa.
- Bổ sung positive control: probe WB hợp lệ phải nhận đúng một đơn; proof NK đúng target vẫn mở gate dù target khác mismatch, credential khác phải làm proof mất hiệu lực.

- 8 finding Important đã GREEN đầy đủ tại remote `fa4a166`, Windows build `36966594616`; positive controls đạt 32/32 FbsState và 35/35 MockApi.
- QA ảnh còn hai lỗi thuộc mục tiêu giao diện/số liệu: toolbar KIZ Mapping bị khuất ở viewport 1044 px; biểu đồ ngày vẫn đếm hai SKU thành hai đơn. Test RED `b2db32b`, regression `36966954613` xác nhận cả hai assertion thất bại. Sửa responsive toolbar thành hai hàng ở cửa sổ nhỏ và distinct external order theo ngày; đang đợi GREEN cuối.

## Xác minh cuối và ranh giới API thật

Remote code `92220145cae66d79242e60dffb263d984a71c584` (local `fc27d54`) đã GREEN trên Windows regression `36967354996`:

| Suite | Kết quả |
|---|---:|
| Contracts | 37/37 |
| Fbs | 22/22 |
| ProductSync | 9/9 |
| FbsState | 32/32 |
| License | 14/14 |
| MockApi | 35/35 |
| UI | 34/34 |
| Print | 13/13 |
| Tổng | 196/196 |

Đã đọc ảnh cuối Báo cáo và KIZ Mapping: biểu đồ đếm 1 external order có 2 SKU thành 1; toolbar kiểm tra Znack nằm trọn trong viewport 1044 px và chỉ lưới có thanh cuộn ngang. Đã xem ảnh FBO, Test Center và shipment WB; ô nhập credential được che và report echo đã được redaction.

Ruling: Giữ nhánh `fix/dashboard-fbs-stability` cùng PR hiện có, không merge nhánh chính — tiếp tục quyền push đã xác nhận và ranh giới API thật — workspace quản lý bởi host được giữ lại theo finishing-a-development-branch.

Gói bàn giao là Windows portable của build `36967354995`, không phải installer. Bước tiếp theo cần seller nhập API thật trực tiếp trong Kiểm tra tích hợp, chạy chỉ đọc và gửi báo cáo đã che; chỉ thử ghi đúng một biến thể/đơn sau xác minh và checkbox xác nhận. Chưa chạy API thật, chưa tạo bộ cài.

Windows build `36967354995` đã thành công: 196/196 test, zero failure trong log, publish self-contained win-x64 và upload preview 75.792.073 byte. Artifact `11209962932`: https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/36967354995/artifacts/11209962932 . Tất cả bước installer đều skipped. PR regression `36967360641` cũng success.

Bàn giao: giải nén ZIP, mở MarketplaceHub.exe → chọn cửa hàng → KIZ Mapping → Kiểm tra / đồng bộ Znack (hoặc Kiểm tra tích hợp ở menu). Seller nhập token WB và API key National Catalog trong ô che, chạy Kiểm tra chỉ đọc, gửi báo cáo đã che. Đây là ranh giới cần dữ liệu thật, không yêu cầu dán secret vào chat. Nhiệm vụ 7 hoàn tất phần đã có thể kiểm chứng bằng mock; kiểm tra API thật và installer còn chờ seller.

## Mốc mới thay thế preview ngày 02/10 — 03/10/2026

Đối chiếu thêm các kho người dùng gửi và sửa các thiếu sót theo kế hoạch `2026-10-03-reference-gap-fixes.md`. Ba ảnh tham chiếu đã được khôi phục và xem. Release WCode chính thức mới được xác minh là 1.1.67; source công khai tham khảo vẫn là 1.1.32, chưa truy cập được source mới nhất.

Code cuối `09704bcac282d366b4342dc3c76ad17e4e6ac77d` đạt **222/222** trên Windows: Contracts 41, FBS 22, ProductSync 11, FbsState 44, License 14, MockApi 35, UI 41, Print 14. Build `37092774111` và hai regression `37092774168`/`37092778781` đều success. Đã xem ảnh UI và render phiếu nhặt PDF từ artifact cuối.

Preview hiện hành: https://github.com/ntccong2468-lab/GTIN-Sync-WB/actions/runs/37092774111/artifacts/11263182438 . Đây là ZIP portable, 75.808.110 byte, SHA-256 `1be25d1e2749d1fab45342a2670c7cb747671c478959a7a354a250c037ad2437`; mọi bước installer đều skipped. Không dùng preview/installer cũ làm bản nghiệm thu đợt này.

Chi tiết lỗi, bằng chứng RED→GREEN, giới hạn nguồn và hướng dẫn API thật ở `docs/reviews/2026-10-03-reference-gap-audit.md`. Các lỗi xác nhận trong phạm vi đã sửa; đăng ký thẻ Znack mới vẫn chỉ là chuẩn bị cục bộ và không hiển thị đã xuất bản. Tiếp tục giữ PR #2 mở, không merge. Nhập API thật trong form che khóa và gửi báo cáo đã che trước mốc xây installer.
