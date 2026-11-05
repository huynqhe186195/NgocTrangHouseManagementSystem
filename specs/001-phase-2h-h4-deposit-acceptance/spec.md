# Feature Specification: Phase 2H H4 Deposit Disposition — Evidence Reconciliation

**Feature ID:** 001  
**Status:** DRAFT / EVIDENCE RECONCILIATION (not a declaration of BUSINESS ACCEPTED)  
**Created:** 2026-11-05  
**Code baseline:** `wip/phase-2g2` @ `a9519996c06d42db880cec4a741036a2e520e460`  
**Deployment:** NONE; isolated test/staging only.

## Business sources

- Original: `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx`, §§6, 8, 9 (not found as a repository-root file; consult the owner's authoritative copy).
- Owner-approved H1–H6 and OI-01–OI-06: `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`.
- Review scope: `etc/phase-audits/Phase2G2H_PostDecisionImplementationScope_20261101.md`.
- Historical evidence and HOLD gates: `etc/phase-audits/Phase2H_LocalStagingAcceptance_20261103.md`.
- Current exact-SHA checks: H4 disposable acceptance https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41970261950 and Windows safe regression https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41970262020 (both GitHub workflow conclusions: success, SHA `a951999`). Check logs and artifact scopes before widening claims.

## Problem and desired outcome

The owner approved H4: a transfer may keep verified deposit, require a **verified top-up** when the target needs more, or handle an **excess** through an authorized, exclusive next-rent credit / refund-liability disposition after reconciliation. The codebase now has extensive H4 intent checks and authenticated disposable PostgreSQL CI. This feature first **reconciles those implementations and proof** with the approved business outcomes; it must neither duplicate existing writers nor infer actual bank disbursement from financial intent.

## User stories and acceptance scenarios

### US1 — transfer to a room requiring greater deposit (P1)
As an authorized operator, I can reconcile cash-backed source deposit and a higher target requirement, then record the approved outcome without claiming that merely outstanding collection was paid.

- **AC-H4-01:** The final contract/deposit outcome distinguishes verified carried cash from an outstanding top-up liability.
- **AC-H4-02:** An actual money collection is marked paid only on trusted, independently reviewed evidence, not an HTTP-provided flag or a reference string.
- **AC-H4-03:** Failed approval, concurrent completion, replay or DB commit checks cannot create duplicate posted funds or partially close the source contract.

### US2 — transfer with excess deposit credited to next rent (P1)
As an authorized operator, I can allocate verified excess once to an authorized future rent obligation.

- **AC-H4-04:** One immutable, scoped credit/referral exists for the correct target and cannot be credited twice or silently alter an issued invoice.
- **AC-H4-05:** A future-rent **credit instruction** is not presented as cash repaid, bank transfer or a settled receivable until separately posted and proven.

### US3 — transfer with refund-liability disposition (P1)
As an authorized operator, I can assign the excess to an approved refundable balance with a unique beneficiary and amount, without asserting funds have already been disbursed.

- **AC-H4-06:** Liability vs actual refund settlement is explicit; approved payee, independent verification, unique money movement and audit trail are required to mark disbursed.
- **AC-H4-07:** Any replay, change of financial intent after freezing, contradictory re-hash or competing transfer is denied with no partial financial/room/contract mutation.

### Cross-cutting requirements

- **AC-H4-08:** One and only one disposition of each verified VND unit; source available = carried + authorized excess disposition, and target required = carried + appropriately classified top-up.
- **AC-H4-09:** No cross-building operations; authenticated scope, active status, live memberships, permissions and separation of duties apply to each operation.
- **AC-H4-10:** H1 linked old/new contracts, H5 full immutable tenant roster, H6 signed source handover, meter/debt/invoice guards and source/target room readiness remain intact.
- **AC-H4-11:** Tests cover positive, denial, replay, contradictory fingerprint, concurrent serializable loser, rollback and direct-SQL bypass in isolated PostgreSQL where relevant.
- **AC-H4-12:** Acceptance report references exact commit SHA, tests and PR. No production activity and no assertion of real-world bank payment from simulated test data.

## Scope boundaries

**In:** gap inventory; proof/claim reconciliation; additive code or test fixes **only if a specific uncovered scenario is verified**; stage-only evidence and documentation; strict acceptance report.

**Out:** wholesale transfer rewrite, fresh public route or tenant permission, changing ordinary 2G.5 rent policy, silently changing D01/D02/D03/D04/D06 owner policy details, applying production migrations, real bank payments, automatically merging to `main`.

## Open detail gates (do not assume resolved)

- D01/D02: signed handover-day charge ownership, effective cutoffs and exact whole-VND rounding/residual examples for any general mid-month lane.
- D03: independently approved official-invoice/service correction and receipt allocation without rewriting issued invoices.
- D04: when/what constitutes independent evidence of **actual** collected top-up or **actual** disbursed refund, versus a recorded pending liability/credit.
- D06: final signatory matrix and accepted physical defect/assets/checklist proof.
- Broader business acceptance for unrestricted operations remains HOLD until every applicable detail gate and exact-SHA acceptance is satisfied.

## Success measure

A reviewed, exact-SHA H4 traceability matrix matches each AC to implemented code, deterministic test ID, relevant authenticated HTTP/PG evidence, and independently validated financial states. Any uncovered requirement becomes a bounded implementation task; passing CI alone is not an automatic business sign-off.
