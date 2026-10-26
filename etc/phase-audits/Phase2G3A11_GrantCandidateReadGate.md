# Phase 2G.3A.11 — Read-only operator-building grant selection (candidate gate)

**Date:** 2026-10-26  
**Branch:** `wip/phase-2g2`  
**Status:** Staged read-only SQL/query code and tests — **NEW TESTS NOT RUN LOCALLY**.

## Verified inputs from user-local console

- Phase 2G.3A.10 `BuildingOperatorAccessGrant_Tests`: **11/11 PASS**.
- Phase 2G.3A.10 `BuildingOperatorAccessGrantDesignSqlite_Tests`: **6/6 PASS**.
- Prior stage `BuildingBusinessScopeDesignGate_Tests`: **21/21 PASS**.
- Prior stage `BuildingBusinessScopeEfIntegration_Tests`: **5/5 PASS**.
- Prior isolated HTTP authorization suite: **13/13 PASS**.
- `check-invoice-revision-no-write-gate.ps1`: **SOURCE NO-WRITE GATE PASS** (not a live DB-role test).
- `git status --short`: only untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql` is present; **preserve** it.
- Application test build: four pre-existing Mapperly warnings. EF test build succeeded without reported warnings.

## New candidate query

`BuildingOperatorGrantCandidateQuery.FindActiveBuildingIdsAsync` in the business EF project:

- Accepts only **queryables supplied by a trusted server**, the authenticated operator ID from a trusted caller, and an explicit **trusted UTC instant**.
- Performs a single SQL-translatable selection with the operator ID, effective start time, expiration `ExpiresAtUtc > now` and `RevokedAtUtc IS NULL`.
- Joins the real Building row and excludes soft-deleted buildings (`!Building.IsDeleted`) **in SQL**, including in the isolated DbContext that does not install ABP's global soft-delete filters.
- Returns distinct building GUIDs, not a credential or time-stable authorization token. It does not enumerate other users' grants.
- Is a **static candidate query only**: it is **not registered as `IBuildingOperatorGrantReader`**, does not replace `DenyAllBuildingOperatorGrantReader`, and cannot run against the current application's unmapped grant table.
- Does **not** check the real ABP Identity user's disabled/locked status, `IPermissionChecker`, role, action, or a resource's full graph; the later runtime authorization integration must verify all these facts.

### Security clarification found during review

`BuildingOperatorAccessGrant.IsEffectiveAt` previously permitted an already-recorded revocation with a future `RevokedAtUtc` timestamp to remain effective until that timestamp. The SQL candidate query excludes **all** recorded revocations. The domain method now uses the same **deny immediately if `RevokedAtUtc != null`** semantics and a new `MEMBER12` test covers that boundary. Timestamp recording remains an audit field, not a mechanism to schedule continued access after revocation.

No existing production user grant or invoice/data write path was changed by this correction.

## New tests — PENDING local verification

1. `BuildingOperatorAccessGrant_Tests`: now expected **12/12**, including `MEMBER12` (previously 11/11 confirmed).
2. `BuildingOperatorGrantCandidateQuery_Tests`: expected **9/9**, `GRANTQUERY01–09`. Runs a fresh **in-memory SQLite** table and verifies actor isolation, future start, exact expiration, revoked row, soft-deleted building, independent active scopes, malformed caller identity/clock and revocation observed on a new read.
3. Previous `BuildingOperatorAccessGrantDesignSqlite_Tests`: expected **6/6**, unchanged; optional targeted regression to confirm schema rules remain intact.
4. Re-run `check-invoice-revision-no-write-gate.ps1` unchanged.

From repository root:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~BuildingOperatorAccessGrant_Tests" --logger "console;verbosity=normal"
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingOperatorGrantCandidateQuery_Tests|FullyQualifiedName~BuildingOperatorAccessGrantDesignSqlite_Tests" --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

Expected filtered EF count **15/15** if tests compile and run (9 new query + 6 previously verified schema). **Do not report new test passes without local evidence.**

## Remaining release blockers

1. Approve the business authority matrix for grant/revoke/bootstrap/ownership transfer and verify the authenticated issuer/recipient is active in the real ABP Identity source. Ensure grant writers cannot self-escalate.
2. Review storage/migration for PostgreSQL, UTC handling, security ownership and historical audit retention. The production `BuildingManagementDbContext` still does not map the grant entity.
3. Only then register a real EF-backed grant reader and replace default deny **after** enforcing both action-level ABP permission and current resource-building membership in every business AppService; otherwise logged-in users may still access unauthorized buildings.
4. Restrict collection SQL **before** paging/counting, and enforce write-scope/permission recheck in an appropriate transaction/locking protocol; test revocation races.
5. Add real authenticated HTTP tests (403 or deliberate 404), IDOR and cross-building isolation for Building, Floor, Room, Contract, Tenant, Payment, Meter and all financial endpoints. No mutating invoice revision/issue/post/debt endpoint should be enabled.
6. Real deployed host endpoint metadata and PostgreSQL non-owner runtime DB-role security tests remain outstanding.

**No migration, production schema registration, PostgreSQL operation, identity role assignment, client permission exposure, or invoice mutation was performed.**
