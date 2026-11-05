# Tasks — Feature 002 D01/D02

**Statuses:** implemented test code is not executed CI evidence; CI evidence is not end-to-end BUSINESS ACCEPTANCE.

- [x] Identify approved 2026-11-04 D01 target-day and D02 calendar-denominator/midpoint-up policy; do not reopen Owner decisions.
- [x] Audit existing `RoomChangeDailyRentQuoteCalculator`, `RoomChangeApprovedH2H3ExecutionPolicy` and current H4 execution guards; reuse accepted source.
- [x] Add exhaustive calendar oracle: 118 daily scenarios in existing `TRF_D01_04` Fact (xUnit test count unchanged).
- [x] Add even/odd month midpoint rounding oracle in existing `TRF_D01_08` Fact (xUnit test count unchanged).
- [ ] Verify CI on the **new exact SHA**, including test counts, failures, skips and scratch PostgreSQL/HTTP workflow conclusions.
- [ ] Independently inspect archived TRX XML when asserting artifact-level counters.
- [ ] Persisted signed D01/D02 source ↔ official issued rent line and append-only correction / no overlapping days, with SQL parity and authenticated HTTP negative and positive tests.
- [ ] Regression proof for ordinary 2G.5 full-final-month rent and immutable existing invoice.
- [ ] Business/Owner review and separately evidenced acceptance. Keep PR draft; no `main` or production.

This PR targets `wip/phase-2g2` independently from combined Spec Kit/H4 security PR #5. Financial H4 external collection, future rent-credit posting and bank refund stay HOLD.
