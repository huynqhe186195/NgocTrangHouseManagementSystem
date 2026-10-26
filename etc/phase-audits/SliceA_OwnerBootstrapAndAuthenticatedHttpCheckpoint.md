# Slice A — authenticated HTTP deny + atomic first-owner bootstrap (WIP checkpoint)

**Branch:** `wip/phase-2g2`  
**Date:** 2026-10-27  
**STATUS: UNVERIFIED new code/tests; DO NOT DEPLOY.**

## Confirmed baseline

The user reports their previous **FULL `verify-2g-security-batch.ps1` run passed in entirety**. This included App, SQLite, Host and two read-only source gates at that older commit. That prior PASS does not cover the changes below.

Release blocker remains: `BuildingManagementDbContext` does **NOT** map the proposed `BuildingOperatorAccessGrant`, and real ABP Identity active-account and membership services are still default-deny. The 15 secured Building/Floor/Room CRUD operations remain unusable in normal production flows until a reviewed migration and grant provisioning is enabled.

## What is implemented in this batch

### 1. Authenticated HTTP negative tests (not just anonymous 401)

Extended `BuildingManagementAutoApiAuthorization_Tests.cs` with `APIAUTO09–12`. The isolated in-memory host has a **test-only authenticated header principal** and **repository proxies that throw on any DB call**. These verify that a signed-in, unassigned operator is rejected **before any repository access** for:

- GetList of Building/Floor/Room.
- Guessing Building/Floor/Room IDs with GET.
- Creating Floors and Rooms with arbitrary parent IDs and valid payloads.
- Creating a Building without first-owner bootstrap authority.

Expected new host suite total: **19/19** (previously 15; +4). These are negative authenticated tests against a host without actual EF or production Identity. They do NOT demonstrate cross-building allowance for two *real* distinct staff members.

### 2. Dedicated first-owner bootstrap permission and production entry point

- New `BuildingManagementPermissions.Buildings.BootstrapOwner` permission definition, localized English and Vietnamese. A permission definition **never** assigns itself; no role/user receives this capability.
- `BuildingAppService.CreateAsync` now delegates to `BuildingOwnerBootstrapCoordinator.CreateWithCurrentOwnerAsync`; the previous direct standalone Building `InsertAsync` code was removed.
- Coordinator reads the **actual authenticated current user ID** and checks the special ABP permission (via `AbpBuildingOwnerBootstrapPrincipalReader`), and the active-account adapter. It never accepts a client-selected owner ID. A failed check raises `AbpAuthorizationException`.
- The storage contract `IBuildingOwnerBootstrapWriter.CreateWithOwnerAtomicallyAsync` MUST create Building and its first operator grant together. Runtime DI resolves `DenyAllBuildingOwnerBootstrapWriter`; no production writes can currently pass this point.
- Positive results in `BuildingOwnerBootstrapCoordinator_Tests` use only a fake writer. New **7** test methods cover unauthorized caller, missing permission, inactive account, owner ID not controlled by DTO and writer failure. Pending local verification.

### 3. Actual relational atomic writer **not registered** in production

`EfBuildingOwnerBootstrapAtomicWriter` implements the storage contract but deliberately has **no DI service registration, `ITransientDependency`, `ExposeServices` nor production DbContext mapping**. It is instantiated explicitly only in a disposable test context with the proposed grant table mapping.

It wraps Building insert and matching first-owner grant insert in **one SQL transaction**. If the second statement fails, the first Building insert is rolled back. The editor did **not** create or run an EF migration, modify the running DbContext, connect to PostgreSQL or change role assignments.

New `BuildingOwnerBootstrapAtomicSqlite_Tests` (**4** methods) checks:

1. One Building + one owner grant committed together.
2. A real SQLite `BEFORE INSERT` trigger forces the grant insert to fail **after** the Building insert, and verifies the whole transaction rolls back (0 buildings / 0 grants).
3. Empty owner identity denied before any SQL write.
4. Two separate bootstrap operations result in independent building owner grants.

These tests exercise actual SQLite transactions, but **not** production PostgreSQL, Identity authorization, the permission store, or the deployed runtime schema. Pending local verification.

### 4. Safety checks

`etc/scripts/check-building-scope-design-only-gate.ps1` now asserts:

- The Building CRUD entry point calls the bootstrap coordinator, instead of trying to insert an unowned Building.
- First-owner coordinator retains active account checks and remote disable.
- Production bootstrap writer remains Deny-All.
- Experimental transactional writer remains unregistered and includes explicit transaction and rollback calls.
- Existing Building/Floor/Room auth checks, default-deny account/grant provider and unmodified legacy source protections remain present.

This script is a **source-only guard** and is NOT a substitute for live DI, HTTP or DB security testing. Invoice NO-WRITE gate remains independently mandatory.

## Run one combined full batch

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
```

**Expected**: all existing Application/SQLite/Host tests and both source gates pass. Current host expected **19/19** and new bootstrap tests are **7 Application + 4 SQLite**. If compilation or a negative HTTP check fails, provide the exact output; the batch must stop and be fixed before any broader rollout.

The assistant's code tool environment has NO `dotnet` SDK. Do not describe any of the new tests as PASS before the user's local run or verified CI evidence.

## Remaining blockers to a usable Slice A

1. Explicitly approve **who** can receive `BootstrapOwner`, with two-operator recovery, audit/revocation and anti-self-escalation controls. No anonymous/signup automatic bootstrap and no blanket superuser access.
2. Create and REVIEW a real production membership migration, snapshot, indexes, rollback and source checks. Rehearse in isolated PostgreSQL before any real schema application. Do not overwrite or execute the untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql`.
3. Verify actual ABP Identity account active/lockout state and permission-check provider with isolated real Identity DB. No client-claimed account status.
4. Only after 1–3, selectively register an EF-backed grant reader and transaction writer; keep deny-all as the default until verified.
5. Add signed-in A-vs-B user integration tests against true membership persistence, revocation/write concurrency, all 15 CRUD endpoints, and cross-building list/count filtering.
6. Secure the remaining Contract/Tenant/Reservation/Payment/Utility/Invoice APIs before production use; these are not protected just because Slice A is partially integrated.
7. Keep invoicing revisions/issue/post/debt remote-disabled until separately reviewed.

**Release decision:** HOLD. An isolated PASS means this batch did not break known behavior; it does NOT mean provisioning, migrations or financial workflows are production safe.
