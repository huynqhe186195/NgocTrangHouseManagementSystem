# Slice A — approved root-first ownership policy

Decision on **2026-10-28**: only expressly designated root administrative user IDs may create a new building. The same server-authenticated, active and unlocked account is the initial software owner; later assignment/revocation requires a separately granted per-building permission, the issuer's own active building membership, and no self-assignment. No administrator wildcard or client-provided owner ID.

## Enforced before opening production

`AbpBuildingOwnerBootstrapPrincipalReader` now requires BOTH:
- server-only `BuildingManagement:Bootstrap:RootUserIds` contains authenticated account ID (defaults to **empty**);
- ABP grants the exact `BuildingManagement.Buildings.BootstrapOwner` permission.

The existing coordinator checks account active/lockout (production account reader defaults DenyAll). Existing atomic writer writes building and first grant in one transaction but remains unregistered. No automatic role seed, wildcard superadmin, production permission escalation, or database migration.

A configuration example illustrating **format only** (never use this placeholder as a real account):

```json
{"BuildingManagement":{"Bootstrap":{"RootUserIds":["11111111-1111-4111-8111-111111111111"]}}}
```

An ID on the allowlist without ABP permission remains denied; a role with BootstrapOwner but no allowlisted ID also remains denied. Account recovery and actual root IDs must be reviewed separately.

Pending: reviewed production membership migration+snapshot, ABP real permission-store integration, positive authenticated A/B building HTTP tests, revocation/write race protocol. Release remains **HOLD**.

## 2026-10-28 — Staged operator delegation and revocation

The new internal `BuildingOperatorChangeCoordinator` checks the server issuer,
active accounts, a dedicated Assign/Revoke permission, current Building
membership and no self-assignment. It calls the domain
`IBuildingOperatorGrantMutationWriter` contract. **Production DI registers
only `DenyAllBuildingOperatorGrantMutationWriter`**, so no real grant writes
become remotely accessible.

The unregistered `EfBuildingOperatorGrantAtomicMutationWriter` uses a
PostgreSQL SERIALIZABLE transaction and rechecks the issuer's current
membership and nondeleted Building inside the write transaction. It uses a
unique unrevoked index for duplicate prevention and optimistic revision for
revocations with durable audit history. PostgreSQL `EFMEMBER04-06` check
wrong-building/self escalation, duplicate insert, revision-checked revoke,
regrant and concurrent assignment. SQLite `IDMEM13` composes ABP permission
checks, stored grants and immediate revocation; the isolated ABP HTTP host
asserts effective default-DenyAll DI.

**Still HOLD:** this is not complete account/role permission-store integration
or a full concurrent role revocation/write proof; grant issuer strings cannot
be trusted directly from clients. No production migration, grant activation,
new public endpoint or finance mutation is included.

## 2026-10-28 — transaction-bound membership write fence (candidate)

`EfBuildingScopedWriteFenceCandidate` acquires PostgreSQL
`SELECT ... FOR SHARE OF g` on a fresh unrevoked membership inside the
**same READ COMMITTED transaction** as the guarded CRUD mutation.
It blocks concurrent revoke/update until commit; afterward, future
writes cannot acquire a live grant. The candidate has **no ABP DI marker**,
is not registered in production, and cannot authorize an operation by
itself: caller must separately establish authenticated ABP identity,
active account, operation permission, and server-resolved Building resource.

`EFMEMBER07–08` validate the deterministic PostgreSQL row-lock
revoke/write ordering and cross-Building denial. No production CRUD
app-service is yet wired into this fence; ABP permission/account
revocation races on other storage are still explicit release blockers.

## Full application EF chain rehearsal (isolated, never deployment)

`PGCHAIN01` creates a random disposable PostgreSQL database named
`bm_slicea_rehearsal_<32 lowercase hex>`, runs the **actual current
BuildingManagementDbContext EF migration chain** into that database,
inserts two EXISTING Buildings, then applies only the quarantined
test-assembly Slice A EF migration. It verifies Building rows are intact,
writes a membership on an existing Building, rejects destructive Down
when historical data is present, and confirms scoped member reads.
It requires explicit `BM_PG_SLICE_A_FULL_CHAIN_OPT_IN` and a local,
dedicated PostgreSQL role with CREATEDB. The CI workflow uses a fresh
PostgreSQL 16 service with no production credentials. The exact generated
database is dropped in finally. No runtime migration/snapshot is changed.

This rehearses an upgrade mechanism; it is NOT a production migration
approval. The last-review production migration, DB privileges, backups
and deploy/rollback review remain required.

## Isolated real HTTP A/B staging proof

`STAGEHTTP01` uses the **actual ABP conventional BuildingAppService HTTP
controller**, real IdentityUser EF repository and permission checker,
Building EF repository and SQLite-persisted candidate membership reader.
The test's separate ABP module overrides production DenyAll providers
ONLY inside the isolated TestServer, and all three DB files are generated
and deleted by the test. A may list/get A but not B; B may list/get B but
not A; after A's membership is revoked, A is denied. This is a real
positive/negative HTTP routing test, but NOT a production PostgreSQL
host or a release of the privilege model. Grant/revoke API remains
disabled and production DenyAll remains intact.

## Building mutation boundary wired (still production DENY)

`BuildingAppService.UpdateAsync` and `DeleteAsync` now execute INSIDE
`IBuildingScopedWriteBoundary`, after ABP CRUD preflight. The production
default `DenyAllBuildingScopedWriteBoundary` rejects every mutation,
including a future mistakenly permissive preflight configuration.
`EfBuildingScopedWriteBoundaryCandidate` is an unregistered adapter
which resolves the trusted actor from `ICurrentUser` and delegates to
the PostgreSQL grant-lock fence using the same EF DbContext transaction.
It deliberately refuses unmapped grants and SQLite in production.

The isolated SQLite HTTP fixture overrides only its test DI boundary to
check permission + membership before Update; it does NOT claim to
serialize concurrent PostgreSQL writes. PostgreSQL EF tests exercise the
actual row lock and revocation ordering separately. **No production
DB migration, mapping, DI registration or release change.**

## 2026-10-28 — Building mutation boundary integration checkpoint

The live `BuildingAppService` now passes both `UpdateAsync` and
`DeleteAsync` through a mandatory `IBuildingScopedWriteBoundary`
after preliminary ABP user/permission/resource checks. Isolated A/B
HTTP staging tests override the boundary only within a disposable
test host. The default runtime DI resolves
`DenyAllBuildingScopedWriteBoundary`; a dedicated host assertion
verifies its callback NEVER executes. The unregistered Npgsql
adapter resolves identity only from ABP `ICurrentUser` and invokes
the exact SQL fence.

Production activation is NOT approved until app services share the
same PostgreSQL EF DbContext/UoW with the locked membership row,
the runtime membership model/migration is reviewed and deployed,
and role/account permission-revocation races and least-privilege
DB rights are proven. A design-only adapter and separate PostgreSQL
race tests are not proof of a deployed runtime path.

## Source migration now in runtime EF chain (NOT deployed)

A new native EF Core migration
`20261028121500_SliceAOperatorBuildingMembership` and corresponding
`BuildingManagementDbContext` mapping/snapshot are committed.
`PGCHAIN01` applies the **actual** app migration chain to a disposable
database containing two Building rows, then proves guarded Down refuses
to delete historical membership. No permissions are seeded.

This is **schema availability only**, not authorization activation:
runtime Identity/account/grant/read/write providers still default
to DenyAll; code does NOT run DbMigrator on startup in this batch.
CI cannot establish a production backup, DBA-approved least-privilege
role, operation windows or live database state.

### Reviewed migration metadata alignment

`BuildingOperatorAccessGrantDesignConfiguration`, model snapshot and
native Up SQL now use the SAME primary key, Building FK, filtered
user/building unique index and Building lookup index names. This
reduces accidental EF migration drift. `PGCHAIN01` must PASS against
the runtime migration on a disposable populated PG database before
release signoff. No schema or permission change was made on production.
