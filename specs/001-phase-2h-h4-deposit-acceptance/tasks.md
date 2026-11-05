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
- [ ] **T007 PARTIAL** Authenticated H4 scenarios deny staff and cross-building intent/read/complete; `TRFH4PG04` denies foreign source at DB layer. Still explicitly map H4 account/membership revocation at commit and separation-of-duties to exact assertions; generic `STAGEPGHTTP01` title is not sufficient evidence.
- [x] **T008** Create `acceptance-matrix.md` with all AC-H4-01–12, exact baseline/test-merge SHAs, test/code/CI links, bounded STAGING PASS versus PARTIAL/HOLD and source limitations.
- [ ] **T009 HOLD / separate code PR** Reconciliation exposes unverified actual H4 top-up settlement, credit consumption and refund disbursement, plus T007 coverage gap. First define authorized D04-H4 behavior and testable gaps; do not duplicate the already-working H4 deferred-intent writer.
- [ ] **T010** Execute exact-SHA stage-only Domain + Application + EF + HTTP + PostgreSQL regression for any code change. Capture immutable run URLs/TRX and prove rollback.
- [ ] **T011** Review Constitution and spec compliance, align business decisions and code, and complete converge report without overstating production readiness.

## Explicit holds

- [ ] **HOLD-D01/D02/D03:** signed day allocation, VND rounding and issued-invoice reconciliation for broad mid-month policy (not resolved by H4 tests alone).
- [ ] **HOLD-D04:** externally established proof of actual collection/disbursement vs pending credit/refund liability; no fictitious bank success.
- [ ] **HOLD-D06:** final signature and physical handover evidence criteria.
- [ ] **HOLD-RELEASE:** No `main` merge or production migration/activation from this adoption work.

**Next action:** Finish T007 targeted identity/permission/commit-time review and resolve D04-H4 business settlement semantics before any real-money writer change. T004/T005/T006/T008 now have a dated evidence matrix; T010/T011 and owner/production gates remain open.
