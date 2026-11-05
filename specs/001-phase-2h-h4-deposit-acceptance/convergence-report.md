# Feature 001 — H4 brownfield convergence review (2026-11-05)

**Assessment type:** source-to-spec / exact-SHA CI reconciliation; NOT a business acceptance or production authorization.
**Source baseline:** `wip/phase-2g2` @ `a9519996c06d42db880cec4a741036a2e520e460`.
**Spec Kit docs:** draft [PR #3](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/pull/3).
**Dedicated H4 Identity regression:** draft [PR #4](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/pull/4), code/test head `907c700901adb44b822013fe84100b22bdb12fe9`.
**Execution safety:** disposable PostgreSQL only; no production DB, deployment, bank transfer, invoice write or actual customer funds were touched by these PRs.

## Review method

1. Read owner-approved H1–H6 and OI-01–OI-06 directions in `etc/phase-audits/Phase2G2H_ApprovedBusinessDecisions_20261101.md`; cross-check against post-decision implementation scope and requirements-first traceability. Treat older historical `HOLD` snapshots as dated observations, not current code truth.
2. Reconcile the implemented `RoomChangeDepositDispositionIntentRecord`, `RoomChangeMidMonthCompletionRecord`, H4 staging route guards, persisted Identity lease and Building write fence to AC-H4-01–12. See `acceptance-matrix.md` for criterion-level proof.
3. Inspect GitHub job-level logs (not only workflow titles) for positive authenticated H4 execution, PostgreSQL SQL guards, migration rehearsal and broad security regression. The authenticated H4 test executes **three inner disposition scenarios inside ONE xUnit test**; count it as 1/1, not 3/3 independent HTTP tests.
4. Leave a separate implementation/test PR for a demonstrably missing regression. Do not rewrite working financial intent/completion paths.

## Verified findings

- The source baseline and prior spec-only PR run already model **unpaid top-up**, **future rent credit instruction**, and **manual refund liability** as distinct immutable pending obligations. These are **not** collected money, posted rent credit or a paid bank refund.
- [PR #3 CI at `01ec26c7`](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/pull/3) passed both documented workflows: [H4 PG acceptance 41990528639](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41990528639) and [safe regression 41990528512](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41990528512). These checks are *synthetic PR merge checks*, not an owner signature.
- [PR #4 original test head `eb158f9f`](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/commit/eb158f9ff976cef068ad645f028e75701f10002d) added H4-specific persisted `BootstrapOwner` revoke-through-commit checks, stale-root HTTP denial, inactive-account HTTP denial, and fixture restoration. Its H4 PG job [41990317705](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41990317705) showed real authenticated HTTP **1/1**, migration **1/1**, H2–H6 PostgreSQL **103/103**, zero reported failures for those groups.
- The same original head exposed a **preexisting stale workflow counter**, not a failing Domain test: `buildingmanagement-slice-a-postgres.yml` expected `160/160` for its filter, but actual Domain execution was **162/162 PASS**, so the count guard stopped later steps. [Run 41990317775](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41990317775). PR #4 subsequent commit `907c7009` updates only the exact count expectation and obsolete workflow labels to 162 while preserving the filter, fail-closed validation and 103 PG threshold. This new head must independently PASS all workflows.
- A separate [Windows regression run at the original PR #4 head](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/41990317815) succeeded. It does **not** certify a later commit without that commit's own check.
- The H4 scenario also requires an independent D03 financial classifier distinct from the proposer and owner-reviewer, and staff/cross-building H4 unauthorized routes remain negative-tested. T007 nevertheless remains **PARTIAL** until H4-specific Building membership revocation/commit-race evidence is demonstrated.

## Decision/status register

| Area | Current supported status | Unresolved gate |
|---|---|---|
| H4 deferred deposit split and immutable intent | Staging verified for reviewed source SHA and tested routes | No external money settled by intent |
| H4 Root permission lease / disabled Identity | Test implementation on PR #4; full new-SHA CI acceptance pending as of this report | H4-specific membership-race regression still open |
| AC-H4-01–12 | See `acceptance-matrix.md` for mixed STAGING PASS and PARTIAL status | Do not promote every criterion to BUSINESS ACCEPTED |
| D01/D02/D03 | Existing signed fixture and guarded routes are evidenced, not universal policy sign-off | General mid-month effective cutoff/rounding/residual/issued-invoice policy details |
| D04-H4 | HOLD | Independent bank-backed top-up receipt, approved refund payee/execution proof, replay-safe future rent credit consumption |
| D06/H6 | PARTIAL/HOLD | Final accepted signatory matrix and physically validated handover/defects evidence |
| Production release | EXCLUDED | Requires separate authorization; never authorized here |

## Convergence decision

**Do not duplicate the three implemented H4 pending-obligation writers.** The next bounded financial implementation, *only after owner-approved D04-H4 settlement details*, must create separately tested receipts/credit postings/refund confirmations with independent approval, money provenance, unique bank reference/idempotency, concurrent replay denial, rollback and immutable journal. No documentation or provisional approved direction is such proof.

Retain PR #3 and PR #4 as reviewable drafts. Require final-head CI on the implementation/test PR and an explicit human business-acceptance record before widening the execution lane. All production changes remain excluded.

**Source/CI limitation:** this report relies on GitHub source and job-log observations; it does not claim to independently validate the raw archived TRX ZIP/XML or actual signed physical/bank documents.
