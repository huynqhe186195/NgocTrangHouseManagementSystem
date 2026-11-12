# Phase 2G.3A.13 — SQL-scoped collection query audit (design-only)

**Branch:** `wip/phase-2g2`  
**Prepared:** 2026-10-27 (Asia/Bangkok)  
**Status:** NEW QUERY TESTS AND SOURCE GUARD STAGED — NOT LOCALLY EXECUTED

## Verified evidence from the latest user-provided test log

- Phase 2G.3A.12 `BuildingOperatorChangeDesignGate_Tests`: **17/17 PASS**.
- `NgocTrangHouseManagementSystem.HttpApi.Host.Tests` (includes `APIAUTO06` default-deny DI): **14/14 PASS**.
- `check-invoice-revision-no-write-gate.ps1`: **SOURCE NO-WRITE GATE PASS**. This script does not exercise database-level roles or transaction safety.
- `git status --short`: only the local, untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`; preserve it unchanged.
- Host run showed **5 compiler warnings** (2 existing CS8604 in OpenIddict seeding, 2 existing CS8602 in Swagger filter, 1 CS0162 unreachable code). Application Tests build showed **4 warnings** (previously identified Mapperly RMG012).

These 31 test passes cover design-only delegation, anonymous authentication and the default-deny dependency. They **do not prove real authenticated cross-building access enforcement in business CRUD endpoints**.

## New SQL query candidates

`BuildingBusinessScopedCollectionQueries` in the EF module contains four static IQueryable composition helpers for **Building**, **Floor**, **Room**, and **Contract**. Each takes an explicit collection of **server-trusted** building IDs and uses only database-side predicates:

- Building list: direct authorized-building filter.
- Floor list: join floor to its actual building, and filter at SQL layer.
- Room list: join room → floor → building before list sorting/counting/paging.
- Contract list: join contract → room → floor → building before list sorting/counting/paging.
- All four reject soft-deleted nested resources/buildings and fail empty for empty/null/invalid grants. Collection queries are composed first; `CountAsync`, `Skip`, `Take`, and `Select` are subsequently applied to the resulting IQueryable.

**Important:** These are NOT AppService methods, HTTP routes, real grant readers, or authorization decisions. A caller that trusts client-submitted IDs can defeat this helper. The future real application layer must obtain current grants using a server-authenticated ABP user, require the specific operation permission and validate account state. The existing application CRUD/list code is untouched and remains a release blocker.

## New tests — pending local execution

`BuildingBusinessScopedCollectionQuery_Tests` in `BuildingManagement.EntityFrameworkCore.Tests` uses existing **isolated ABP SQLite** repositories and synthetic buildings, floors, rooms, and contracts:

- `LISTSCOPE01`: buildings/floors only for server-granted building.
- `LISTSCOPE02`: room `CountAsync` is filtered before `OrderBy/Skip/Take`, including a forbidden room that sorts before allowed rooms.
- `LISTSCOPE03`: contracts resolved and filtered through actual nested graph.
- `LISTSCOPE04`: null/empty/Guid.Empty/unknown grant means no listed resources.
- `LISTSCOPE05`: duplicate/mixed GUIDs never add another building.
- `LISTSCOPE06`: soft-deleted building removes all nested lists.
- `LISTSCOPE07`: EF-generated SQL contains JOIN/WHERE and returns filtered counts.

Expected **7/7**, NOT YET VERIFIED.

A new read-only PowerShell script `etc/scripts/check-building-scope-design-only-gate.ps1` stops if the design-only membership is mapped into the runtime business DbContext or default-deny source guard is removed. This is a **source-only** gate; it does not prove actual runtime DI or authorization. Run the prior invoice no-write gate separately.

## Commands from repo root

```powershell
git pull --ff-only origin wip/phase-2g2

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingBusinessScopedCollectionQuery_Tests" --logger "console;verbosity=normal"

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-building-scope-design-only-gate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1

git status --short
```

**Stop on compilation or regression failure.** Send the exact diagnostic; never alter live connection strings or run DbMigrator to fix a SQLite test problem.

## Unresolved review gates

1. Real ABP Identity account state and grant authority/owner-bootstrap policy need **integrated tests with isolated identity storage**; the unregistered Identity adapter being built proves only API compatibility.
2. Approve a schema and PostgreSQL least-privilege migration for `BuildingOperatorAccessGrant`; until then runtime DB MUST stay unmapped.
3. Actually enforce resource-specific permissions and active per-building grants in every authenticated **read and write AppService**, including indirect nested mutations and financial operations; default sign-in is not enough.
4. SQL filtering must be applied **before pagination and count**, never as a client-side postfilter. Add real HTTP 403/404 and cross-building IDOR negative tests.
5. Review revocation-vs-write race protocol and lock order. A prior positive preflight never authorizes a later write.
6. Keep invoice draft revision, issuance, debt posting and permission assignments disabled.

There is **no production migration, no grant/revoke, no app DB query, no current-user role change and no invoice mutation** in this increment.
