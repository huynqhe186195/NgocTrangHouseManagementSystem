# Tasks — Feature 001: H4 evidence reconciliation

**Status vocabulary:** DONE = directly observed evidence for this precise fact; TODO = not verified; HOLD = unresolved business/acceptance decision. **Task checkboxes are not substitutes for linked test logs.**

## Adoption baseline (spec-only)

- [x] **T001** Confirm `wip/phase-2g2` HEAD `a9519996c06d42db880cec4a741036a2e520e460` and identify both current child branches. Evidence: GitHub branch comparison/HEAD on 2026-11-05.
- [x] **T002** Confirm two exact-SHA GitHub workflows show overall `success`: H4 disposable PostgreSQL run `41970261950` and safe regression run `41970262020`. These overall statuses are **not** per-AC business acceptance.
- [x] **T003** Read approved H1–H6 / OI decisions and the post-decision scope; establish Constitution and feature specification.

## Evidence-first verification (still TODO)

- [ ] **T004** Read exact-SHA CI job logs/TRX and map selected H4 Domain (165 expected) and PostgreSQL (103 expected) cases to AC-H4-01–12. Record observed counts and exclusions.
- [ ] **T005** Audit H4 Domain intent/fingerprint and PostgreSQL deferred-commit constraints for conservation, re-hash contradictions, concurrency and idempotency.
- [ ] **T006** Audit actual authenticated H4 top-up, next-rent credit, refund-liability HTTP paths and verify their **money-status labels** against approved H4 semantics.
- [ ] **T007** Confirm active account, building membership and separation-of-duties authorization; negative cross-building tests must be authenticated, not source-only.
- [ ] **T008** Compare dated project audit files and implemented code against each acceptance criterion. Prepare `acceptance-matrix.md` with code/test/CI links and PASS/PARTIAL/HOLD; do not fake missing evidence.
- [ ] **T009** If T004–T008 reveal a real gap, design and implement the smallest cohesive correction in an independent code PR, with regression test before implementation.
- [ ] **T010** Execute exact-SHA stage-only Domain + Application + EF + HTTP + PostgreSQL regression for any code change. Capture immutable run URLs/TRX and prove rollback.
- [ ] **T011** Review Constitution and spec compliance, align business decisions and code, and complete converge report without overstating production readiness.

## Explicit holds

- [ ] **HOLD-D01/D02/D03:** signed day allocation, VND rounding and issued-invoice reconciliation for broad mid-month policy (not resolved by H4 tests alone).
- [ ] **HOLD-D04:** externally established proof of actual collection/disbursement vs pending credit/refund liability; no fictitious bank success.
- [ ] **HOLD-D06:** final signature and physical handover evidence criteria.
- [ ] **HOLD-RELEASE:** No `main` merge or production migration/activation from this adoption work.

**Next action:** T004–T008 as a single review batch before any financial writer change.
