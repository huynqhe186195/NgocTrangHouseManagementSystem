# Tasks — Feature 002 D01/D02

**Statuses:** implemented test code is not executed CI evidence; CI evidence is not end-to-end BUSINESS ACCEPTANCE.

- [x] Identify approved 2026-11-04 D01 target-day and D02 calendar-denominator/midpoint-up policy; do not reopen Owner decisions.
- [x] Audit existing `RoomChangeDailyRentQuoteCalculator`, `RoomChangeApprovedH2H3ExecutionPolicy` and current H4 execution guards; reuse accepted source.
- [x] Add exhaustive calendar oracle: 118 daily scenarios in existing `TRF_D01_04` Fact (xUnit test count unchanged).
- [x] Add even/odd month midpoint rounding oracle in existing `TRF_D01_08` Fact (xUnit test count unchanged).
- [x] **Persisted SQL candidate regression implemented; exact-head `d1af665b` CI PASSED (historical, not a later-head certification):** extend existing `TRFPG34` to write and independently re-read all **118 signed/reviewed** D01/D02 date partitions across 28/29/30/31-day months in a single rollback-only SERIALIZABLE PostgreSQL scratch schema; verify stored integer VND amounts, day ownership and canonical SQL↔.NET digest for each row. Extend existing `TRFPG31` with SQL-persisted 22.5→23 VND cases for 28/30-day months. Preserve the seven existing xUnit Facts and strict **103** focused PG gate. Commit `684c5693` is only the first test-code candidate; exact-head CI is required before any PASS claim.
- [x] Verify CI on `d1af665b`: H4 disposable PG `42017530703` (103/103 PG, HTTP 1/1, migration 1/1), Slice A `42017530623` (162/162 Domain, 103/103 PG, HTTP 1/1), Windows `42017530616` (712/712 Domain, 183/183 Application, 447/447 EF, 76/76 HTTP), Identity `42017530764` SUCCESS. These are historical exact-head results only; all subsequent code heads require independent checks. The 118 SQL candidate rows are NOT 118 issued invoices or 118 authenticated H4 transfers.
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


## Execution-ready outstanding acceptance — 2026-11-05

- [ ] **D01/D02 official issued-invoice parity (28/29/30 days):** in an isolated PostgreSQL fixture per calendar type, create two distinct room contracts with different integer-VND rent, a signed D-day transfer candidate, the ORIGINAL issued source invoice/rent line and approved correction. Authenticate the owner operation; after COMMIT independently SELECT both room day partitions (source D−1, target D), original invoice, correction and receivable. Recalculate integer rounding per line with an independent oracle. Verify immutable original invoice and source/target IDs, no duplicate day and no double charge. Do not substitute a quote candidate for an official invoice.
- [ ] **Negative authorization and integrity:** reject wrong Building, disabled/revoked account, forged signed candidate, wrong denominator, altered source/target VND, stale revisions, replay and direct SQL overwrite of issued invoice/correction. Assert unchanged row counts and original amounts after denial; race two conflicting approved corrections and prove at most one durable commit.
- [ ] **Rollback:** induce a failure between correction preparation and posting within one isolated transaction, then independently verify no partial invoice, receivable, credit or duplicate correction survived. Record the SQLSTATE/API status and before/after fingerprint.
- [ ] **Ordinary 2G.5 policy isolation:** for an ordinary move-out (not H4 transfer), assert full final-month rent still applies, original issued invoice remains immutable, and H4 D01/D02 proration does not leak into this lane.
- [ ] **Exact-head reproducibility:** run all relevant authenticated HTTP, PostgreSQL, migration and full regression workflows on the commit containing these tests; inspect failed/skipped counts and relevant job steps. Do not mark BUSINESS ACCEPTED merely because current candidate SQL or an earlier SHA passed.

**Merge discipline:** PR #5 combines material from draft PR #3/#4/#6; retain them as review sources, do not merge duplicates, and do not merge to `main` or touch production. H4 external receipt, rent credit consumption, bank refund, D03 service tariffs, D06 signatures and Owner PDF export remain separate HOLD gates.
