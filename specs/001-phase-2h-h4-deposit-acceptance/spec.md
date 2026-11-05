# Feature Specification: Phase 2H H4 Deposit Disposition — Evidence Reconciliation

**Feature ID:** 001  
**Status:** DRAFT / EVIDENCE RECONCILIATION. Owner policy D01/D02/D03/D04/D06 was approved 2026-11-04; code/financial settlement and BUSINESS ACCEPTANCE are separate, still limited.  
**Created:** 2026-11-05  
**Code baseline:** `wip/phase-2g2` @ `a9519996c06d42db880cec4a741036a2e520e460`  
**Deployment:** NONE; isolated test/staging only.

## Business sources

- Original: `Nghiep_vu_Quan_ly_nha_tro_Phase_2G_2H_v1.0(2).docx`, §§6, 8, 9 (not found as a repository-root file; consult the owner's authoritative copy).
- Owner-approved H1–H6 and OI-01–OI-06 **plus the binding 2026-11-04 D01/D02/D03/D04/D06 and Owner contract-export addendum**: `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`. See `approved-policy-reconciliation-20261105.md` for precedence and implementation gates.
- Review scope: `etc/phase-audits/Phase2G2H_PostDecisionImplementationScope_20261101.md`.
- Historical evidence and HOLD gates: `etc/phase-audits/Phase2H_LocalStagingAcceptance_20261103.md`.
- Current exact-SHA checks: H4 disposable acceptance https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41970261950 and Windows safe regression https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41970262020 (both GitHub workflow conclusions: success, SHA `a951999`). Check logs and artifact scopes before widening claims.

## Problem and desired outcome

The owner approved H4: a transfer may keep verified deposit, require a **verified top-up** when the target needs more, or handle an **excess** through an authorized, exclusive next-rent credit / refund-liability disposition after reconciliation. The codebase now has extensive H4 intent checks and authenticated disposable PostgreSQL CI. This feature first **reconciles those implementations and proof** with the approved business outcomes; it must neither duplicate existing writers nor infer actual bank disbursement from financial intent.

## User stories and acceptance scenarios

### US1 — transfer to a room requiring greater deposit (P1)
As an authorized operator, I can reconcile cash-backed source deposit and a higher target requirement, then record the approved outcome without claiming that merely outstanding collection was paid.

- **AC-H4-01:** A signed H4 transfer can complete with an unpaid positive deposit top-up; the final contract/deposit outcome distinguishes verified carried cash from an outstanding separate top-up receivable, due no later than one *Vietnam calendar month* after the transfer date. Unpaid cash is never marked held or paid.
- **AC-H4-02:** An actual money collection is marked paid only on trusted, independently reviewed evidence, not an HTTP-provided flag or a reference string.
- **AC-H4-03:** Failed approval, concurrent completion, replay or DB commit checks cannot create duplicate posted funds or partially close the source contract.

### US2 — transfer with excess deposit credited to next rent (P1)
As an authorized operator, I can allocate verified excess once to an authorized future rent obligation.

- **AC-H4-04:** On the Owner's exclusive choice, one immutable, scoped credit instruction is reserved against the **next month's genuine issued rent** for the correct target; application is independently posted at most once and cannot silently edit a previously issued invoice.
- **AC-H4-05:** A future-rent **credit instruction** is not presented as cash repaid, bank transfer or a settled receivable until separately posted and proven.

### US3 — transfer with refund-liability disposition (P1)
As an authorized operator, I can assign the excess to an approved refundable balance with a unique beneficiary and amount, without asserting funds have already been disbursed.

- **AC-H4-06:** Liability vs actual refund settlement is explicit; approved payee, independent verification, unique money movement and audit trail are required to mark disbursed.
- **AC-H4-07:** Any replay, change of financial intent after freezing, contradictory re-hash or competing transfer is denied with no partial financial/room/contract mutation.

### Cross-cutting requirements

- **AC-H4-08:** One and only one disposition of each verified VND unit; source available = carried + authorized excess disposition, and target required = carried + appropriately classified top-up.
- **AC-H4-09:** No cross-building operations; authenticated scope, active status, live memberships, permissions and separation of duties apply to each operation.
- **AC-H4-10:** H1 linked old/new contracts, H5 full immutable tenant roster, H6 evidence signed by **Owner, handing-over Staff and primary tenant/authorized representative**, source final meters/condition and debt/invoice guards, and source/target room readiness remain intact; handwritten signed attachments are permitted.
- **AC-H4-11:** Tests cover positive, denial, replay, contradictory fingerprint, concurrent serializable loser, rollback and direct-SQL bypass in isolated PostgreSQL where relevant.
- **AC-H4-12:** Acceptance report references exact commit SHA, tests and PR. No production activity and no assertion of real-world bank payment from simulated test data.

## Scope boundaries

**In:** gap inventory; proof/claim reconciliation; additive code or test fixes **only if a specific uncovered scenario is verified**; stage-only evidence and documentation; strict acceptance report.

**Out:** wholesale transfer rewrite, fresh public route or tenant permission, changing ordinary 2G.5 rent policy, silently changing D01/D02/D03/D04/D06 owner policy details, applying production migrations, real bank payments, automatically merging to `main`.

## Approved policy (binding 2026-11-04) versus remaining verification

The 2026-11-04 owner addendum in the approved decision register **supersedes** the 2026-11-03 provisional descriptions. Do not re-ask already decided business choices:

- **D01 approved:** Vietnam local *civil* transfer date; source charged until D−1 and target from D. Historical source-day signed candidates remain immutable but are not executable for a new transfer unless re-reviewed and signed with target-day choice.
- **D02 approved:** actual 28/29/30/31 calendar-day denominator, independent nonnegative rent lines rounded to nearest VND with 0.5 upward. Golden edge-date, SQL parity and no double-day acceptance still required.
- **D03 approved in principle:** tariffs are scoped by RoomId + ServiceId + billing unit + price VND + effective version/editor; room/contract/invoice snapshots frozen. Metered charges use real readings; per-person charges use verified occupants. **The per-service choice between fixed full-month and day-proration is not globally approved**; it requires an explicit versioned rule for each non-metered service and issued-invoice correction proof.
- **D04-H4 approved in principle:** completing a transfer with an unpaid positive top-up is allowed, keeping it as a separate receivable with maximum due date `transferDate.AddMonths(1)` under Vietnam civil dates. For excess, Owner chooses **either** next-month issued-rent credit **or** independently reviewed manual/bank refund, never both. Approved beneficiary/payment evidence, partial/late receipts, immutable posting, unique transaction reference and replay/rollback/security tests remain **implementation and settlement proof gates**. Seven-business-day H4 refund SLA remains a proposal for scenario verification, not a recorded completed refund.
- **D06 approved:** three signatories (Owner, handing-over Staff, primary tenant/authorized representative); signed handwritten scan/upload accepted; independent role verification, roster, readings, physical defects/assets and attachment evidence still require executable checks.
- **Owner contract export approved:** scoped Owner download of the new contract with immutable old/new linkage and service/pricing snapshots. This is a **separate implementation scope**, not evidence that H4 money was settled.

**Not yet BUSINESS ACCEPTED:** live H4 collected top-up, posted future-rent credit, executed bank refund, per-room service tariff versioning/migration, issued-line corrections, comprehensive D01–D03/D06 enforcement on the new execution path, Owner contract export and independent human sign-off. No production or live bank operations.

## Success measure

A reviewed, exact-SHA H4 traceability matrix matches each AC to implemented code, deterministic test ID, relevant authenticated HTTP/PG evidence, and independently validated financial states. Any uncovered requirement becomes a bounded implementation task; passing CI alone is not an automatic business sign-off.
