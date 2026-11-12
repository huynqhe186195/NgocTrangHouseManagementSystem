# Phase 2G.3A.9 — Building-scoped authorization prerequisite (design-only)

**Date:** 2026-10-26  
**Branch:** `wip/phase-2g2`  
**Status:** CODE STAGED / NEW TESTS NOT YET RUN / NOT AUTHORIZATION APPROVAL

## 1. User-confirmed baseline

- `NgocTrangHouseManagementSystem.HttpApi.Host.Tests`: **13/13 PASS**, including `AUTHMODEL01–06`, `APIHTTP01–02`, and `APIAUTO01–05`.
- Build during that test: **9 warnings**; the detailed causes of these nine were not provided with that run.
- `etc/scripts/check-invoice-revision-no-write-gate.ps1`: **PASS** (static source check only).
- `git status --short` showed one unchanged, untracked review SQL: `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`.

**13/13 proves only the isolated module-host anonymous/authentication boundary, not an authenticated staff member's permissions over a particular building.** AppServices such as `BuildingAppService.GetListAsync`, `RoomAppService.GetListAsync`, and `ContractAppService.GetListAsync` do not yet filter results by operator-to-building membership. This is a release blocker for multi-building use.

## 2. Staged read-only server scope gate

New `BuildingManagement.Application/Security/BuildingBusinessScopeDesignGate.cs` contains:

- Eight operation capabilities: read/manage for Buildings, Floors, Rooms, Contracts. Eight ABP permission **definitions**, no role or user assignment, registered in `BuildingManagementPermissions` and `BuildingManagementPermissionDefinitionProvider`.
- `AbpBuildingBusinessPrincipalReader` reads the **real ABP `ICurrentUser.Id`** and maps a fixed operation enum to a **specific** `IPermissionChecker` name (never accepts a client-provided arbitrary permission string).
- `EfBuildingBusinessResourceScopeReader` resolves **actual EF repository** relationships for Building; Floor→Building; Room→Floor→Building; Contract→Room→Floor→Building. Missing, empty and normally soft-deleted resources fail closed. Existing ABP data/tenant filters remain active.
- `IBuildingOperatorGrantReader` defaults to **`DenyAllBuildingOperatorGrantReader`**: every operator has zero active buildings. **There is no membership storage, no data migration, no permission grants and no admin bypass.**
- `BuildingBusinessScopeDesignGate.AssessDesignOnlyAsync` requires a matching resource/capability combination, logged-in server actor, explicit operation permission, matching real resource ID, nonempty resolved building and current matching building grant. The only positive status is **`EligibleForDesignOnly`**. No token, cached authorization or external API results.
- The gate is **not wired into existing CRUD AppServices**, deliberately avoiding a false claim of access-control coverage or an unreviewed breaking change. The existing invoice-only `DenyAllInvoiceDraftRevisionBuildingGrants` remains independent and unchanged.

Do **not** use a positive design-only result to authorize an actual write. Production actions must revalidate under their lock/transaction. For collection endpoints, filter **database queries** by authorized building IDs before counts/paging, not post-filter an in-memory list.

## 3. New test gates — not locally executed

| Project | Filter | Expected count | Scope |
| --- | --- | --- | --- |
| `BuildingManagement.Application.Tests` | `FullyQualifiedName~BuildingBusinessScopeDesignGate_Tests` | **21** | 13 facts + 8 theory rows: default deny, resource/capability matching, missing permission/actor, invalid/forged IDs, different building, revocation between calls, read-vs-manage separation, 8 distinct permission names |
| `BuildingManagement.EntityFrameworkCore.Tests` | `FullyQualifiedName~BuildingBusinessScopeEfIntegration_Tests` | **5** | Actual repository-backed SQLite building/floor/room/contract graph, soft-delete filtering, default DI deny-all, simulated cross-building rejection |

These test fixtures use fakes for user membership and **isolated SQLite** for repository hierarchy. They **do not** demonstrate production operator membership, revocation locking, tenant-specific role enforcement or PostgreSQL runtime privileges.

### Run from repository root

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~BuildingBusinessScopeDesignGate_Tests" --logger "console;verbosity=normal"
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingBusinessScopeEfIntegration_Tests" --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

### Future implementation gates — OPEN

1. Review operator↔building membership model against the host's actual ABP identity, role, tenant and building lifecycle; auditable provision/revoke, least privilege and ownership of newly created buildings.
2. Implement and test server-owned membership repository on a **throwaway** SQLite/PostgreSQL test environment, not app data. Prove revocation and soft-deleted building/disabled operator behavior.
3. Integrate operation-specific permission + building scope checks **into every relevant AppService action**, including nested writes, tenant lists and financial actions. Do not assume the eight proposed permissions cover Payment/Deposit/Invoice capabilities.
4. Add negative authenticated HTTP tests (403/not-found policy) and cross-building list/count/pagination leakage tests, including guessed GUIDs and role changes.
5. Verify the real configured host's endpoint inventory, anonymous exceptions, and separate runtime DB role.
6. Only after those gates, consider controlled approval of additional invoice lifecycle capabilities. Invoice rebuild/issue/post/debt remains remote-disabled and out of scope.

**Safety:** No migration files, DbMigrator, roles, PostgreSQL data or invoice mutations were modified by this design-only increment. Preserve the untracked generated SQL review file.
