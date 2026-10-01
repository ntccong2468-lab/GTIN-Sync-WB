# Ozon and Yandex FBS implementation plan

**Goal:** Receive complete Ozon postings and Yandex orders into a durable local packing batch, attach a distinct code per required unit, confirm the whole order, then export official labels.

**Architecture:** Add partial gateway, services, database and UI files. Keep the common FBS listing. A local batch groups existing platform orders; it never invents a platform supply. Fresh details determine requirements and permitted actions. Durable unit reservations survive API failures and retries. Ship/status mutation is journaled before sending and ambiguous responses require reconciliation by readback.

**Constraints:** No cached status may authorize allocation. Cancelled, missing, unknown or changed orders fail closed. Yandex sends all items with `allowRemove=false`; preserve complete existing box distribution or require an explicit layout. Ozon ships one complete posting package. No physical printing before platform readback. Keep codes out of audit messages. Implementation is integrated by the root agent.

- [x] Add failing HTTP fixture tests for fresh identity/status, whole posting/box payloads, exact item/code status matching, duplicate codes and mutation readback.
- [x] Add gateway snapshots, whole payload builders, KIZ preparation and confirm/readback helpers; make fixtures pass.
- [x] Add persistent local batches, unit reservations, allocation transactions and mutation journal.
- [x] Add service receive/pack/export orchestration with reentrancy protection and GTIN validation.
- [x] Add batch detail UI through existing action hooks, explicit confirmation and stale page guards.
- [x] Compile app and run fixture checks; report remaining platform/live-account verification limits.

Local evidence: FBS contracts18/18; product catalog8/8; print11/11; Windows application, UI and10 persistence checks compile. Actual Windows execution and installer remain pending.
