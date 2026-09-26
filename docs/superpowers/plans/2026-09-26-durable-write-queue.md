# Durable WB write queue implementation plan

> For agentic workers: implement task by task with tests before product changes.

**Goal:** Make GTIN Sync WB survive interruption and avoid duplicate or wrong-size writes after uncertain WB responses.

**Architecture:** Keep the existing WinForms client and DPAPI settings. Persist a completed read snapshot and confirmed card-level write jobs atomically under LocalAppData. A processor rereads each WB card before POST, stores every transition, treats transport ambiguity as unknown, and verifies the target chrtID before success. No task writes without the seller's confirmation.

**Tech Stack:** .NET 8 WinForms, System.Text.Json, Inno Setup, Windows CI.

**Spec:** `docs/superpowers/specs/2026-09-26-gtin-sync-wb-design.md`

## Tasks

- [ ] Add tests for durable queue serialization/restart, unknown POST outcome, duplicate and drift checks; prove failures on Windows CI.
- [ ] Implement no-retry write transport, conservative API gates, card-level processor with persistent statuses and readback.
- [ ] Wire preview freshness, confirmation, resume and per-row status into WinForms. Persist read snapshots and provide settings/status UI.
- [ ] Add CSV/XLSX export tests and implementation; document limits and actual verification.
- [ ] Run Windows CI, inspect smoke/screenshots, download the actual installer and publish release artifacts.

## Release gates

No seller token is embedded or used. An installed Windows smoke test proves desktop shortcut, startup and DPAPI round trip. Offline API mocks prove the processor branches. Live seller API compatibility remains explicitly unverified until the seller runs a confirmed operation.
