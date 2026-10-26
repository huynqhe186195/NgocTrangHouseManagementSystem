# NgocTrangHouseManagementSystem — Accelerated delivery plan / security-first

**Prepared:** 2026-10-27  
**Development branch:** `wip/phase-2g2`  
**Decision:** Stop delivering a new numbered "design-only" subphase for each small test set. Work in complete, reviewable **vertical slices** with a single CI/test gate per delivery batch.

## 1. Verified project state and evidence

User-local results confirmed:

- Phase 2G.3A.14: `READGATE` 14/14, `STOREPAGE` 5/5 PASS.
- Phase 2G.3A.13: `LISTSCOPE` 7/7 PASS.
- Host isolated ABP authentication suite: **15/15 PASS**, including `APIAUTO07` (verified in the latest user message).
- Building scope source design-only gate: PASS.
- Invoice source no-write gate: PASS.
- Current local working tree reported one untracked review SQL script: `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`; preserve it.
- `READSQL01–08` on the newer Stage 2G.3A.15 has **NOT** been confirmed yet, although the source/test exists.
- Known nonblocking warnings: four Tenant Mapperly RMG012 and five host warnings (two OpenIddict CS8604, two Swagger CS8602, one host CS0162).

**Evidence limitation:** all current positive resource-scope and grant results are design-only and/or SQLite. The production CRUD endpoints remain accessible to *any authenticated caller who passes only the host authentication convention*, with no proven per-building permission restriction. Never equate passing 15 HTTP anonymous tests with authorization of signed-in users.

## 2. Current public service contract inventory (source-level)

Inspected `BuildingManagement.Application.Contracts` on `wip/phase-2g2`. **74 declared async operations across 12 application-service interfaces**:

| Service interface | Methods | Delivery group |
| --- | ---: | --- |
| IBuildingAppService | 5 | Slice A |
| IFloorAppService | 5 | Slice A |
| IRoomAppService | 5 | Slice A |
| IContractAppService | 13 | Slice B |
| ITenantAppService | 5 | Slice B |
| IRoomReservationAppService | 4 | Slice B |
| IMoveOutRequestAppService | 7 | Slice B |
| IPaymentAppService | 8 | Slice C |
| IUtilityRateAppService | 5 | Slice C |
| IMeterReadingAppService | 4 | Slice C |
| IUtilityMeterAppService | 9 | Slice C |
| IContractRenewalHoldAppService | 4 | Slice B |
| **Total** | **74** | |

This is a **source interface method count**, not yet the output of a running host endpoint inventory. Some methods may map to multiple verbs/paths; other explicitly authored/sample endpoints and invoice internals are not part of this count.

## 3. New release strategy

### Slice A — Secure building/floor/room operations as a real feature

**Scope:** All 15 public operations of Building, Floor and Room: create, get, list, update, delete. Count only after authorized SQL scope. Ensure a child Floor/Room cannot be assigned to an unrelated building through input DTOs. Require permission **and** current active per-building membership for each request.

**Prerequisites:** Approve the *first owner bootstrap* protocol, how a building gets its first legitimate owner and how an owner can recover access. Without this, a zero-grant default blocks every operator, while a blanket superuser exception leaks all buildings. The previously staged grant entity still has NO production migration.

**Acceptance tests:** authenticated user A can read/update only A, B-only user cannot read/list/modify A, unaffiliated user gets 403/404 (document decision); invalid GUIDs and soft-deleted parents deny; cross-building new-floor/room attempted writes deny before mutation; scope SQL count/paging verified. Verify revocation on a fresh request and on a concurrent write protocol. Run on isolated SQLite and a purpose-created nonproduction PostgreSQL test DB.

**Do not release Slice A partially.** No `featureFlag: false` production bypass path that silently returns to authentication-only access.

### Slice B — Secure rental occupancy lifecycle

**Scope:** Contract (13), Tenant (5), Reservation (4), MoveOut (7), ContractRenewalHold (4): **33 methods**. Child/tenant privacy and signed/ended contract state transitions must respect building ownership; check every indirect method and nested transaction.

**Acceptance:** cross-building negative create/edit/link/sign/cancel/renew/termination tests, no tenant PII leakage, correct room occupancy and rollback when a forbidden nested resource is referenced.

### Slice C — Secure financial and utility operations

**Scope:** Payment (8), UtilityRate (5), MeterReading (4), UtilityMeter (9): **26 methods**. Audit all other invoice, deposit, meter replacement and financial workflows even if not included in the 74 interface methods.

**Acceptance:** tenant/payment linkage cannot cross buildings; read/write and finance rights separated; room meter replacement preserves history; money/meter operations transactionally atomic, idempotency and concurrency tested with actual PostgreSQL constraints. Invoice revision/rebuild/issue/debt remain remote-disabled until dedicated authorization, ledger and migration review pass.

### Slice D — Final hardening and release readiness

- Full configured-host **route/verb/auth/permission/scope inventory** and negative **authenticated** HTTP tests; not just anonymous 401 tests.
- Reviewed real ABP Identity account+lockout tests, role grants, migration/owner audit and second-admin recovery.
- Review migration source, generated SQL, checksum and rollback in an isolated PostgreSQL DB. Test non-owner runtime role, minimum DB privileges and revocation race conditions.
- Confirm backup/restore plan, UI integration contract and release smoke tests. Deploy only when all P0 risks are closed.

**Work-in-progress limit:** ONE slice at a time until it includes code, negative auth tests, integration tests, docs and rollback guidance. Incomplete slices must not merge into the release branch.

## 4. Less manual work: one safe command + CI

Committed `etc/scripts/verify-2g-security-batch.ps1`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Quick
```

Quick runs both existing source-only safety checks and filtered relevant Application/SQLite/Host suites. Full runs **entire** Application, SQLite EF and Host test projects (not PostgreSQL or DbMigrator):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
```

A GitHub Actions workflow now triggers on push and PRs targeting `wip/phase-2g2` and runs the Full suite on an isolated Windows CI runner. It has **read-only repository permission**, no production secrets, no DB migration and no deployment stage. **CI execution status has not been observed and must NOT be reported as PASS before GitHub Actions logs confirm it.**

The workflow tests the PR merge checkout after naming the ephemeral local branch `wip/phase-2g2` so existing source-only branch guards work correctly. This does not change any remote branch outside a normal commit.

## 5. Gates that are not negotiable

1. No production PostgreSQL migration or write without explicit approval, rollback plan and isolated dry run.
2. No unlocking draft invoice rebuild/issue/debt endpoints before permission, resource ownership, ledger and DB role tests pass.
3. Never delete, stage, overwrite, or execute the user's untracked `Phase2G3A7_RevisionLedger_Generated_Review.sql` by accident.
4. Do not use ABP's `AddAlwaysAllowAuthorization` in real HTTP security tests; use explicit test authentication and scoped per-user fixtures.
5. Failed compilation or negative authorization test **stops the batch**. Fix the failing implementation before adding more unrelated mock/design-only tests.
6. The new CI gate does not by itself make CRUD secure; Slice A acceptance must explicitly demonstrate negative signed-in cross-building behavior.

## 6. Execution order immediately following this plan

- Confirm `READSQL01–08` and newly introduced batch/CI compile/run evidence in one verification batch, instead of asking user to run several similar commands.
- Immediately deliver **Slice A as one integrated change set**: reviewed ownership bootstrap and persistence, actual enforcement of the 15 methods, authenticated negative HTTP tests and tested migration dry-run. No more standalone 5-test micro-phases as the main form of progress.
- Then complete B, C and D using the same single-batch gate and explicit pass/fail evidence.

**Current release state:** BLOCKED on authenticated per-building CRUD authorization and missing real operator/owner grants. The correct response is to finish the functionality, not to label isolated positive tests as a production security pass.
