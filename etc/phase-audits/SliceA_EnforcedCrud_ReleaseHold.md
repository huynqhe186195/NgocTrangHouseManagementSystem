# Slice A — enforced Building/Floor/Room CRUD authorization (WIP checkpoint)

**Date:** 2026-10-27  
**Branch:** `wip/phase-2g2`  
**Status:** REAL AppService enforcement now staged; tests NEW/NOT LOCALLY VERIFIED; no release or DB migration approval.

## Baseline before this update

The user ran `etc/scripts/verify-2g-security-batch.ps1 -Mode Full` on the preceding commit; console ended:

- `=== QUALITY GATE PASS: ALL REQUESTED TEST RUNS SUCCEEDED ===`.
- Host suite **15/15 PASS**; overall batch included Application + SQLite + Host but only the final project count was pasted.
- Source-only building-scope and invoice NO-WRITE gates are included in that batch.
- Last local `git status --short` showed only the preserved, untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`.
- The new code and tests described below were NOT covered by that prior PASS.

## What is different now — actual enforced Application operations

This is the **first change that uses the access policy inside the existing business AppServices**, rather than isolated design-only probes.

`BuildingCrudAccessPolicy` (`BuildingManagement.Application/Security/BuildingCrudAccessPolicy.cs`) is a real, nonremote, transient interface-backed authorization dependency.

Every permitted direct read/write needs ALL of the following, checked server-side:

1. Operation-specific fixed capability: Building, Floor or Room **Read** vs **Manage** (no arbitrary permission name).
2. ABP `ICurrentUser` **authenticated** and nonempty user GUID via `AbpBuildingBusinessPrincipalReader`, plus `IPermissionChecker` grant for that capability.
3. Account is still active via `IBuildingOperatorActiveAccountReader`.
4. `IBuildingOperatorGrantReader` supplies one or more currently assigned building IDs from the server; empty/invalid/revoked input denies.
5. A direct resource's true building ID is resolved by `IBuildingBusinessResourceScopeReader` through actual EF repository relationships; guessed/cross-building/soft-deleted resource IDs deny.
6. Operation/resource matrix forbids checking unrelated resources with a mismatched capability. Floor creation can check Building with ManageFloor; Room creation can check Floor with ManageRoom.

Unauthorized operation results in `Volo.Abp.Authorization.AbpAuthorizationException`; the caller never reaches mutation code. The policy is marked `[RemoteService(IsEnabled=false)]`, so it must not appear as a generated HTTP route.

**Implemented in all 15 publicly exposed methods:**

| Resource | Create | Get | GetList | Update | Delete |
| --- | --- | --- | --- | --- | --- |
| Building | **Blocked pending owner bootstrap** | ReadBuilding + scope | ReadBuilding + filtered SQL | ManageBuilding + scope | ManageBuilding + scope |
| Floor | ManageFloor + Building scope | ReadFloor + scope | ReadFloor + JOIN Building scoped SQL | ManageFloor + scope | ManageFloor + scope |
| Room | ManageRoom + Floor→Building scope | ReadRoom + scope | ReadRoom + Room→Floor→Building SQL | ManageRoom + scope | ManageRoom + scope |

**Important operational impact:** Production DI currently has `DenyAllBuildingOperatorActiveAccountReader` and `DenyAllBuildingOperatorGrantReader`. There is NO approved owner-bootstrap workflow and NO deployed membership table. The 15 AppService operations will therefore **fail closed (deny users)** on this development branch, rather than giving every authenticated user unrestricted building access. **Do not merge/deploy as an operational release** without finishing the authorized first-owner provisioning and membership lifecycle. There is no superuser bypass.

### Existing business test compatibility

Contract and utility-meter suites legitimately test the Room Delete/Get domain consequences. They previously used `IRoomAppService` directly and would be blocked by the new fail-closed policy.

A **TEST-ASSEMBLY ONLY** `LegacyRoomBusinessFixturePolicy` is registered solely in the isolated **SQLite test module**. It denies everything by default, and permits only Room Read/Manage inside `using var roomFixtureAccess = LegacyRoomBusinessFixturePolicy.AllowRoomBusinessAssertions()`. Exactly **5 legacy business test methods** explicitly opt in, keeping the established active-contract/meter-history/delete soft-state assertions meaningful. It cannot be used in the running application; no production project references it and no global full-authorization bypass was added. Security policy tests instantiate the **REAL policy**, not this fixture.

## New test coverage — NOT yet locally run

- `BuildingCrudAccessPolicy_Tests`: target **22 xUnit cases**, covering default deny, permission separation, cross-building forged GUIDs, read/manage separation, revocation, invalid operation, parent child-creation and orphan-building create blockage.
- `BuildingFloorRoomCrudDefaultDeny_Tests`: **3 SQLite tests** invoking **all 15 real AppService CRUD entry points** under default-deny test conditions; they must reject before returning/mutating.
- `BuildingCrudAccessPolicySqlite_Tests`: **4 SQLite tests** invoking the **actual enforcement policy with actual repository-backed building/floor/room graph** (simulated actor/account/grant only) to prove A allowed and B denied, child parent scope, soft delete and bootstrap block.
- `APIAUTO08`: **1 new HTTP Host model test**, verifying discovery/authentication metadata on all 15 endpoints and proving the CRUD security policy is not an HTTP controller. Full host suite target rises from **15/15 to 16/16**.
- `check-building-scope-design-only-gate.ps1` now verifies source guard coverage: all 15 methods reference the enforcement policy as intended; prevents accidental return to unscoped CRUD.

**No authenticated end-to-end HTTP with separate A/B users has been passed yet.** The current isolated host lacks a real EF/Identity membership DB, so claiming it proves authenticated IDOR isolation would be incorrect. Even after these new tests pass, owner bootstrap, real active grant provider, transactional mutation/revocation handling and finance/contract/tenant endpoints remain P0 blockers.

## Single batch command

From the repo root on the correct branch:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
```

The `Full` batch also reruns the previously unconfirmed `READSQL01–08`, old business tests, all new CRUD tests, HTTP Host inventory and both source safety gates. **Any compiler or test failure must stop release, be fixed, and followed by a fresh Full PASS.**

Do not turn on live schema migrations, PostgreSQL integration against customer data, grant roles or invoice issue/debt endpoints to make this batch pass. Preserve the local untracked invoice-revision review SQL without staging or editing.

## Remaining Slice A acceptance gate

- Approved, audited **first owner creation** (atomic new Building + first scoped grant, no client user-ID trust, no orphan possible).
- Reviewed PostgreSQL membership migration / rollback / indexes, tested ONLY on disposable sandbox data first.
- Real account-active + locked status, role-level permissions, issuer/revoker authority, two-admin recovery and tenant policy.
- Authenticated HTTP 403/404 for user A attempting another building B, on read, create child, update, delete and lists, including permissions revoked concurrently. Negative tests must use deliberately authenticated test principals, never `AddAlwaysAllowAuthorization`.
- Lock/recheck mechanism for revocation racing with writes.
- Remaining Contract/Tenant/Payment/Utility/Invoice APIs must receive equivalent guards in later slices before any production rollout.

**Release decision:** BLOCKED. This checkpoint improves the actual CRUD entry-point boundary but does not constitute a usable finished Slice A.
