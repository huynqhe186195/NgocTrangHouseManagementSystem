# Slice A — ABP Identity, permissions and persistent membership integration checkpoint

**Scope**: Isolated manual composition only, default production Deny-All preserved.  
**Branch**: `wip/phase-2g2`

## Implemented

- Production **unregistered** `AbpIdentityBuildingOperatorAccountProbe` continues to accept the actual ABP `IIdentityUserRepository` and `IdentityUserManager`. It rejects empty IDs, missing accounts, `IdentityUser.IsActive=false` and real `IsLockedOutAsync` decisions.
- An **internal test-only constructor**, accessible only to `NgocTrangHouseManagementSystem.HttpApi.Host.Tests`, accepts the inherited ASP.NET Identity `UserManager<IdentityUser>.IsLockedOutAsync` method. This exercises lockout end and lockout-disabled semantics without an application Identity DB. Production constructor unchanged.
- `BuildingIdentityMembershipComposition_Tests` composes the existing production ABP principal readers, real ABP `IdentityUser`, real ASP.NET Identity lockout evaluation, scoped CRUD and delegation policies with a disposable SQLite-backed building/floor/room/grant graph.
- Identity repository and permission checker are isolated test adapters, not proof of a configured production ABP Identity/permission store. IDMEM11 uses the actual ABP Identity EF **model** in a disposable SQLite database, but still routes through a test-only repository adapter rather than the production `IIdentityUserRepository` registration. The test app does not register any candidate service in runtime DI and does not grant permissions to production roles.
- Existing APIAUTO06/07 test still verifies effective Deny-All service registration in an isolated ABP application.

## Test matrix

`IDMEM01–11`: active account and allowed building, inactive, locked, expired lockout, missing user, read vs manage permission, cross-building floor and room GUIDs, immediate grant revocation, anonymous/different actor, assign permission distinct from manage and disabled recipient revocable. IDMEM11 additionally persists actual ABP `IdentityUser` entities using `modelBuilder.ConfigureIdentity()` in an independent SQLite database, and checks that fresh repository reads honor disabled/locked/unlocked records while keeping unrelated Building scope denied.

## Release blockers

1. Test against actual **persisted Identity records and role/permission grants** in a disposable ABP Identity database; the isolated repository/permission seams above are NOT a substitute.
2. Approve bootstrap-owner, delegation policy, grant writers, owner recovery and PostgreSQL membership migration.
3. Implement revocation/write locks in one transaction to avoid TOCTOU after authorization, including list/count filtering on final runtime SQL queries.
4. Only then evaluate replacing runtime DenyAll providers. **No production activation or financial workflow** is included in this checkpoint.

## Verification

```powershell
git switch wip/phase-2g2
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
```

The Full workflow executes these isolated host tests and ensures the safety source gates remain intact.
