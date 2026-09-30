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
- [x] Correct these paths: 12/12 gateway contracts pass, including missing exemplar and missing status.

### Task 2: UI lifecycle and layout
Files: MarketplaceHub/UI/MainForm.cs; tests/MarketplaceHub.UI/*; .github/workflows/marketplace-regression.yml.
- [x] Run Windows baseline: 1/6 UI cases passed; five failures reproduced.
- [x] Dispose controls, preserve row identity, limit decoding and thumbnail/cache memory, commit checkbox edits and wrap toolbar controls.
- [x] Make report date filters update the chart; mark operational counts as current and stack finance at narrow widths.
- [x] 11/11 Windows UI/encoding regressions pass; final Report/FBS/Sync screenshots inspected; independent review findings addressed.

### Task 3: Deliver the verified Windows build
Files: MarketplaceHub/MarketplaceHub.csproj; MarketplaceHub/installer/MarketplaceHub.iss; MarketplaceHub/README.md; .github/workflows/build-marketplace-hub.yml.
- [x] Require both suites before packaging 0.7.1.
- [x] Windows release run 36791393116 passed: 12/12 API, 11/11 UI, 14/14 self-test, published and installed GUI startup, silent installation.
- [x] Deliver the retained 0.7.1 installer and PR #2. SHA256 f01733fd7efa43fdd92f282f1f706373277023018da25c2ac1f6689897e60670 matches Windows release output. Live seller API / physical printer boundaries documented.


## Final review and additional reproductions

Independent reviewer: `release_review` at commit 18cd5fa. Findings fixed: stale FBS detail / sync callbacks and incomplete Ozon exemplar confirmations. Added regressions reproduced all four failures before corrections.

Additional baseline failures reproduced on Windows: manual order sync bypassed the full-sync store gate; plain DataMatrix lacked GS1 FNC1. All partial/full sync entrypoints now share one gate. Printed KIZ encoding uses the legacy ZXing encoder with exactly one leading ASCII GS/FNC1. Local decode reproduced a CompactEncoding corruption of an internal GS after a C40 run; the legacy path passes both raw/scanner-prefixed payload checks.

Final Windows run 36791393116 passed on 40948b6. Screenshot verification complete. Retention run 36792007564 also passed every check and committed the verified installer at e20316a. Subsequent workflow-only packaging has identical application code. Live seller credentials and physical printers are outside the fixture/CI environment. Ozon/Yandex settlement adapters remain explicitly unsupported; no estimated totals are substituted.
