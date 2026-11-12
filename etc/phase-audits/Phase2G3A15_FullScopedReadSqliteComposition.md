# Phase 2G.3A.15 — Full scoped read chain, SQLite-only integration

**Branch:** `wip/phase-2g2`  
**Date:** 2026-10-27  
**Status:** New tests staged; **NOT YET EXECUTED locally**. No production CRUD or DB changes.

## Confirmed user-local evidence on Phase 2G.3A.14

- `BuildingScopedReadDesignGate_Tests`: **14/14 PASS**.
- `BuildingScopedCollectionStore_Tests`: **5/5 PASS**.
- `check-building-scope-design-only-gate.ps1`: **PASS**.
- `check-invoice-revision-no-write-gate.ps1`: **PASS**.
- `git status --short`: one untracked local `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`, untouched.
- Application build reported **4 RMG012 Mapperly warnings**, which did not fail tests.
- The current user log **did not include** the requested updated `NgocTrangHouseManagementSystem.HttpApi.Host.Tests` run (**15/15 target including APIAUTO07**). The earlier **14/14** baseline is confirmed, but APIAUTO07 remains unverified.

## New verification: READSQL01–08

`BuildingScopedReadSqliteComposition_Tests` in the existing `BuildingManagement.EntityFrameworkCore.Tests` suite uses the normal isolated ABP SQLite EF repositories. Fake, server-style principal/account/grant readers are passed to the **real** `BuildingScopedReadDesignGate`, whose read storage is the **real** `EfBuildingScopedCollectionStore`.

- `READSQL01`: a user granted building A only can read/count exactly A's rooms; no building B room.
- `READSQL02`: switching simulated grant from A to B on consecutive reads changes the contract set and count without leaving an old A result.
- `READSQL03`: missing read permission or inactive identity returns Denied before querying memberships (and therefore before the EF scoped store).
- `READSQL04`: revocation (simulated provider returns no current scope) on a new read fails closed.
- `READSQL05`: paging and count are applied to SQL-scoped A-only rows; B cannot leak into either page.
- `READSQL06`: soft-deleting A makes its previously granted room rows invisible.
- `READSQL07`: resolves **real ABP DI default** `IBuildingOperatorActiveAccountReader` and confirms it denies, even with synthetic business data.
- `READSQL08`: resolves **real ABP DI default** `IBuildingOperatorGrantReader` and confirms it remains `DenyAllBuildingOperatorGrantReader`.

**Expected new tests: 8/8, NOT verified.** The fake actor, fake account and simulated grant changes are confined to test memory; no real ABP Identity users, permission assignments or membership records are written. Real ABP SQLite repositories hold synthetic building/floor/room/contract rows for the test only.

## Commands to run from repository root

```powershell
git pull --ff-only origin wip/phase-2g2

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingScopedReadSqliteComposition_Tests" --logger "console;verbosity=normal"

dotnet test .\test\NgocTrangHouseManagementSystem.HttpApi.Host.Tests\NgocTrangHouseManagementSystem.HttpApi.Host.Tests.csproj --logger "console;verbosity=normal"

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-building-scope-design-only-gate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

Targets: **8/8 new SQLite integration cases**, **15/15 HTTP Host cases**. If a case fails, capture the exact compiler/test diagnostic and keep production deployment blocked. Never alter production DB connections or run migration tools to fix tests.

## Major unresolved security gaps — blocking production

1. **No authenticated-user resource isolation in the existing public CRUD AppServices.** Current auth tests verify anonymous 401 only; authenticated IDOR is still a concern.
2. No approved, deployed owner bootstrap / administrator grant/revoke policy, actual operator↔building membership persistence, or active identity verification linked to isolated Identity tests.
3. The design-only positive outcome `EligibleForDesignOnly` does NOT authorize real read/write, and the read gate is `RemoteService(IsEnabled=false)`.
4. SQL count and page currently execute in separate reads; revocation and concurrent data modifications require an explicit consistency/transaction plan. Never treat previous membership reads as a reusable authorization token.
5. No full authorization coverage for Tenant, Utility, Invoice, Deposit, Payment or other nested finance/resource workflows. No PostgreSQL least-privilege runtime user test.
6. The untracked invoice revision review SQL and all remote-disabled invoice mutations MUST stay untouched.

**Review decision:** Keep the application unchanged while closing gaps with negative authenticated API tests and a reviewed schema/ownership migration plan. Design-only tests demonstrate composition correctness, not production readiness.
