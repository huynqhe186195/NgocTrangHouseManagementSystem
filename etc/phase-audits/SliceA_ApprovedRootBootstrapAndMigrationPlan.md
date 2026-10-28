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
