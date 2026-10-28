# Slice A — Isolated staging activation (NOT production)

Status: **code staged, production HOLD**. This is an operator runbook,
not an authorization to deploy or migrate a real database.

## Safety prerequisites

1. Only an ASP.NET Core environment named **Staging** is eligible.
2. Two separate throwaway PostgreSQL databases, both named with the prefix
   `bm_slicea_staging_`, must be provided in
   `ConnectionStrings:Default` (ABP Identity/permissions) and
   `ConnectionStrings:BuildingManagement`. Use dedicated non-production
   credentials; do not point either connection at a tenant database.
3. The root administrator is a **single actual account ID** in
   `BuildingManagement:Bootstrap:RootUserIds`, not a role or wildcard.
   It must be active/unlocked and have the separately assigned ABP
   `Buildings.BootstrapOwner` permission.
4. Grant assignment and revocation require
   `Buildings.AssignOperators` / `Buildings.RevokeOperators`, an active
   issuer membership on that Building, plus recipient status checks. A
   client-supplied issuer ID never authorizes a write.
5. Before activation: backup BOTH isolated databases; apply the
   reviewed source migration `20261028121500_SliceAOperatorBuildingMembership`
   with the dedicated migrator/operator; verify table, foreign key and
   unique index. **No migration is applied by the host at startup.**
6. Do not use staging permissions as proof of production DB role setup.
   Test least-privilege roles, account and permission revocation races,
   application UoW boundaries, and HTTP A/B behavior before release.

## Server-only configuration example (placeholders; not usable secrets)

```json
{
  "ConnectionStrings": {
    "Default": "Host=staging-db;Database=bm_slicea_staging_identity;Username=<isolated-user>;Password=<secret>",
    "BuildingManagement": "Host=staging-db;Database=bm_slicea_staging_buildings;Username=<isolated-user>;Password=<secret>"
  },
  "BuildingManagement": {
    "Bootstrap": { "RootUserIds": ["<approved-root-user-guid>"] },
    "SliceAStaging": {
      "Enabled": true,
      "Activation": "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES"
    }
  }
}
```

The application rejects non-Staging activation, missing confirmation,
non-prefixed databases or a missing/ambiguous root. Production has
`Enabled=false` by default and resolves the DENY-ALL account, grant,
owner, mutation and Building/Floor/Room write services.

### Available isolated staging HTTP routes

`POST /api/slice-a-staging/operator-grants` with
`{ "buildingId": "...", "operatorUserId": "...", "expiresAtUtc": null }`.

`POST /api/slice-a-staging/operator-grants/revoke` with
`{ "buildingId": "...", "operatorUserId": "...", "expectedRevision": 1 }`.

Both require authenticated ABP HTTP identity; the coordinator performs
per-Building permission/membership checks and rejects self-grants. There
is no bypass for a generic administrator. Their production path denies.

## Rollback and recovery

Disable `BuildingManagement:SliceAStaging:Enabled` first, which restores
Deny-All on the next staging restart. Do not drop membership history or
roll back the SQL table after any grant rows exist: the migration Down
intentionally refuses. Restore a verified backup only through an approved
incident plan. No automatic root bootstrap seeding or recovery override.
