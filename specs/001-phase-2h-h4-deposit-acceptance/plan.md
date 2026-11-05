# Implementation / Verification Plan — Feature 001

**Current phase:** audit-first; no financial application code changes as part of Spec Kit adoption.  
**Baseline:** `a9519996c06d42db880cec4a741036a2e520e460`.

## Current architecture to preserve

- **.NET 10 / ABP layered DDD monolith** with module paths under `modules/buildingmanagement/src`.
- **Domain:** `BuildingManagement.Domain/RoomChanges/`, notably `RoomChangeDepositDispositionIntentRecord.cs` and `RoomChangeMidMonthCompletionRecord.cs`.
- **Tests:** `modules/buildingmanagement/test/BuildingManagement.Domain.Tests/RoomChanges/`, `BuildingManagement.PostgreSql.Tests`, and `test/NgocTrangHouseManagementSystem.HttpApi.Host.Tests`.
- **Independent acceptance workflows:** `.github/workflows/phase-2h-h4-disposable-acceptance.yml` and `buildingmanagement-safe-regression.yml`.
- **Local gate scripts:** `etc/scripts/verify-phase-2h-h4-candidate.ps1`, `verify-phase-2h-financial-foundations.ps1`, `verify-2g-security-batch.ps1`.

Existing exact-SHA CI at the baseline reports `success` for both H4 disposable PostgreSQL and safe Windows regression. The CI workflow source asserts 103/103 selected PG tests and the Windows workflow asserts 165/165 selected H4 Domain tests; these counts are **workflow source expectations**, and run logs/TRX must be inspected to certify each group.

## Phase 0 — Evidence and gap mapping (no code)

1. Confirm the source branch's current HEAD; update the baseline if new commits land before implementation.
2. Read H4 Domain and SQL commit validators, authenticated HTTP tests and test filters, not only prose summaries.
3. Inspect the two exact-SHA CI run logs and artifacts; reconcile assertions to AC-H4-01–12.
4. Compare implemented H4 financial intent vs approved **business meaning** of top-up, credit and refund. Distinguish actual disbursement from a liability.
5. Produce a per-AC table: implementation location, test ID, proof type, current status, missing evidence and decision owner. Only mark PASS for evidence of the same SHA.

## Phase 1 — Targeted remediation (only if a gap is demonstrated)

- Prefer existing domain invariants, audited ledgers and serializable PostgreSQL constraints.
- Add tests that fail on the specific uncovered scenario before changing the implementation.
- Extend the authenticated HTTP/PG and exact-SHA gates together; do not weaken existing filters, counts, target allowlists or deny-all controls to attain green status.
- Any schema change: peer-review EF migration and check SQL-bypass/rollback on disposable PostgreSQL; never touch production.

## Phase 2 — Acceptance/convergence

- Run selected Domain, Application, EF, authenticated HTTP and disposable PostgreSQL tests on the **implementation branch** with exact SHA.
- The existing local one-shot H4 script checks out branch name `wip/phase-2g2` and requires its remote HEAD. It is **not safe to run unchanged** on `chore/adopt-spec-kit-20261105`. Use the PR's CI/test setup or a separately reviewed, branch-aware, equally strict runner.
- Review tests and spec for contradictory semantics, false success flags, cross-building, replay, duplicate money disposition, and rollback.
- Prepare a documented acceptance report with explicit residual HOLD items. Do not automatically merge or deploy.

## Risk and rollback

- Spec-only PR: revert documentation commit if necessary; it does not alter database or execute application code.
- Subsequent code PR: preserve a rollback plan for each change; never delete historical financial data to accommodate the spec.
- Never represent an artifact check or code review as authenticated production security acceptance.
