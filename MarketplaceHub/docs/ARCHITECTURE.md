# Marketplace Hub — kiến trúc hợp nhất

## WCode
WCode được xác định là Java/JavaFX đóng gói jpackage/WiX, có FBSBarcode, SQLite JDBC, OkHttp, Gson, Apache POI/PDFBox và barcode. Ý tưởng nên giữ: desktop standalone, local state, scanner-first FBS và printer/label workflow. Không sao chép/decompile logic độc quyền.

## Modules
Tổng quan; Sản phẩm; Giá; Copy; FBS; Честный ЗНАК/KIZ; Jobs; Lịch sử; Stores/API; Thiết bị; Cài đặt.

## Core
UI -> Application Services -> Domain -> Adapters -> official APIs.
Adapters riêng: Wildberries, Ozon, Yandex, HonestSign, SignatureProvider.

Normalized Product: title, description, brand, vendor code, barcode/GTIN, canonical category, attributes, dimensions, media, variants. Không để raw API models đi thẳng UI.

## Price Engine
+/- %, delta RUB, fixed price, formula, rounding, Excel, destination rule, preview, verify, rollback.
State: QUEUED -> VALIDATING -> SENDING -> MARKETPLACE_PROCESSING -> VERIFYING -> SUCCESS/PARTIAL_SUCCESS/FAILED/UNKNOWN.
HTTP 2xx không mặc định business success.

## Copy Engine
source -> normalize -> category map -> attribute map -> validate required -> media -> destination create/update -> price -> verify.
Không copy marketplace IDs, rating/reviews/history, warehouse/order IDs. Preview field green/yellow/red; required missing thì cấm publish.

## FBS
Một packing station chung nhưng state translation riêng từng sàn.
scan product/order/DataMatrix -> resolve -> count -> KIZ verify -> box -> confirm/ship -> label -> printer -> shipment/supply -> final QR/act.
Order claim theo workstation; timeout write => UNKNOWN + read-back trước retry.

## Честный ЗНАК
True API challenge signing bằng УКЭП theo tài liệu hiện hành. Không lưu private key/PIN. DB chỉ certificate thumbprint/metadata + auto-sign policy.
KIZ: AVAILABLE -> RESERVED -> ASSIGNED -> SHIPPED; rollback có điều kiện. Check existence/status, GTIN, crypto khi hỗ trợ, owner, blocked/sold/retired.

## Persistence
Production SQLite + migrations. Bảng: shops, credential metadata, products, variants, media, marketplace refs, mappings, price snapshots, jobs/items, FBS orders/items/boxes/labels/shipments/stations, znak accounts/codes, kiz pool/assignments, signature jobs, audit events, api logs.
Secrets DPAPI CurrentUser.

## Reliability
CancellationToken; Retry-After; per-marketplace limiter; internal idempotency; correlation ID; structured logs; redaction; sync snapshot atomic; failed-only retry.

## Installer
.NET 8 win-x64 self-contained + Inno Setup. CI phải build, silent install, launch smoke test rồi mới coi installer verified.
