# Phase 2G.3A.14 — Server-composed scoped list/read design (no public activation)

**Date:** 2026-10-27  
**Branch:** `wip/phase-2g2`  
**Status:** STAGED / NEW TESTS NOT YET EXECUTED LOCALLY / NO CRUD AUTHORIZATION APPROVAL

## User-confirmed checkpoint

- Phase 2G.3A.13 `LISTSCOPE01–07`: **7/7 PASS** on isolated ABP SQLite.
- `check-building-scope-design-only-gate.ps1`: **PASS** before this increment.
- `check-invoice-revision-no-write-gate.ps1`: **PASS**.
- Last user `git status --short`: one untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`, which must remain unmodified.
- The prior EF test run had 4 Mapperly `RMG012` warnings. No evidence was provided of any production permission/role grants or database-role security.

## Problem being addressed

Four `BuildingBusinessScopedCollectionQueries` methods can filter properly in SQL, but their `trustedBuildingIds` parameter is only safe when supplied by a real server-side authenticated grant resolver. An AppService that simply forwards client-supplied building IDs is still vulnerable to IDOR and list/count leakage.

Also, Application cannot directly reference EntityFrameworkCore without violating module dependency direction. A scoped list-store interface needs to sit below Application, with the EF implementation behind the interface.

## Changes (internal and design-only)

1. `BuildingManagement.Domain/Security/IBuildingScopedCollectionStore.cs`
   - Defines `BuildingScopedCollectionKind` (Building/Floor/Room/Contract) and `BuildingScopedListPage` with total count and resource IDs.
   - Defines an **internal service contract** (not `IApplicationService`, no remote controller, no DTO endpoint).
2. `BuildingManagement.EntityFrameworkCore/Security/EfBuildingScopedCollectionStore.cs`
   - Implements the interface using existing ABP repositories, SQL-side resource→building joins, soft-delete checks, `CountAsync`, and deterministic GUID ordering before `Skip`/`Take`.
   - Enforces `skip >= 0` and `1 <= take <= 100`. Invalid kinds fail closed.
   - **The count and page are two SQL statements**; this design test does not guarantee a repeatable-read snapshot. A later production implementation must define transaction isolation and revocation consistency before use.
3. `BuildingManagement.Application/Security/BuildingScopedReadDesignGate.cs`
   - **`[RemoteService(IsEnabled = false)]`**, not an application CRUD service.
   - Composes **`IBuildingBusinessPrincipalReader`** (ABP server identity + operation-specific permission), **`IBuildingOperatorActiveAccountReader`**, **`IBuildingOperatorGrantReader`** (active server grants) and the EF-backed store.
   - Checks identity and permission before account, account before building grants, and denies before touching the store when there is no valid scope.
   - Only positive status is `EligibleForDesignOnly`; it is not an authorization token and cannot authorize writes/endpoint activation.
4. Existing public `BuildingAppService`, `FloorAppService`, `RoomAppService`, `ContractAppService` were not changed. The real application account/grant providers stay **DENY ALL**. No new public route or migration.
5. Updated `check-building-scope-design-only-gate.ps1` to stop if the candidate scoped list gate ceases to be remote-disabled or gets referenced in those four CRUD classes before a full review.

## New verification gates — ALL PENDING LOCAL EXECUTION

| Test suite | Target | What it demonstrates |
| --- | ---: | --- |
| `BuildingScopedReadDesignGate_Tests` (Application) | **14/14** | Unauthorized, disabled, ungranted, invalid-input requests never call the data store; read permissions selected per resource; fresh grant revoke is observed; no client-generated building IDs |
| `BuildingScopedCollectionStore_Tests` (EntityFrameworkCore SQLite) | **5/5** | EF DI registration, all four collections, absent grants, SQL scope before count/paging, invalid inputs, soft-deleted building |
| `NgocTrangHouseManagementSystem.HttpApi.Host.Tests` (full suite) | **15/15** | Includes new `APIAUTO07`: the scoped-read/delegation gates are NOT exposed as ABP auto-controllers, and grant DI remains deny-all |
| Source-only design gate | **PASS expected** | Membership remains unmapped, identity adapter unregistered, current reader remains internal |
| Source-only invoice NO-WRITE gate | **PASS expected** | Draft storage stays remotely disabled and non-mutating |

Neither SQLite fakes nor a minimal ABP host can verify that authenticated users cannot access someone else's real building through existing business APIs. **Authenticated cross-building CRUD HTTP tests remain unimplemented and must block release.**

## Commands from repository root

```powershell
git pull --ff-only origin wip/phase-2g2

dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~BuildingScopedReadDesignGate_Tests" --logger "console;verbosity=normal"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingScopedCollectionStore_Tests" --logger "console;verbosity=normal"

dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --logger "console;verbosity=normal"

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-building-scope-design-only-gate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

If tests fail, collect exact compiler/test diagnostics. Do not enable the grant schema in the running DbContext, run DbMigrator, use the real PostgreSQL database, or grant actual user permissions in order to make them pass.

## Critical next gates

- Review the real ABP Identity adapter in an *isolated* Identity store, including missing, disabled, locked, revoked and tenant-mismatched users.
- Approve bootstrap/ownership and privileged grant/revoke policy and a PostgreSQL membership migration/DB role strategy.
- Wire real membership/operation enforcement into all CRUD/read/list/financial services with negative authenticated 403/404 HTTP tests and scope-before-pagination checks. No blanket administrator exception.
- Recheck permission, account, grant, FK relationship and resource state inside write transactions; define lock/revocation concurrency protocol. No positive preflight may act as a write token.
- Keep the invoice draft/rebuild/issue/debt endpoints disabled and preserve the local SQL review evidence.
