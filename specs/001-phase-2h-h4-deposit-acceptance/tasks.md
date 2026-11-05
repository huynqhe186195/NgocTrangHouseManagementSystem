# Tasks — Feature 001: H4 evidence reconciliation

**Status vocabulary:** DONE = directly observed evidence for this precise fact; TODO = not verified; HOLD = unresolved business/acceptance decision. **Task checkboxes are not substitutes for linked test logs.**

## Adoption baseline (spec-only)

- [x] **T001** Confirm `wip/phase-2g2` HEAD `a9519996c06d42db880cec4a741036a2e520e460` and identify both current child branches. Evidence: GitHub branch comparison/HEAD on 2026-11-05.
- [x] **T002** Confirm two exact-SHA GitHub workflows show overall `success`: H4 disposable PostgreSQL run `41970261950` and safe regression run `41970262020`. These overall statuses are **not** per-AC business acceptance.
- [x] **T003** Read approved H1–H6 / OI decisions and the post-decision scope; establish Constitution and feature specification.

## Evidence-first verification (still TODO)

- [x] **T004** Read exact-commit PR CI job logs and correlate selected H4 Domain 165/165, PG 103/103, authenticated HTTP 1/1 and migration 1/1; Windows Full regression 712/183/447/76 PASS. Evidence: `acceptance-matrix.md`, runs 41985608433/41985608440. The PR checks execute synthetic merge SHA `3285089`, and archived TRX XML was not independently extracted; do not conflate parser fixture runs with behavioral tests.
- [x] **T005** Audit H4 intent and completion fingerprints, conservation formulas, canonical re-hash and PostgreSQL `TRFH4PG01–17` invariant tests; cover stage transfer competition/rollback in `STAGEPGHTTP01`. This proves deferred intent/completion, NOT a future real-cash settlement writer. See `acceptance-matrix.md`.
- [x] **T006** Audit authenticated H4 routes and three executed inner scenarios in `STAGEPGHTTP01`: top-up remains unpaid, future-rent credit and refund remain liabilities/instructions, not bank transfers or settled credits. Actual external H4 collection/refund/credit posting is **HOLD-D04-H4**; see `acceptance-matrix.md`.
- [x] **T007 — STAGING SECURITY TEST GATE PASS (not BUSINESS ACCEPTANCE).** Draft PR #4 exact head `2a0391e54483c9cb1360617fbe60d8a3b4181c25` proves through real authenticated `STAGEPGHTTP01` on throwaway PostgreSQL: Root `BootstrapOwner` revocation waits for the live H4 intent-read Building UoW, stale Root HTTP receives 403, disabled persisted Root is denied, and a separately delegated Building B operator's member revocation waits for an H4 request-read UoW commit; post-revocation H4 request HTTP is 403, delegated H4 financial intent remains 403, and the Owner retains 200. All four exact-head CI runs succeeded (H4 `41994322548`, Slice A PG `41994322523`, Windows `41994322601`, Identity `41994322594`). The founding Owner's self-issued membership cannot be revoked by ordinary delegation APIs and is not the test subject. **This resolves the bounded missing H4 member/Identity regression, not D04-H4 bank settlement, AC-H4-09 across nonexistent future money writers, owner sign-off, or production release.**
- [x] **T008** Create `acceptance-matrix.md` with all AC-H4-01–12, exact baseline/test-merge SHAs, test/code/CI links, bounded STAGING PASS versus PARTIAL/HOLD and source limitations.
- [ ] **T009 HOLD / separate code PR** Reconciliation exposes unverified actual H4 top-up settlement, credit consumption and refund disbursement, plus T007 coverage gap. First define authorized D04-H4 behavior and testable gaps; do not duplicate the already-working H4 deferred-intent writer.
- [ ] **T010 PARTIAL** Exact-SHA PR #4 tests finished: H4 authenticated PostgreSQL 1/1, migration 1/1, 103/103 PG; Slice A 162/162 Domain, 103/103 PG, 1/1 migration, 1/1 authenticated HTTP; Windows 165/165 selected Domain, 712/712 full Domain, 183/183 Application, 447/447 EF, 76/76 HTTP; independent Identity composition PASS. Full CI run URLs are recorded in `acceptance-matrix.md`. Raw TRX archive XML has not been independently inspected, and any future financial code implementation must repeat all matching exact-head tests and rollback review.
- [ ] **T011** Review Constitution and spec compliance, align business decisions and code, and complete converge report without overstating production readiness.

## Explicit holds

- [ ] **HOLD-D01/D02/D03:** signed day allocation, VND rounding and issued-invoice reconciliation for broad mid-month policy (not resolved by H4 tests alone).
- [ ] **HOLD-D04:** externally established proof of actual collection/disbursement vs pending credit/refund liability; no fictitious bank success.
- [ ] **HOLD-D06:** final signature and physical handover evidence criteria.
- [ ] **HOLD-RELEASE:** No `main` merge or production migration/activation from this adoption work.

**Next action:** T007's bounded **staging security regression** is evidenced by exact-head four-workflow CI; continue reviewing Constitution/spec and raw TRX if independent inspection is required. Keep PR #3 and PR #4 **Draft/unmerged** until owner review. Maintain T009, D01/D02/D03/D04-H4/D06 and production release as HOLD; never mislabel deferred H4 liabilities as bank-paid top-up, posted rent credit or paid refund.
