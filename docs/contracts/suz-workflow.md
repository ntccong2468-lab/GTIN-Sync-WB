# SUZ workflow contract fixture

Source: public WCode ZnackApiClient/ZnackKizCodeService inspected on 2026-10-02 and the official True API documentation referenced in the approved design. These fixtures are not evidence of seller-account capability. Production auto-purchase remains disabled until a scoped Test Center probe verifies that profile.

| Operation | Contract used | Meaning |
|---|---|---|
| Create | POST /api/v3/order; exact signed JSON; orderId | Mutation, no blind retry |
| Status | GET /api/v3/order/status; bufferStatus/availableCodes/gtin | Safe read, one coordinator checkpoint per call |
| List | GET /api/v3/order/list | Candidate metadata; matching GTIN/quantity alone cannot correlate |
| Receive | GET /api/v3/codes; blockId/codes | Stateful read, one attempt |
| Blocks | GET /api/v3/order/codes/blocks; array or blocks collection | IDs bound to the requested order/GTIN |
| Recover | GET /api/v3/order/codes/retry?blockId=… | Known block only; missing/unsupported contract requires reconciliation |
| Legal | POST /api/v3/true-api/cises/info?pg=lp | Batch 100 identification codes; requestedCis exact mapping |

Fixtures: `{"orderId":"SUZ-1"}`, `{"blockId":"BLOCK-1","codes":["fixture code"]}`. A returned orderId/gtin, when present, must match the requested target. A receive response missing blockId is Unknown; recovery must establish issued blocks. An array of codes is accepted only for recovery of an already identified block.

Legal fixture: `[{"requestedCis":"01<GTIN>21<serial>","cisInfo":{"gtin":"<GTIN>","ownerInn":"<owner>","status":"INTRODUCED","statusEx":"EMPTY","packageType":"UNIT"}}]`. Missing fields and unknown response shapes confer no legal proof. EMITTED/APPLIED are not shipping eligibility. Physical acknowledgement is independent.

First catalog: explicitly selected Production/lp/PRODUCTION/UNIT/template 10 on the known official hosts. Import/resale/sandbox profiles remain Unsupported; no approximate production payload is sent.
