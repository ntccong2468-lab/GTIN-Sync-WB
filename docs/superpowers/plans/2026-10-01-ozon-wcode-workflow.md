# Ozon WCode-compatible workflow implementation plan

**Goal:** Receive Ozon postings into same-day work groups, allocate/submit KIZ safely, fix official label retrieval, and expose a read-only real-API diagnostic panel without producing an installer.

**Architecture:** Keep marketplace-specific mutations behind Ozon services. Use the existing generic FBS UI/state only for shared receipt and grouping. Persist mutation intent/task identity before remote calls, reconcile ambiguous outcomes, and keep diagnostics read-only and ephemeral.

**Spec:** `docs/superpowers/specs/2026-10-01-ozon-wcode-workflow-design.md`

## Task 1: Red tests for Ozon label jobs

**Files:** `tests/MarketplaceHub.Fbs/Program.cs`, `MarketplaceHub/Services/OzonLabelService.cs`, persistence contracts.

- Add fixtures for v2 create returning direct `result.task_id` and legacy task arrays.
- Add poll pending/ready/failed, numeric task ID, invalid URL/PDF and ambiguous create tests.
- Add restart/reprint test proving an existing task is reused.

## Task 2: Durable Ozon label service

**Files:** service/model/repository partials and additive SQLite migration.

- Add durable label job schema and repository methods.
- Implement create, persist, poll, bounded verified download and atomic publish.
- Route Ozon `DownloadLabelAsync` through the new service.
- Remove the old assumption that the direct v2 endpoint returns PDF bytes.

## Task 3: Red tests for receive and KIZ workflow

**Files:** FBS and persistence test projects.

- Cover unfulfilled cursor pages, same-day group membership and New-tab exclusion.
- Cover one KIZ per unit, exact mapping, duplicate ownership, restart reuse, changed details, exemplar acceptance and ambiguous mutation readback.

## Task 4: Ozon receive/group integration

**Files:** shared FBS state/database/service/UI partials.

- Reuse the shipment/work-group abstraction for Ozon.
- Add create/add same-day choice from selected New postings.
- Hide durably received postings from New and show them under Packing.
- Refresh posting details before any KIZ/pack decision.

## Task 5: Harden automatic KIZ preparation

**Files:** `MarketplaceFbs.cs`, application FBS service, persistence partials.

- Allocate by exact posting/item/unit and preserve reservation order.
- Require accepted exemplar status before ship/label bundle.
- Persist fingerprints and reconcile lost responses without blind retries.

## Task 6: Red tests and implementation for live diagnostics

**Files:** diagnostics service/models, Settings UI, contract/UI tests.

- Add safe stage result models and read-only endpoint runner.
- Add status-specific guidance, rate-limit reporting, cancellation and redaction.
- Add the masked-key dialog and redacted report copy action.
- Ensure no diagnostic action calls mutation endpoints or saves secrets implicitly.

## Task 7: Verification without packaging

- Run all portable contract/FBS/product/state/print/UI suites where supported.
- Push the source branch and run Windows CI for build and regression execution.
- Review diagnostics and workflow screenshots using fixtures only.
- Do not run publish, Inno Setup, installer retention, or release steps.
- Hand the user the source/CI result and instructions for opening the diagnostic panel with real credentials.

