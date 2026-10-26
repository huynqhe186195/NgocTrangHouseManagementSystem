# Phase 2G.3A.12 — ABP Identity and privileged operator delegation boundary

**Date:** 2026-10-26  
**Branch:** `wip/phase-2g2`  
**Status:** DESIGN-ONLY / NEW TESTS NOT YET VERIFIED / NO LIVE PERMISSION CHANGES

## 1. User-confirmed checkpoints

- Phase 2G.3A.11: **MEMBER01–12 = 12/12 PASS**, `GRANTQUERY01–09` + `MEMBERDB01–06` = **15/15 PASS**, all isolated.
- Source `check-invoice-revision-no-write-gate.ps1`: **PASS**; it is not a PostgreSQL runtime role/permission check.
- Git showed only the preserved untracked SQL review file `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`.
- Earlier stage: 13/13 isolated module HTTP authentication tests, 26/26 scope tests and 17/17 grant aggregate/SQLite tests passed at their corresponding commits. Do not confuse those verified tests with a deployed authorization policy.
- Application tests build previously had 4 known `RMG012` Mapperly warnings; EF tests build clean.

## 2. Why a separate grant/revoke boundary is needed

A generic `Buildings.Manage` permission must never imply authority to appoint administrators or change a different user's building scope. An authenticated user must not be able to add their own grant, assign another building, or use an inactive user account as a newly privileged operator.

The host's Identity data is in the ABP Identity domain/DbContext and the BuildingManagement schema lives separately. Business module authentication must not trust actor/recipient status submitted in a DTO. `MultiTenancyConsts.IsEnabled = false` currently, so this design must not claim tenant isolation.

## 3. Implemented staging

- Added `BuildingManagementPermissions.Buildings.AssignOperators` and `RevokeOperators` and registered both in the ABP permission definition provider. **Permission definition != permission grant.** Neither is assigned to any role or user.
- Added localized labels for all currently defined BuildingManagement building/floor/room/contract/read/manage/assign/revoke permission keys in English and Vietnamese.
- `AbpBuildingOperatorChangeIssuerReader`: reads `ICurrentUser.IsAuthenticated` / `ICurrentUser.Id` and checks the exact ABP permission for the requested enum; no arbitrary permission string or client-reported issuer is trusted.
- `BuildingOperatorChangeDesignGate`: fail-closed, read-only assessment. It requires a non-empty building/recipient, authenticated issuer with **dedicated permission**, forbids self-assignment and self-revocation, checks issuer active, recipient active **for Assign only**, resolves a real active Building from the server-side resource reader and rereads issuer's current building memberships. The only positive result is `EligibleForDesignOnly`, NEVER an authorization token or approval to write.
- Revoking a disabled or locked target is necessary to purge stale grants. A future real revoke command must first resolve the actual unrevoked matching grant row from the database. This preflight alone does not establish that the target had such a row.
- Default `IBuildingOperatorActiveAccountReader` provider is **DenyAllBuildingOperatorActiveAccountReader**. Default building membership provider also continues to deny all. The actual `BuildingBusinessScopeDesignGate` and all business CRUD services are unchanged and NOT protected by this new design gate.
- `AbpIdentityBuildingOperatorAccountProbe` in the **HTTP host** is an **unregistered candidate adapter**: fetches `IdentityUser` through `IIdentityUserRepository`, rejects missing/inactive user and uses `IdentityUserManager.IsLockedOutAsync`. It explicitly does **not** register itself as `IBuildingOperatorActiveAccountReader`, so it does not displace deny-all in production. This bridge must be build-verified locally; it is not evidence of a tested real ABP Identity database.
- No membership schema mapping, grant creation API, revocation API, bootstrap-owner API, DbMigrator or production authorization change is introduced.

## 4. New test gates, awaiting local .NET execution

**BuildingManagement.Application.Tests / BuildingOperatorChangeDesignGate_Tests**

**17 expected test cases** (15 [Fact] and one [Theory] with two enum cases), including:

- default-deny identity and building grant providers;
- simulated valid contexts for separate assignment and revocation operations, still design-only;
- anonymous or missing-privilege issuer denied;
- self-assign and self-revoke denied;
- inactive issuer denied; inactive recipient cannot be assigned, but can be considered for **revocation** without an active-account check;
- missing/cross-building issuer scope denied, mismatched resource graph denied;
- invalid operations and missing GUIDs denied before unnecessary reads;
- fresh scope revoke denies a previously positive design preflight;
- permission name separation and presence.

**Host project compile** must verify the unregistered ABP identity probe resolves APIs correctly against this solution's ABP 10.6.1. The probe is not injected into running services.

From the root of `NgocTrangHouseManagementSystem`:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~BuildingOperatorChangeDesignGate_Tests" --logger "console;verbosity=normal"
dotnet build .\src\NgocTrangHouseManagementSystem.HttpApi.Host\NgocTrangHouseManagementSystem.HttpApi.Host.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

If build/test fails, capture the precise error instead of modifying production DB settings or granting rights. No .NET runtime is available in the assistant's code container, so tests are **not locally verified by the assistant**.

## 5. Remaining critical release blockers

1. Decide who approves initial ownership/bootstrap, transfers ownership, grants/revokes other users and rescues a building without an active admin; no broad admin shortcut/self-grant.
2. Approve and test effective account state from the real ABP Identity store, role permission grants, lockout/disabled revocation, current tenant behavior.
3. Review operator grant persistence and PostgreSQL migration; retain history, protect concurrency, avoid owner privilege and audit the issuer. The running `BuildingManagementDbContext` STILL has **no grant table mapping**.
4. Register live identity account reader and an EF grant reader **only after** operation permissions and building scope are enforced in every relevant business AppService. Do not swap the current deny-all provider prematurely.
5. Build authenticated-user 403/404 and cross-building IDOR/list/count/paging negative tests; ensure changing grant state invalidates later writes under an agreed DB lock/transaction protocol.
6. Keep invoice draft revision, invoice issuance, debt posting and the already-proven NO-WRITE constraints intact. Verify PostgreSQL non-owner runtime role separately.

**Bottom line:** The newest implementation produces only design-time decision signals. No real operational action may treat `EligibleForDesignOnly` as permission to mutate business data.
