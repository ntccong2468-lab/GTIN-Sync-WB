# MASTER PROMPT — MARKETPLACE HUB WINDOWS

Bạn là senior Windows/.NET engineer và marketplace integration architect. Phát triển scaffold hiện tại thành **Marketplace Hub** production-ready cho Windows 10/11 x64, không website, self-contained installer.

## Rules
1. Dùng .NET 8 desktop; giữ WinForms hoặc migrate WPF có test.
2. Chỉ dùng API chính thức hiện hành của Wildberries, Ozon, Yandex Market, Честный ЗНАК. Trước khi code endpoint, xác minh docs mới nhất và tạo docs/API-MATRIX.md ghi endpoint, quyền, rate limit, async behavior, ngày kiểm tra.
3. Không decompile/copy WCode; chỉ tham khảo UX workflow.
4. Không hard-code secret. Marketplace tokens/API keys dùng Windows DPAPI CurrentUser. Không log Authorization/API-Key.
5. УКЭП private key/PIN không lưu DB/file; SignatureProvider gọi Windows Certificate Store/CryptoPro/Rutoken/VipNet.
6. Mọi write có preview + explicit confirmation, trừ operation được seller whitelist. HTTP 2xx không phải final success nếu API async. Phải poll/read-back verify.
7. Timeout sau write => UNKNOWN; verify trước retry.
8. Tests bắt buộc cho mapper, pricing, KIZ state, FBS state, retry/rate limit, serialization, DPAPI.

## Stores/API
Multi-store không giới hạn.
WB: capability cho Content, Prices/Discounts, FBS.
Ozon: Client ID + API Key.
Yandex: auth hiện hành + Business ID + Campaign IDs.
Honest Sign: organization INN, environment, certificate thumbprint/provider.
Nút Test connection chỉ test auth/capability, không gọi là sync.

## Products
Normalized catalog + raw snapshot cho debug. Adapter API -> normalized model. UI chỉ dùng normalized model.

## Prices
Hỗ trợ percentage +/-; fixed RUB delta; fixed price; expression; rounding 1/10/50/100; Excel import; destination pricing rule; batch preview; write; verify; rollback.
Yandex phân biệt business-level/campaign-level. WB xử lý task/status/quarantine theo docs. Ozon adapter độc lập để schema đổi không phá UI.

## Copy
Hỗ trợ WB->WB, Ozon->Ozon, Yandex->Yandex và mọi cặp cross-marketplace.
Pipeline: fetch source -> normalize -> category match -> attribute map -> required-field validation -> media -> destination create/update -> price -> verify.
Không copy marketplace IDs, reviews, ratings, sales history, warehouse/order IDs. Barcode/GTIN validate theo destination.
Mapping category/attribute phải cache/versioned và cho manual override.
Preview từng field: copyable / transformed / missing-required / prohibited. Missing required => block publish.

## FBS Packing Station
Một UI cho ba sàn:
queue; scanner autofocus; parser nhận product barcode/order/DataMatrix; count; wrong-item alarm; order claim; KIZ requirement; box split; confirm/ship; label; printer; shipment/supply/act; final QR; audit timeline.
WB: assembly/supply/sticker/metadata/DataMatrix/supply QR theo docs hiện hành.
Ozon: awaiting_packaging -> marking/exemplar -> validation -> ship -> label -> carriage/act.
Yandex: assembly/boxes/marking -> labels -> READY_TO_SHIP -> shipment.
Xây FbsDomainState + translator riêng từng marketplace, không ép raw states giống nhau.

## Честный ЗНАК / KIZ / УКЭП
Pages: connection/certificate, KIZ check, bulk check, pool, documents, signature jobs, history.
Auth challenge-sign theo True API docs hiện hành; TokenManager refresh trước expiry.
SignatureProvider liệt kê cert, kiểm private key, expiry/chain/INN, CryptoPro/Rutoken; ký đúng format API.
AutoSignPolicy: MANUAL(default), SEMI_AUTO, WHITELISTED_AUTO. Destructive/correction operations không auto-sign nếu chưa whitelist.
KIZ states: AVAILABLE, RESERVED, ASSIGNED, SHIPPED, INVALID, RETIRED. Reserve transaction-safe; attach fail => conditional rollback. Validate KIZ GTIN match order item.

## Job Engine
Persisted Job + JobItem:
QUEUED, VALIDATING, SENDING, MARKETPLACE_PROCESSING, VERIFYING, SUCCESS, PARTIAL_SUCCESS, FAILED, UNKNOWN.
Survive restart; retry failed only; Retry-After; marketplace rate limiter; progress%; user error + expandable technical detail.

## Database
SQLite + EF Core or Dapper, versioned migrations.
Tables tối thiểu:
shops; shop_credentials_metadata; products; product_variants; product_media; marketplace_product_refs; category_mappings; attribute_mappings; price_snapshots; jobs; job_items; copy_jobs; fbs_orders; fbs_items; fbs_boxes; fbs_box_items; labels; shipments; packing_stations; znak_accounts; znak_codes; kiz_pool; kiz_assignments; signature_jobs; audit_events; api_call_logs.
No plaintext secrets.

## Devices
HID scanner; configurable Enter/Tab suffix; printer profiles; raw ZPL; PDF/PNG fallback; 58x40 preset; optional COM scale; test print.

## UI
Professional compact left navigation, light/dark, no overlap, 100/125/150% DPI.
Pages exactly: Tổng quan; Sản phẩm; Giá sản phẩm; Sao chép sản phẩm; Đơn hàng FBS; Честный ЗНАК/KIZ; Jobs; Lịch sử; Cửa hàng/API; Thiết bị; Cài đặt.
Vietnamese UI first; raw marketplace statuses may show Russian/English in details.

## CI / Installer
Automated unit + mock HTTP integration tests, no seller secrets.
Windows CI: restore/build/test -> publish win-x64 self-contained -> launch smoke -> Inno Setup -> silent install -> launch installed exe -> SHA-256 -> artifact.
Installer: MarketplaceHub-Setup-{version}-win-x64.exe. Preserve DB/config on upgrade. Never claim verified unless install+launch smoke passes.

## Implementation order
Phase 1 persistence/DPAPI/job engine/store profiles + real read-only product sync.
Phase 2 Prices preview/write/verify/rollback.
Phase 3 same-market copy, then cross-market mapper.
Phase 4 FBS read queue/scanner/labels, then write packing/ship.
Phase 5 Honest Sign cert health/auth/check, then document signing.
Phase 6 KIZ pool + full FBS/KIZ integration.
Phase 7 hardening, rate limits, recovery, DPI, installer tests.

Definition of Done per feature: current official endpoint documented; adapter; tests; rate/error handling; preview/verify; audit; no secret leak; CI green. Không thay real integration bằng fake success. Demo mode phải đánh dấu rõ.
