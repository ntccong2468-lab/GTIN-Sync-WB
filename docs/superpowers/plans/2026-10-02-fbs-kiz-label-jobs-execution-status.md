# FBS/KIZ execution status

Approved plan: 2026-10-02-fbs-kiz-label-jobs.md. Base: 1dc0d757.
Feature branch: feat/fbs-kiz-label-jobs. Code tasks completed: 3/6.
Baseline: cd1d59a, Windows run 37066694838: 196/196 checks passed. Live seller API: not run. Installer: disabled.

Execution decisions: sparse shallow checkout preserves original Git object SHAs; Windows Actions runs exact-SHA RED/GREEN because this execution host lacks dotnet/WinForms. No historical test result counts as evidence for this branch.

Task 1 RED: dbd0524, run 37067129222: expected missing workflow domain types. Full log reviewed before implementation.

Initial persistence: 08d8d2e, run 37068023723: 10 new checks and all original regression steps passed.
Credential fence RED: 4e74595, run 37068311179, job 111041501027: 11/12 passed; the stale credential response correctly reproduced a missing service authorization fence.

Task 1 GREEN: bb7864b, run 37068480854: 12/12 persistence acceptance checks; existing FBS state suite passed. Purchase ID/null updates, HMAC/CIS deduplication, additive migration, encrypted blocks, optimistic claims and deletion/credential fences implemented.
Deferred within this plan: isolate old SelfTest from the seller DB as part of Task 6 portable acceptance; portable remains blocked until that check passes.

Task 2 RED: bc39ec1, run 37068952244: missing new SUZ transport interfaces. Initial GREEN: 4dad543, run 37069501672, 25/25 workflows.
Additional RED: 97506cd, run 37069848924, 25/27: wrong-GTIN order status and duplicate conflicting True API legal proof reproduced.
Task 2 GREEN: 4d2e1a1, run 37070102275, workflow job 111047288148: 27/27. Structured outcomes, exact signed JSON, safe-read backoff, persisted quota deadlines, block identity checks, CryptoPro output draining and exact True API CIS mapping implemented. Old purchase helpers remain until Task 4 replaces their entry points; this interim branch is not a portable release.

Task 3 RED: a71b28d, run 37070650476: missing purchase coordinator. Initial GREEN: 1ea7c8c, run 37070993115: 42/42. Additional RED: 9b0bddc, run 37071177979: duplicate block listing repeated recovery and conflicted with durable evidence. Final GREEN: 28aca98, run 37071824351, job 111052798133: 43/43. No purchase on uncertain receipt, durable quota deadlines, original-block recovery, partial-code preservation and credential/deletion fences verified.

Task 4 RED: 1104404, run 37072204253, job 111054006763: missing allocation/eligibility/label adapter and coordinator interfaces. Full log read before implementation.
