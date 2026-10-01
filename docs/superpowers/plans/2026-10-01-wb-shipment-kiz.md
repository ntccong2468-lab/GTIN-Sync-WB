# Three-marketplace FBS and product synchronization plan

> **For agentic workers:** Use superpowers:executing-plans for native implementation in this session.

**Goal:** Select all FBS orders, create or choose today's open WB shipment, add every selected order, attach KIZ and export labels from the complete supply view. Extend receiving/packing to Ozon postings and Yandex orders/boxes, and synchronize complete product variants for all three marketplaces.

**Architecture:** Keep the existing FBS flow and print bundle. Add a WB gateway partial for same-day supply listing, batched adding and server readback. Persist supply membership and KIZ reservations in additive SQLite tables; partial failure preserves the shipment and exact reserved codes. A shipment choice dialog and complete supply detail replace direct printing from new orders.

**Tech Stack:** .NET8 WinForms, SQLite, existing SkiaSharp/ZXing, Windows CI.

**Spec:** User's 3 screenshots and instruction: select all → create new or add to same-day shipment → auto KIZ → export sticker like wcode image3. Official WB FBS supplies API and public test-wcode supply workflow.

## Global Constraints

- Existing shipments must belong to the selected store, be open and have today's createdAt in UTC+03:00 (Moscow).
- Add orders in batches of at most100; a119-order selection must create exactly one shipment.
- Verify membership and confirm status before KIZ or sticker retrieval.
- Do not recreate a shipment or allocate another KIZ after ambiguous/partial failure.
- Preserve existing seller data, Ozon/Yandex packing and WB print bundle behavior.
- Delivery remains an explicit separate action; physical printing is never claimed by CI.

## Review Focus

- A shipment becomes closed or is from yesterday after the picker was loaded: reject before adding.
- Second batch fails: preserve supplyId and verified first batch, stop KIZ/sticker requests.
- Network error after KIZ PUT: retain its reservation and read current metadata on retry.
- Several sizes of one nmID: resolve GTIN from order chrtId/SKUs, never first card size.
- Navigation or repeated clicks: one job; background completion must not replace a new page.

### Task1: Gateway and contract tests

**Files:** Services/WbShipments.cs, Services/WbLabels.cs, MarketplaceGateway.cs, Core/Models.cs, tests/MarketplaceHub.Contracts.
**Interfaces:** GetWbTodaySuppliesAsync(store,ct,now?), GetWbSupplyAsync(store,id,ct), GetWbSupplyOrderIdsAsync(store,id,ct), GetWbOrderStatusesAsync(store,ids,ct); CreateShipmentAsync keeps old parameters and adds optional existingSupplyId/name/progress.
- [x] Reproduce119-order rejection and invalid/store-mismatched IDs with failing contracts.
- [x] Implement same-day pagination, single create, 100-ID batches, membership/status readback and partial error supplyId.
- [x] Add red→green contracts for closed/yesterday choices, 429, missing readback, cancelled status and same-shipment retry.

### Task2: KIZ persistence and complete supply UI

**Files:** Infrastructure/AppDatabase.WbShipment.cs, Services/AppServices.WbShipment.cs, UI/WbShipmentDialog.cs, UI/MainForm.WbShipment.cs, existingMainForm.cs; Windows UI tests.
**Interfaces:** StoreWbSupplyMembership/FindWbSupplyForOrder; ReserveWbKiz/ConfirmWbKiz; EnsureWbSupplyKizAsync(store,orders,gtinByOrder,useKiz,ct,progress).
- [x] Test reservation isolation, no reuse after uncertain response and exact current metadata on retry.
- [x] Wire create/add choice into select-all actions, closing and printing new WB orders.
- [x] Open full supply detail with all server IDs, verified KIZ status, export and explicit delivery.
- [x] Test filtered select-all, choice rendering, complete supply rendering, correct variant GTIN and navigation guards.

### Task3: Verification and release

**Files:** README, comparison document, project/installer/workflow version, PR2.
- [x] Review independently and address important findings with red→green regressions.
- [ ] Build0.7.3, contracts, print tests, UI tests, installed self-test and GUI smoke on Windows.
- [ ] Retrieve exact installer, verify SHA256 against CI, save and deliver with live-token/printer boundary.

## Ledger

- Base2f5cf6f; existing feature branch is clean and used for PR2. Current desktop source is already isolated from marketplace-hub-v2.
- Root causes: CreateShipmentAsync rejects selections>100; always creates a new supply; new-tab print bypasses shipment; supply detail contains one order; KIZ uses first card size and has no persisted reservation.
- Constraints and interface scan: gateway→UI supplyId/membership; UI→KIZ exact variant GTIN; KIZ→print fresh metadata. No conflicting signatures.


### Task4: Ozon/Yandex receiving and packing (scope added by user)

**Files:** independent marketplace FBS gateway/service/database/UI partials and regressions.
- Read the current whole posting/order from the marketplace before allocating codes.
- Preserve every line and unit; exact item identifiers and GTINs, no missing boxes or inferred code readiness.
- Reserve one code per unit, retain ambiguous results and recover by remote readback rather than allocating replacements.
- Explicit receive/pack action then verified platform-specific labels, with one operation and navigation guards.
- Keep WB Supply semantics separate from Ozon postings and Yandex order/box identifiers.

### Task5: Product synchronization (scope added by user)

**Files:** independent product catalog gateway/service/database/UI partials and regressions.
- Strict pagination, cursor-cycle/partial-payload detection, retry server quota on the same cursor.
- Preserve card variants, every size/barcode/GTIN, exact SKU-owned images and per-shop isolation.
- Add durable checkpoints; upsert catalog without deleting seller mappings or previous valid data after a failed page.
- Expose synchronization, variant inspection and missing GTIN in the app.

### Additional review corrections

- Final full-selection membership is re-read after every WB batch; core verifies selection again before KIZ.
- Display, export and explicit delivery share GTIN/uniqueness/status/ownership validation.
- Transactional KIZ adoption refuses legacy and other-marketplace ownership conflicts.
- Missing local WB details stay visible as unresolved server IDs and block KIZ/export.
- Export buttons recover if a subsequent supply refresh fails.

**Evidence so far:** 119-order rejection reproduced (22/24 red), initial gateway fix24/24; expanded30/30; lost-earlier-batch regression30/31 red then31/31 green. Windows UI tests for new API compiled red before implementation; application/UI compile now pass. Current local evidence:31/31 WB contracts,18/18 Ozon/Yandex contracts,8/8 catalog contracts,11/11 print regressions; app/UI/persistence10 checks compile. Actual Windows execution and installer are pending.


NonWB review corrections: order-wide durable ship journal prevents ambiguous mutation resend across batches; Yandex always verifies the full stored layout before READY_TO_SHIP, refreshes requirements before ship and retains reordered remote codes by unit reservation. Current Ozon proof needs ship_available, all exemplars, stable current full marks and no rejection/error_codes; unsupported multibox/third-party delivery is explicit. Historical unfinished batches can resume but cannot receive new-day orders.

Catalog review corrections: GTIN checksum and ambiguous barcode detection; Ozon requires pagination cursor and cumulative terminal total; WB optional price batches preserve all card sizes; Yandex keeps offer identity across mapping changes and reads offer-owned mediaFiles pictures.


Windows run36807964431 reproduced missing fixture credentials and reflection invocation mismatch; these test inputs were corrected. Run36808511167 passed10/11 durable checks, reproducing a real Yandex layout retry bug when identifiers/status reverses code order. Planning now uses persisted unit reservation order before generating the box layout, matching allocation and retaining the same codes after a lost response. Screenshot review also found compact WB action clipping and collapsed Ozon product text; responsive widths/action wrapping and UI assertions were added. Scanner-prefix alias ownership is protected with additive nonunique canonical indexes; no KIZ rows are migrated/deleted.
