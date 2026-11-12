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
- **Contract compatibility risk identified:** `TenantDto` currently inherits legacy `Abp.Application.Services.Dto.AuditedEntityDto<Guid>`, while `Tenant` inherits modern `Volo.Abp.Domain.Entities.Auditing.FullAuditedAggregateRoot<Guid>`. The four Mapperly RMG012 warnings involve mismatched `CreatorUserId`/`LastModifierUserId` targets, not just cosmetic formatting. Also the Domain.Shared project references both `Abp` 10.5.0 and `Volo.Abp.*` 10.6.1. Do NOT silence these warnings by changing DTO inheritance without reviewing serialized response compatibility and client consumers. Address this during Slice B with contract tests.

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

## Slice C — first P0 containment: default-deny unscoped utility/finance APIs

The original host MVC convention denied legacy Contracts/Tenants/
Reservations/MoveOuts/Payments, but **UtilityRate, UtilityMeter and
MeterReading application-service controllers remained authentication-only**.
That is not safe for a multi-building operator because a logged-in actor
could read global utility data or modify another building's rates. In the
same category, preview/fee/invoice application services could silently
become remotely discoverable on later framework changes. The host
convention now denies **every conventional application-service controller
from BuildingManagement.Application except the three reviewed services**
BuildingAppService, FloorAppService, RoomAppService. This is future
method/service default-deny, not just a list of 26 current actions.
Existing isolated host-owned scoped controllers remain independent of
this filter; the old unscoped services are not unlocked by staging.

AUTHMODEL07 explicitly covers three Slice C utility service classes,
legacy Payment and representative preview/invoice app services, plus
the unchanged 15 reviewed CRUD entry points. The real PostgreSQL
authenticated host test discovers the actual three utility list routes
and rate-create route and rejects both a designated BootstrapOwner and
a building-scoped operator with **403**, before any utility rate insert.
No production environment or real database is affected.

**Slice C STATUS: SECURE CONTAINMENT ONLY / remaining utility and finance
scoped functionality NOT YET IMPLEMENTED.** The next cohesive slice is
dedicated Utilities.Read/Manage and Payments.Read/Manage permission
definitions, server-owned resource-to-building joins and PostgreSQL
concurrency/rollback tests, with explicit host staging adapters. Invoice
revision/rebuild/issue/debt legacy routes remain disabled. Containment
does not count as delivery of all 26 utility/financial methods.

### Slice C isolated UtilityRate CRUD — actual scoped vertical

The first functional Slice C group exposes five staging-only rate endpoints
at \`/api/slice-c-staging/utility-rates\`, while **legacy global utility
controllers remain DENY ALL**. Each read requires a live, persisted
\`BuildingManagement.Utilities.Read\` ABP user/role permission under the
commit-bound Identity lease, and SQL applies current building grants *before*
paging and projection. The response omits private Notes. Each write instead
requires \`Utilities.Manage\`, fences a live building grant through its
Building database commit, locks the Building row FOR UPDATE and locks an
existing UtilityRate row afterwards. The old app service remains the single
authority for overlap, precision and period validation. All same-building
writes serialize on the Building row, preventing two simultaneous overlapping
rates from both passing a non-atomic AnyAsync check. UPDATE cannot transfer
the rate to a different building, even if the root can manage both.
DELETE soft-deletes through the original validated service. Neither read nor
manage permission is implied by Contracts.Manage or Buildings.Manage.

The actual authenticated PostgreSQL HTTP test exercises A/B list and direct
ID isolation, cross-building write refusal, transfer refusal, successful
create/update/delete, overlap refusal, non-PII response and continued
denial of the global conventional controller. The rate CRUD adapter is
registered **only** under the existing two-isolated-database staging opt-in.
Production is not enabled. UtilityMeter, MeterReading and Payment scoped
CRUD remain to be delivered in follow-up Slice C batches.


### Slice C isolated MeterReading 4-method vertical — staging only

Four independent scoped HTTP routes under
`/api/slice-c-staging/meter-readings` implement GET list, GET by ID,
POST create and PUT latest reading. The original global
`MeterReadingAppService` conventional controller stays **DENY ALL**.
The GET queries join physical meter -> room -> floor -> building and
filter active, persisted per-building grant **before** ordering, paging
or projection. Payloads expose reading evidence and resolved BuildingId
but no private Notes or audit/actor metadata. Each transaction holds the
separately persisted `Utilities.Read` or `Utilities.Manage` ABP Identity
lease through Building UoW completion. POST and PUT resolve ownership
from the database rather than the request, acquire a live building-grant
write fence and then lock Building -> physical meter -> optional reading.
The original application service remains the authority for monotone
usage, date, latest-month-only edits and replacement frozen history.

The PostgreSQL authenticated HTTP acceptance fixture now provisions two
separate physical meters under dedicated rooms, verifies cross-building
read/write 403, own create/list/detail/update, duplicate-period failure,
private-note omission and continued legacy-controller denial. The new
host adapters are registered only on the two-isolated-database staging
opt-in; both Identity and PostgreSQL CI path filters include their files.

**Status:** Code and integration probes committed; CI result must be
checked at the final SHA before promoting from REVIEW to PASS. Meter
lifecycle/replacement, Payment APIs, authenticated negative race testing,
and comprehensive Slice C acceptance are still outstanding. No
production or main-branch deployment is authorized.


### Slice C isolated UtilityMeter 9-method vertical — staging only

All nine meter/replacement methods are now represented by the dedicated
`/api/slice-c-staging/utility-meters` host controller. Read/list and
replacement-history queries join meter/replacement -> room -> floor ->
building and filter live building grants before projecting. Room-specific
history refuses even an EMPTY foreign room. The returned records exclude
private Notes and replacement Reason. Creating/updating/deactivating/deleting
a meter or replacing it requires the separate persisted Utilities.Manage
permission plus a fenced live building grant. In the *same* Building
transaction it locks building -> room -> existing physical meter before
delegating to the original application service. Its original soft-deleted
reading checks, replacement atomicity, immutable month history and
integrity/uniqueness constraints stay authoritative.

The PostgreSQL authenticated HTTP fixture exercises the real meter routes
on two buildings: foreign create/read/update/replace/delete refusal;
authorized create/list/detail; replacement invalid-closing-index rollback;
successful atomic replacement; read-only replacement history with no private
metadata; old monthly history preservation and post-replacement freeze;
deactivate newly installed meter, reject deletion of replacement-linked
meter, update and delete unbilled standalone meter, and retain legacy API
DENY ALL. The same fixture now creates the physical meters through the
scoped HTTP API rather than directly inserting test records, and then
exercises four scoped MeterReading routes against them.

**Review gate:** verify the final GitHub Actions Identity/PostgreSQL/Full
regression runs after these changes. A green historical workflow at an
earlier commit is not enough. Payment scope and cross-database revocation
race tests remain outstanding. No production/main deployment.
