# Ozon WCode-compatible workflow and live diagnostics

## Goal

Bring Marketplace Hub's Ozon FBS workflow in line with the safe parts of the latest public WCode implementation and the Ozon-relevant WCode 1.1.61 release behavior:

1. Discover new actionable postings from `POST /v4/posting/fbs/unfulfilled/list`.
2. Keep receipt separate from packing/printing: received postings leave the New queue and enter a local shipment/work group.
3. Allocate exactly one KIZ per physical marked unit, preserve ownership across retries, validate and set exemplars without blind mutation retries.
4. Generate Ozon labels through the asynchronous label job contract instead of treating the direct endpoint response as a PDF.
5. Add a live, read-only diagnostic panel where a seller can enter real credentials and inspect failures without publishing an installer.

The private production repository `rupphi/source-wcode` is unavailable. The design therefore uses the public `rupphi/test-wcode` source plus the 1.1.61/1.1.62 release notes. WCode 1.1.62 is a license-only hotfix; the latest relevant Ozon behavior remains 1.1.61.

## Safety boundaries

- The initial diagnostic run is read-only. It must not create exemplars, ship postings, create label tasks, change stock, or change product data.
- Credentials entered in the diagnostic panel live in memory unless the seller explicitly saves the store.
- Never include API keys, authorization headers, raw KIZ, complete product payloads, or document URLs in logs, UI reports, test evidence, or exceptions.
- Marketplace mutations have no automatic network retry. A lost response requires a fresh readback and a durable `RECONCILE_REQUIRED` state.
- Label creation is a mutation. Persist a local pending record before the call and never create a second task automatically after an ambiguous response.
- No installer is produced in this phase.

## User workflow

### Receive

The New tab refreshes the actionable Ozon queue with a bounded cutoff window and cursor pagination. The header checkbox selects every visible eligible posting. `Nhận đơn` creates a local Ozon shipment/work group for the current day or adds the postings to an existing same-day group selected by the seller.

After durable membership is saved, received postings no longer appear under New. They appear under Packing inside the selected group. A later remote refresh may update their status but must not duplicate membership.

### Pack and KIZ

Opening a group refreshes every selected posting through `POST /v3/posting/fbs/get` with `product_exemplars=true`. The detail response, not the list response, authorizes KIZ and packing.

For every physical quantity requiring marking:

- Resolve the exact offer/SKU/GTIN mapping.
- Reserve one available KIZ under a durable posting/item/unit ownership key.
- Reuse the same reservation after restart or retry.
- Reject duplicate KIZ ownership across all open jobs.
- Validate the complete posting payload.
- Persist the request fingerprint before `set`.
- Poll status and require accepted exemplars before shipping or producing the final bundle.

If a response to `set` or `ship` is lost, read the posting/exemplar state back. Never blindly repeat the mutation.

### Ozon label

Replace the current direct call to `/v2/posting/fbs/package-label` with:

1. Persist local label job as `CREATE_PENDING`.
2. `POST /v2/posting/fbs/package-label/create` with bounded posting numbers.
3. Parse current `result.task_id`, with the older `result.tasks[]` schema as a compatibility fallback.
4. Persist the task ID before polling.
5. Poll `POST /v1/posting/fbs/package-label/get`, sending `task_id` using its safe server-compatible scalar form.
6. When `file_url` is ready, accept only an HTTPS Ozon document host, download with a size bound, and verify `%PDF-`.
7. Publish the file atomically and retain the same task for reprint/recovery.

`FAILED` may be retried only through an explicit seller action. `RECONCILE_REQUIRED` must never create another label task automatically.

## Live diagnostic panel

Add `Kiểm tra Ozon API` under Settings with Client ID, masked API key, optional posting number, `Kiểm tra chỉ đọc`, cancel, and `Sao chép báo cáo đã ẩn bí mật`.

Read-only stages:

| Stage | Endpoint | Purpose |
|---|---|---|
| Seller | `/v1/seller/info` | Credential/account validity |
| Roles | `/v1/roles` | FBS, exemplar, and label capability hints |
| Warehouses | `/v2/warehouse/list` | At least one accessible warehouse |
| Queue | `/v4/posting/fbs/unfulfilled/list` | Actionable queue contract and cursor shape |
| Posting | `/v3/posting/fbs/get` | Optional posting status, requirements, products, exemplars |

Each row shows stage, endpoint, result, HTTP code, elapsed time, safe explanation, and next action. A failure stops only dependent stages. HTTP 401/403, 400/schema, 404/deprecated endpoint, 429/retry delay, timeout/TLS, invalid JSON, and incompatible response shapes are distinguished.

The report exporter contains only redacted summaries and short safe error codes. The panel does not persist responses.

## Components

- `OzonDiagnosticsService`: bounded read-only requests, result model, redaction, cancellation.
- `OzonLabelService`: create/poll/download state machine.
- `OzonLabelJob` persistence: additive SQLite schema and repository operations.
- Existing FBS receipt/group persistence: extend for Ozon membership rather than inventing a second queue.
- Existing marketplace FBS service: retain fresh posting checks and durable KIZ reservations; tighten accepted-state and readback gates.
- Settings UI: diagnostic dialog/panel with no secret logging.

## Error and recovery model

- 401/403: credentials or role failure; do not mutate or retry.
- 400: show endpoint-specific contract guidance; do not retry unchanged input.
- 429: respect `Retry-After`; diagnostic panel reports the wait and stops that endpoint.
- 5xx/timeout on reads: limited bounded retry may be offered manually.
- timeout/lost response on mutations: mark ambiguous and reconcile by a separate read.
- invalid label/PDF: preserve task ID, mark safe failure, never expose document URL.
- cancellation: stop between calls and leave durable jobs resumable.

## Verification

Tests use mock HTTP only and contain no seller credentials.

- Ozon queue pagination and received-posting exclusion.
- Exact physical quantity to KIZ reservation mapping and restart reuse.
- Duplicate KIZ, missing GTIN, changed posting fingerprint, rejected/pending exemplar failure paths.
- Label direct `result.task_id`, legacy tasks array, pending-to-ready polling, invalid PDF, unsafe URL, lost create response, and recovery with an existing task.
- Diagnostic 401/403/400/429/timeout/invalid JSON redaction and cancellation.
- UI contract for masked key, optional posting, read-only warning, result columns, and no installer action.

