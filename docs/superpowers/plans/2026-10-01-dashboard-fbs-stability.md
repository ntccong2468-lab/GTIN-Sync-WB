# Dashboard and FBS stability implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task by task, with a final independent review.

**Goal:** Finish the requested mint dashboard, unified report, three-marketplace FBS/KIZ/labels and navigation stability on the existing 0.7.0 branch.

**Architecture:** Keep the standalone .NET 8 WinForms app and its SQLite data. Refine existing gateway and page lifecycle boundaries; verify API behavior with deterministic HTTP fixtures and UI behavior on Windows.

**Tech Stack:** .NET 8, WinForms, Microsoft.Data.Sqlite, SkiaSharp, ZXing, Inno Setup.

**Spec:** MarketplaceHub/README.md (0.7.0 implementation) and the user's current request: mint/green dashboard reference, merged overview/finance, WB/Ozon/Yandex FBS and marketplace-specific labels with KIZ.

## Global constraints
- Standalone Windows app; no web or ChatGPT login dependency.
- No fixture data in normal operation. Test data stays in test executables and is removed.
- Network work must not block the UI; stale callbacks must not mutate a new page.
- Preserve official marketplace statuses and original label contents.
- KIZ must be verified before successful shipping is reported.

## Review focus
- Multi-line postings: selecting one line must preserve the full posting and mandatory quantities.
- Navigation during asynchronous work: never operate on disposed controls or a replacement page token.
- Slow image responses after filtering: never attach an old product's image to a new row.
- API permissions/status failures: no guessed new orders and no fabricated financial totals.
- Partial KIZ acceptance and delayed shipment processing: no premature success.

### Task 1: Gateway contract correctness
Files: MarketplaceHub/Services/MarketplaceGateway.cs; tests/MarketplaceHub.Contracts/*.
- [x] Add deterministic real-gateway tests for WB status errors, Ozon mark vs country requirements, Yandex hasCis, Ozon shipment readback and exemplar acceptance, PDF label signatures, Yandex quantities.
- [x] Run baseline: 4/10 passed; six expected regressions reproduced.
- [ ] Correct these existing paths and run the whole contract suite.

### Task 2: UI lifecycle and layout
Files: MarketplaceHub/UI/MainForm.cs; tests/MarketplaceHub.UI/*; .github/workflows/marketplace-regression.yml.
- [ ] Run Windows regression tests against the old page lifecycle, status tabs, toolbar geometry and report range.
- [ ] Dispose page controls, bind image results to row identity and bound background decoding, commit checkbox edits, make FBS actions fit at minimum width.
- [ ] Make report date filters update the chart and refreshed metrics.
- [ ] Run Windows UI suite, inspect the generated screenshots, and complete a final independent code review.

### Task 3: Deliver the verified Windows build
Files: MarketplaceHub/MarketplaceHub.csproj; MarketplaceHub/installer/MarketplaceHub.iss; MarketplaceHub/README.md; .github/workflows/build-marketplace-hub.yml.
- [ ] Require both regression suites before packaging 0.7.1.
- [ ] Build and test installed EXE on a Windows runner.
- [ ] Deliver the installer and GitHub review link; state unverified live seller API / physical printer boundaries accurately.
