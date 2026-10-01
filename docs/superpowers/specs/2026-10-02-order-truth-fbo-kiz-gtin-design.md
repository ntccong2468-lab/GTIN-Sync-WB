# Order Truth, FBO, KIZ Mapping and GTIN Sync Design

## Goal

Make Marketplace Hub show the real number of new FBS orders, receive every eligible WB order into one new or same-day open shipment without cancelled orders blocking the batch, and add the missing WCode-style FBO/KIZ Mapping/GTIN workflow. No installer is built until mock tests with more than 100 orders pass and the user completes a live-API validation round.

## Scope and constraints

- Platforms remain Wildberries, Ozon and Yandex Market; WB Supply semantics must not be reused for Ozon/Yandex batch identifiers.
- Seller data is local SQLite data. Schema changes must be additive and must not delete stores, products, orders, KIZ, mappings, print history or audit history.
- API credentials are never committed, logged, embedded in fixtures or shown unmasked in diagnostics.
- All remote writes require server readback before the application reports success.
- A cancelled or invalid order may be excluded with a precise reason, but it must not prevent other eligible selected orders from entering a shipment.
- Existing KIZ ownership and confirmed GTIN values are immutable during retry or a later failed batch.
- Quota handling resumes from a persisted checkpoint and honors `Retry-After`; it never restarts a completed page or batch.

## Evidence and current root causes

### WB shipment failure

`MarketplaceGateway.CreateWbShipmentAsync` validates the whole selection before creating or updating a supply. One row whose status is `new/canceled_by_client` returns immediately, so every otherwise valid selected order is rejected. The new-orders grid filters only the cached supplier status and cannot see the current `wbStatus`, so a cancelled row remains selectable until the receive operation.

### False FBS count

The report counts every cached row for which `IsNew(status)` is true. It does not subtract durable WB/marketplace shipment membership and does not evaluate the current cancellation state. Because order sync is an upsert rather than a snapshot replacement, historical queue rows remain in the count.

### FBO and KIZ Mapping gaps

The current FBO page is a product-cache table without variant color/size, quantity input, selection or label action. KIZ Mapping groups on one product barcode and displays only a coarse pipeline state; it lacks durable mapping rules, paging, National Catalog synchronization state and WB writeback progress.

### WCode patterns retained

The public `rupphi/test-wcode` source separates supply membership, GTIN mapping, Znack inventory and purchase pipeline state. It keeps background jobs serialized, exposes friendly errors, retains checkpoints, and makes GTIN-level KIZ inventory an explicit view. Marketplace Hub will reproduce those behavioral contracts in its existing .NET/SQLite architecture without copying private or unavailable implementation.

## Architecture

### 1. Canonical order truth

Add an `OrderTruthService` that derives a single state for each external order from:

1. latest marketplace queue status;
2. latest marketplace cancellation/status readback;
3. durable local shipment/batch membership;
4. shipment completion state.

The canonical states are `New`, `InShipment`, `Packing`, `Shipping`, `Completed`, `Cancelled` and `Unknown`. Report counters and FBS tabs consume this projection instead of directly calling `IsNew` on cached rows.

For WB, a row is selectable only when its supplier status is `new` and its current `wbStatus` is not cancelled. `confirm` is not a new order; it is valid only when the order is already a member of the chosen supply. Ozon/Yandex retain their existing platform-specific status rules and durable batch membership.

### 2. WB receive plan and partial-safe execution

Before mutating WB, build a receive plan for all selected order IDs:

- `EligibleNew`: current `supplierStatus=new`, not cancelled and not already in another supply;
- `AlreadyMember`: present in the chosen supply and currently `confirm`;
- `Cancelled`: either status contains a cancellation marker;
- `Rejected`: missing, malformed, unknown or belongs to another supply.

Only `EligibleNew` is added. `AlreadyMember` is retained for idempotent retry. `Cancelled` and `Rejected` are returned as per-order outcomes and never abort eligible orders. If there is no usable order after classification, no supply is created.

One supply is created at most once. Eligible IDs are patched in chunks of 100. After each chunk, membership is read back and saved. A later failure returns the same supply ID and the verified membership count. After all chunks, statuses and complete membership are read again; only then does the UI move the verified orders from “Đơn mới” to “Đang đóng gói”.

The receive result contains counts and per-order outcomes so the user sees “đã thêm”, “đã có trong shipment”, “khách hủy” or the exact failure instead of one batch-level popup.

### 3. Accurate report and new-order tab

`New FBS` equals the number of distinct canonical orders in `New`, not the number of product lines. It excludes:

- any order in WB supply or Ozon/Yandex batch membership;
- current cancellation states;
- historical cached rows whose current remote state is no longer new;
- duplicate SKU lines belonging to one posting/order.

The report displays the last successful order/status refresh time. If the current status readback is incomplete, it labels the counter stale/partial rather than presenting it as authoritative.

The grid header checkbox selects only eligible visible orders. Rows discovered as cancelled during receive are updated locally, become unselectable and disappear from the new-order projection after refresh.

### 4. FBO variant preparation

The FBO preparation grid becomes one row per exact active variant with image, name, color, size, SKU, GTIN/barcode, requested quantity, available KIZ and a selection checkbox. Seller can select several variants and export labels; output remains platform-specific and never synthesizes an official marketplace label when none was returned.

FBO/FBW supply requests remain a separate page backed by marketplace API data. The preparation grid does not fabricate warehouse quantities from product catalog rows.

### 5. Durable KIZ Mapping view

Add additive tables for mapping rules and GTIN sync jobs. A mapping row is keyed by `(store_id, marketplace, sku, variant_id)` and stores GTIN, source (`seller`, `znack`, `marketplace`), confirmation state, last error and timestamps. Existing `product_variants` and KIZ pool remain intact.

The page loads 50 rows at a time, prioritizes confirmed/mapped GTINs, and supports search/filter without resetting the current job. It shows exact mapping-rule count, total/available/reserved/assigned KIZ counts, current Znack stage, last error and actions to edit, sync, export, add or archive.

Seller-entered GTIN always wins over an unconfirmed automatic suggestion. A confirmed mapping is never cleared because a later page or batch fails.

### 6. Честный ЗНАК and WB GTIN writeback

The sync pipeline is:

1. normalize and checksum-validate GTIN-14;
2. load or query the product in Честный ЗНАК/National Catalog;
3. save product identity and registration state;
4. confirm the mapping for the exact WB variant;
5. queue WB card writeback grouped by `nmID`;
6. send at most 50 cards per request while preserving every size of each `nmID`;
7. read WB cards again and mark only verified variants complete.

Each endpoint has its own checkpoint and retry state. HTTP 429 stops only that endpoint, persists cursor/batch and resumes after the server delay. A failed later batch does not remove previously verified GTINs. Technical/barcode ambiguity is surfaced for seller choice rather than choosing the first barcode.

### 7. Test Center and live API boundary

Mock API coverage is completed first. The test matrix includes 119 and 205 WB orders, mixed cancelled rows, duplicate SKU lines, retry after an ambiguous PATCH, 429 resume, restart recovery, several sizes under one `nmID`, a failed second 50-card batch and KIZ ownership conflicts.

After those tests pass, add or reuse a local Test Center where the user enters real WB and Честный ЗНАК credentials. It performs read-only connection/product/status probes first. Mutating probes require a separately selected test product/order and explicit confirmation. The assistant asks the user for credentials only at this stage.

## UI behavior

- “Đơn mới” displays a checkbox in the header beside `ORDER ID`; select-all never includes cancelled/ineligible rows.
- Clicking “Nhận đơn” opens the choice between creating one new shipment and using an open shipment created today in Moscow time.
- Successful members disappear from “Đơn mới” and appear inside “Đang đóng gói”; opening that shipment is the only entry point for automatic KIZ and label export.
- Batch result dialog lists per-order outcomes and keeps the created supply ID visible for retry.
- Report counter uses distinct canonical orders and shows freshness/partial status.
- FBO and KIZ Mapping follow the provided screenshots while using the existing dark/light theme and responsive layout.

## Data safety

- Migrations create tables/indexes/columns only; no destructive migration is allowed.
- Shipment membership and KIZ reservations are written transactionally.
- A remote mutation uncertainty retains the local operation journal and blocks duplicate submission until readback resolves it.
- Store deletion continues to delete that store's related rows, including the new mapping/job tables, because this is an explicit local user action.
- Installer generation remains disabled for this milestone until mock and live validation are complete.

## Acceptance criteria

1. A selection containing valid `new/waiting` orders and `new/canceled_by_client` orders creates or updates one shipment for the valid subset and reports cancelled rows separately.
2. 205 eligible WB orders use one supply and three add-order requests of at most 100 IDs, with full membership/status readback.
3. The `Đơn mới` report and grid count distinct eligible orders only and both become zero for orders moved into shipment.
4. Restarting after a partial receive keeps the same supply and never adds a verified order twice.
5. FBO renders exact variants and exports selected quantities without mixing size/color/GTIN.
6. KIZ Mapping pages by 50, preserves seller mappings and exposes Znack/WB progress and errors.
7. WB GTIN writeback sends at most 50 cards, keeps all sizes of an `nmID`, resumes after 429 and preserves earlier verified batches.
8. All Windows regression suites pass before requesting live API credentials; no new installer is produced yet.
