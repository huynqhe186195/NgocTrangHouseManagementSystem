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
