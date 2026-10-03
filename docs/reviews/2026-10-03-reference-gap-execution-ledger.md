# SDD ledger — plan: docs/superpowers/plans/2026-10-03-reference-gap-fixes.md

Pre-flight: Product metadata enriches card JSON consumed by variant catalog and UI; existing tuple/result signatures retained. Nullable finance fields require matching UI formatter. Picking exporter shares existing exact-variant grouping.

Ruling: Continue bounded corrections under prior approved design and explicit user request to fix remaining gaps — no new cloud/IDE subsystem and no repeated permission gate.
Ruling: Tests run through Windows GitHub Actions because local runtime is absent — remote test commits are intentionally RED and not release candidates.
Ruling: Public release notes 1.1.67 define the latest behavior; test-wcode 1.1.32 supplies API examples only — source-wcode latest source remains unavailable.

RED: a212848 / run 37090824647 reproduced finance (4), Ozon metadata (2), WB barcode (2), resize (1), picking (1). FbsState fixture failed compilation due return type; corrected before claiming SUZ RED.
RED: 5f97d3b / run 37091049526 reproduced invalid GTIN, known-order retry, expired block, auth checkpoint, wrong-GTIN download, ambiguous create, separate mapping barcode (7). Existing reservation failures caused by fixture pool leakage; isolated test pool after SUZ checks.

Implementation written; awaiting fresh Windows GREEN. No completion claim yet.

Final review: independent reviewer reference_gap_review, read only; no Critical, 8 Important including two legacy defects directly undermining the new flow.
Final: fix pass written for separate registration preparation journal, atomic SQLite purchase claim, definitive rejection, repeated-offer picking, ambiguous barcode guard, Moscow report date, scrollable finance, durable SUZ Retry-After and draining CryptoPro pipes.
RED: review tests run 37091917875, all named findings reproduced. Valid viewport resize RED in 37092225070; prior window resize was constrained by Windows desktop size.
Ruling: Correct legacy retry fixture GTIN checksum to 04608888888886 — the fixture must represent valid input now that normalization validates checksums; still verifies interrupted WB PUT/readback/reservation behavior.
Ruling: Legacy Znack registration page represents local preparation only, using a separate additive table — full new card publishing remains a separate integration requiring verified contract/account; no fake published status.

GREEN attempt 847abd7: build runner reproduced late concurrent purchase (43/44 FbsState) while regression runner passed that test. Root cause: second caller's auth could finish after first completed; initial CAS permitted any CODES_DOWNLOADED record. Claim now compares original updated_at; rechecks available pool after auth. Same concurrency test remains the regression.

FINAL GREEN: remote source 09704bcac282d366b4342dc3c76ad17e4e6ac77d, Windows build 37092774111 and regression runs 37092774168/37092778781 all completed/success. 222/222 across eight suites, zero failures; self-contained win-x64 portable artifact 11263182438. All installer steps skipped.
VISUAL: inspected final WbSupplyDetail, KizMapping, ShowReport screenshots and rendered Ozon/WB picking PDFs. A4 grouping totals and seller size match fixtures; no overlapping text observed in inspected samples. Real SKU images and physical printing remain unverified.
Ruling: Update existing approved feature PR #2; preserve workspace and branch, do not merge. Latest WCode source access, real API validation, physical printing and new-card National Catalog publishing are explicit remaining boundaries, not mocked completion claims.
COMPLETE: All reproducible in-scope reference-gap fixes and mock verification are finished. Preserve this ledger in committed docs before removing only this task's scratch ledger. Next input is a redacted report from masked in-app API checks; never request secrets in chat.
