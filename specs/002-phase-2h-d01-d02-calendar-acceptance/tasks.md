# Tasks — Feature 002 D01/D02

**Statuses:** implemented test code is not executed CI evidence; CI evidence is not end-to-end BUSINESS ACCEPTANCE.

- [x] Identify approved 2026-11-04 D01 target-day and D02 calendar-denominator/midpoint-up policy; do not reopen Owner decisions.
- [x] Audit existing `RoomChangeDailyRentQuoteCalculator`, `RoomChangeApprovedH2H3ExecutionPolicy` and current H4 execution guards; reuse accepted source.
- [x] Add exhaustive calendar oracle: 118 daily scenarios in existing `TRF_D01_04` Fact (xUnit test count unchanged).
- [x] Add even/odd month midpoint rounding oracle in existing `TRF_D01_08` Fact (xUnit test count unchanged).
- [x] **Persisted SQL candidate regression implemented, CI NOT YET ACCEPTED:** extend existing `TRFPG34` to write and independently re-read all **118 signed/reviewed** D01/D02 date partitions across 28/29/30/31-day months in a single rollback-only SERIALIZABLE PostgreSQL scratch schema; verify stored integer VND amounts, day ownership and canonical SQL↔.NET digest for each row. Extend existing `TRFPG31` with SQL-persisted 22.5→23 VND cases for 28/30-day months. Preserve the seven existing xUnit Facts and strict **103** focused PG gate. Commit `684c5693` is only the first test-code candidate; exact-head CI is required before any PASS claim.
- [ ] Verify CI on the **new exact SHA**, including test counts, failures, skips and scratch PostgreSQL/HTTP workflow conclusions. The 118 SQL candidate rows are NOT 118 issued invoices or 118 authenticated H4 transfers.
- [ ] Independently inspect archived TRX XML when asserting artifact-level counters.
- [ ] Persisted signed D01/D02 source ↔ official issued rent line and append-only correction / no overlapping days, with SQL parity and authenticated HTTP negative and positive tests.
- [ ] Regression proof for ordinary 2G.5 full-final-month rent and immutable existing invoice.
- [ ] Business/Owner review and separately evidenced acceptance. Keep PR draft; no `main` or production.

This PR targets `wip/phase-2g2` independently from combined Spec Kit/H4 security PR #5. Financial H4 external collection, future rent-credit posting and bank refund stay HOLD.


## 2026-11-05 negative SQL tamper follow-up (test-only; not yet CI accepted)

- Existing `TRFPG32` now independently tries a forged **target-room rent amount** in addition to the previously covered forged source-room amount. The existing database validation must reject each with SQLSTATE `23514`.
- The same fact also probes an invalid target-day count (+1), preserving the approved D01 partition (source owns D-1; target owns D). `TRFPG34` already contains other invalid-date/day probes; this adds a negative check adjacent to financial tampering.
- This is **one existing xUnit Fact**, not newly counted cases. No production writer, financial settlement, migration, workflow filter or threshold was changed.
- Code commit: `115af54614d4c5f8b34a434be575648e6f9d534f` on combined draft PR #5. **New exact-head CI is PENDING/UNVERIFIED** as of this documentation update; previously green commit `d1af665b` cannot certify the newer code.
- Still HOLD: independently accepted 28/29/30-day issued-invoice SQL/HTTP parity; approved append-only corrections and rollback; 2G.5 ordinary final-month billing regression; actual H4 bank-backed top-up/credit/refund; independent sign-off and production.


## 2026-11-05 follow-up: independently reviewed CI and D01/D02 SQL tamper breadth

- On exact prior integration SHA `578c11957d4473744e11f0f14ca5c23727e5badf`, all four workflows concluded SUCCESS: H4 disposable PG `42026735187`, Slice A PG `42026734904`, Windows regression `42026735131`, Identity `42026735026`. Job logs directly show `TRFPG32` and `TRFPG34` Passed in both PG workflows, full Windows 712 Domain / 183 Application / 447 EF / 76 HTTP PASS with zero failures or skips, and Slice A 162 Domain / 103 PG PASS. This closes **only** the preceding source/target rent-line and target-day tamper staging test verification; it does not certify bank settlement or full business acceptance.
- Newer test-only SHA `ce5c9761a08ce2fafba046fb847cad9189acb60e` extends the **same existing `TRFPG32` Fact** with direct SQL insert tampering of the source-room days (+1) and actual calendar-month denominator (+1), while retaining previously added target-room amount and target-day rejection probes. No xUnit Fact count, CI threshold, financial application writer or migration changed.
- The `ce5c9761` change must obtain its **own exact-head PostgreSQL and regression CI success** before these new probes can be called staging verified. The four prior workflow successes only prove SHA `578c1195`.
- Still HOLD: 28/29/30-day officially issued rent-line correction parity, append-only signed financial correction, independent bank-backed H4 cash/credit/refund, the ordinary 2G.5 final-month regression and human BUSINESS ACCEPTANCE; production remains excluded.
