# Phase 2G.3A.10 — Operator-to-building membership storage RFC

**Date:** 2026-10-26  
**Branch:** `wip/phase-2g2`  
**Status:** Design-only aggregate + isolated EF configuration and tests. **NOT PRODUCTION READY.**

## Verified previous checkpoints (from user-local console)

- `BuildingBusinessScopeDesignGate_Tests`: **21/21 PASS**.
- `BuildingBusinessScopeEfIntegration_Tests`: **5/5 PASS**.
- `NgocTrangHouseManagementSystem.HttpApi.Host.Tests`: **13/13 PASS**, previously confirmed.
- `check-invoice-revision-no-write-gate.ps1`: **SOURCE NO-WRITE GATE PASS**.
- Git working tree showed only one untracked local generated review SQL script at `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`; preserve it untouched.
- Prior `Application.Tests` build had four warnings, and the isolated EntityFrameworkCore test build succeeded with no warnings. No warning changes have been verified for this new RFC code.

These results DO NOT mean the existing business CRUD endpoints have per-building authorization. They currently have an authenticated-user boundary, not a deployed operator-to-building entitlement policy.

## Findings before selecting a schema

1. The host uses ABP Identity in `NgocTrangHouseManagementSystemDbContext`; the business `BuildingManagementDbContext` is separate. Do not silently create an FK to the Identity `Users` set, especially across modules, without reviewing connection/migration ownership.
2. `MultiTenancyConsts.IsEnabled = false` at present. Never infer multitenant isolation from ABP alone. If this becomes enabled later, the membership and building tenant policies need a fresh review/migration.
3. Current `Building` is a `FullAuditedAggregateRoot<Guid>` with soft-delete. An existing grant to a deleted building MUST NOT make it accessible; the server graph lookup must check the live building.
4. Permission definitions already exist for Building/Floor/Room/Contract read vs manage, but no permission assignments or authenticated operator/building relationship exists.
5. A generic authenticated-user filter alone is insufficient to prevent IDOR, cross-building lists, or write authorization.

## Staged design model: `BuildingOperatorAccessGrant`

The standalone **domain aggregate** in `BuildingManagement.Domain/Buildings/BuildingOperatorAccessGrant.cs` stores:

| Field | Meaning |
| --- | --- |
| `Id` | A separate history record ID |
| `OperatorUserId` | Server-resolved ABP Identity user ID |
| `BuildingId` | Actual building FK |
| `GrantedByUserId` | Audited grant issuer |
| `GrantedAtUtc` | Issuance timestamp from a trusted server clock |
| `ExpiresAtUtc` | Optional expiration; null is potentially indefinite, needs a business policy |
| `RevokedAtUtc` / `RevokedByUserId` | Explicit, irreversible revocation |
| `RevisionNumber` | Monotonically increasing value for optimistic concurrency |

The aggregate rejects zero GUIDs, unspecified/local time, invalid expiration, duplicate revocation, and revocation before issuance. Effective validity requires issue time reached, not revoked and expiry not reached. **That validity alone is NOT authorization**: the caller must separately check real user-active status, ABP operation permission, resource-building relationship, and current membership inside the operation's security/transaction boundary.

A revoked grant remains as history. Any later regrant creates a **different row**, never reactivates the old row.

### Optional isolated EF mapping (not production DbContext)

`BuildingOperatorAccessGrantDesignConfiguration` maps a proposed `BuildingManagementOperatorBuildingGrants` table for the **test-only DbContext**:

- Unique filtered index on `(OperatorUserId, BuildingId)` where `RevokedAtUtc IS NULL`, preventing two simultaneous unrevoked grants.
- `BuildingId` has a restrictive FK to `Building`, preserving history.
- `RevisionNumber` is an optimistic-concurrency token.
- **No Identity FK is presumed.** Actual authenticated account existence/disabled/role state must be validated by a future server-owned grant service.
- An expired but not revoked record still occupies the unique unrevoked slot. A new grant requires explicit revocation/closure first; this avoids resurrecting expired privileges.
- PostgreSQL DDL syntax, indexes, UTC timestamp mapping, migration safety and actor audit enforcement must be reviewed independently before registering this model in production.

**No production changes:** `BuildingManagementDbContext` was not modified, no `DbSet` registered, no `OnModelCreating` production configuration enabled, no migration/snapshot/DB role changed, no grant was created, and `DenyAllBuildingOperatorGrantReader` remains the runtime provider.

## New tests — STAGED / NOT YET RUN LOCALLY

1. `BuildingOperatorAccessGrant_Tests`: **MEMBER01–11**, expected **11/11** — grant data, UTC boundaries, effective/expiry behavior, revoke history, invalid IDs and regrant as new row. Located in `BuildingManagement.Application.Tests`, exercising a Domain type.
2. `BuildingOperatorAccessGrantDesignSqlite_Tests`: **MEMBERDB01–05**, expected **5/5** — isolated SQLite EF persistence, unique unrevoked membership, FK to building, optimistic concurrency on concurrent revocation, and independent building scopes.

The SQLite test constructs a **private nested DbContext** with `Data Source=:memory:` and `Database.EnsureCreatedAsync()` against that disposable connection ONLY; it does not instantiate the running application's DbContext, application credentials or migrator.

### Commands from repo root

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~BuildingOperatorAccessGrant_Tests" --logger "console;verbosity=normal"
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~BuildingOperatorAccessGrantDesignSqlite_Tests" --logger "console;verbosity=normal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\check-invoice-revision-no-write-gate.ps1
git status --short
```

If these tests fail, collect the exact compiler/test diagnostic. Do not point a test to production PostgreSQL to work around a SQLite or mapping failure.

## Unresolved business and security decisions — block migration

- Who can grant/revoke membership, bootstrap the first building owner, or transfer ownership? Prevent self-grant privilege escalation; log issuer/subject and reason. Decide the maximum grant duration and whether null expiry is acceptable.
- Is one staff member allowed to manage multiple buildings; can a building have multiple managers; what can read-only staff do? Need an approved role/capability matrix for financial vs nonfinancial actions.
- Verify a user exists and is active in the **real ABP identity** source, and validate the issuer's rights and building's live status; never accept claimed role/operator/building association from the client.
- Implement a reviewed write API transaction for grant/revoke with a lock strategy. Duplicate grant and stale revocation must fail without partial writes. Decide concurrency semantics for revoke racing with a business mutation and ensure auth recheck within a relevant transaction.
- Implement a **read-only repository-backed** grant provider that selects only current unrevoked, unexpired memberships and requires the authenticated user context. It must NOT replace the production deny-all provider until all authorization/permission checks are wired.
- Filter building-scoped lists, counts and pagination **in SQL before materialization**. Cross-building direct GUID reads/writes must fail, including for a user with global operation permission but no membership.
- If tenant support is later enabled, introduce tenant ownership in the grants and resource graph with a dedicated migration and tests.
- Preserve audit evidence and test DB role separation (non-owner runtime role vs migration owner) on an expressly isolated database.

**Release hold:** Existing business AppServices are NOT yet proven safe for cross-building authenticated access. Invoice rebuild, invoice issue and debt posting remain blocked and remote-disabled. No migration against business data is approved by this RFC.
