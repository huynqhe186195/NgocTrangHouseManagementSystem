# Slice A — Durable owner recovery, cross-DB commit lease and PostgreSQL runtime-role gate

**Branch:** `wip/phase-2g2`  
**Production:** `DENY-ALL / HOLD` — no production rollout, migration, credentials, grants or deployment.

## 1. Owner recovery (isolated staging only)

- One **BootstrapOwner** ID retains exclusive authority to create a building.
- Recovery requires a **separate** allowlisted co-signer with dedicated `Buildings.CoSignOwnerRecovery` ABP permission. Co-signing does **not** grant `Buildings.BootstrapOwner`.
- Both signoffs are written under independently authenticated **server principals**, for the **same** `EvidenceId`, `BuildingId`, original founding account and recipient. Evidence lifetime is limited to 24 h.
- The additive `20261028190000_SliceAOwnerRecoveryApprovals` migration creates `BuildingManagementOwnerRecoverySignoffs` with a separate row per signer. Audit history is protected by DB trigger; populated rollback is rejected.
- A previously active founder blocks recovery. The original self-issued grant remains immutable. The founder query now requires **exactly one** live self-issued grant per Building; zero or multiple candidates fail closed (never `LIMIT 1` selecting an arbitrary founder). The recovery writer independently verifies uniqueness and takes a `Buildings FOR UPDATE` lock before inspecting the self-issued grants. The Building FK's `KEY SHARE` lock makes concurrent grant INSERT wait/serialize; an INSERT that commits first must be detected by the recovery writer, which refuses recovery. PostgreSQL tests cover preexisting ambiguous founders and the competing-INSERT race.
- Recovery writer locks both matching, unexpired, unconsumed rows, inserts exactly one new operator grant with evidence metadata, and consumes the two approvals in the **same Building DB transaction**. Consumption rechecks `ExpiresAtUtc > clock_timestamp()` on the PostgreSQL server (rather than only a previously captured application timestamp). Concurrent reuse cannot succeed.
- Defaults remain `DenyAllBuildingOwnerRecoveryApprovalReader`, `DenyAllBuildingOwnerRecoveryFounderReader`, and `DenyAllBuildingOwnerRecoveryWriter` unless staging host and explicit separate recovery flag are enabled.
- No public recovery or signoff HTTP endpoint has been added. Internal signoff workflow does **not** accept a client-supplied authenticated actor.

## 2. Cross-database Identity transaction lease

- `SliceAStagingIdentityCommitLease` starts an independent read-committed transaction in the **isolated Identity PostgreSQL database** and locks the enabled user row `FOR SHARE`. It accepts the exact Building/Floor/Room mutation capability only when it locks either (a) a real direct-user `AbpPermissionGrants` row, or (b) the persisted `AbpUserRoles`, `AbpRoles` and role-provider `AbpPermissionGrants` rows together. It compares `TenantId` to the real Identity user and role; a role name claim without matching tenant-bound rows fails closed.
- The Building writer still locks the operator-building membership row in the **Building database** and revalidates account/permission after the mutation. Identity row locks remain held through Building Unit-of-Work commit, then are released by `OnCompleted`; failed or disposed units close the lease.
- Direct Identity account `UPDATE`, direct-user permission `UPDATE/DELETE`, the selected role permission `UPDATE/DELETE`, user-role membership `UPDATE/DELETE`, and selected role rename/delete conflict with held row locks. Isolated PostgreSQL HTTP tests check the account, direct permission, role-permission and role-membership revocation waits through Building commit and that stale claims cannot authorize a subsequent write.
- This **is not 2PC/XA**. The selected persisted role grant and membership row are protected, but new or alternative role grants, concurrent role creation, privileged direct SQL, out-of-band cache mutation, grant bootstrap/delegation mutations and uncoordinated migrations are NOT covered by this Building CRUD lease. A privileged DBA can bypass these rules. These paths remain HOLD for production.
- Lock order: **Identity user → direct permission OR selected user-role/role/role-permission rows → Building membership → Building business mutation**. Do not introduce inverse lock acquisition in revocation services.

## 3. PostgreSQL runtime role verification

The CI role test uses a freshly generated `LOGIN` account with non-owner credentials, connects in an **independent session**, and verifies normal grant insert and one-time revoke. It confirms denial of row deletion, grant issuer/recipient rewrite, `ALTER TABLE`, `TRUNCATE`, and grant un-revocation. This proves a **scratch-role candidate**, not the grants of an unknown deployment login.

**Required before actual staging/production acceptance:** a separate deployment owner must provision and inspect actual effective DB roles, including inherited membership and ownership. The runtime web account must not own the Building schema, signoff/grant tables, functions or triggers, and must have no superuser, `CREATEDB`, `CREATEROLE` or schema DDL permission. Runtime identity and Building accounts must be separate from migration/administrative credentials. Audit both databases using a *read-only* session as the actual service login; capture `current_user`, `session_user`, `pg_roles`, effective `has_table_privilege`/`has_column_privilege`, schema ownership and trigger enablement. Do not paste secrets into reports.

An actual deployed-role audit is **not PASS** merely because CI runs on an isolated role. No commands here grant/revoke privileges on a non-test database.

### Read-only operator attestation SQL

The version-controlled `etc/phase-audits/SliceA_RuntimeDbRole_ReadOnlyAudit.sql` is one `SELECT` against the **current effective login** (not `SET ROLE`). It checks both Building grant and recovery signoff tables, their enabled immutable-history guard triggers, runtime schema/table/function ownership, inherited owner membership, privilege escalation through column grants, and administrative role bits. It returns separate `role_pass`, `grant_pass`, `recovery_pass`, and overall `slice_a_role_pass`. Missing objects or unexpected authority fail closed.

Run only during a **separately approved isolated staging/deployment review** using the real, independently provided runtime service-login credentials; do not supply administrator or migration credentials. Example from an approved PowerShell shell with connection secrets supplied securely by the operator (e.g., a permissions-restricted PGPASSFILE):

```powershell
psql -X -v ON_ERROR_STOP=1 -f .\etc\phase-audits\SliceA_RuntimeDbRole_ReadOnlyAudit.sql
```

Require `session_login = runtime_login` and all four flags `true`, confirm expected database + search_path and retain a signed audit record. The script performs **zero writes** and never provisions a role. Its negative-path CI test grants an unsafe immutable-column `UPDATE` to a disposable login and verifies the attestation flips to `FALSE`. Production approval additionally needs a separately approved audit of the **Identity database service-login role**, since Building-only permissions cannot attest Identity credentials.

### Identity DB runtime login audit

`etc/phase-audits/SliceA_IdentityRuntimeDbRole_ReadOnlyAudit.sql` is a **separate** read-only attestation against the actual four ABP tables (`AbpUsers`, `AbpRoles`, `AbpUserRoles`, `AbpPermissionGrants`). Run it as the **Identity service account**, never the migration/admin account, and independently of the Building audit. The result includes `identity_tables_present`, `role_pass`, `users_pass`, `roles_pass`, `membership_pass`, `permission_pass` and `slice_a_identity_role_pass`. Every result must be true and `runtime_login` must equal `session_login`. The SQL rejects schema or database `CREATE` privilege, effective table `TRUNCATE`/`TRIGGER` privilege, table ownership, inherited owner membership and PostgreSQL administrative role attributes. Do not assume revoking `CREATE` on the schema also revokes `CREATE` on the database.

Example for an **approved, authenticated, isolated staging Identity DB connection only**:

```powershell
psql -X -v ON_ERROR_STOP=1 -f .\etc\phase-audits\SliceA_IdentityRuntimeDbRole_ReadOnlyAudit.sql
```

The isolated PostgreSQL HTTP fixture provisions a fresh nonowner login, executes the exact checked-in query against the **real ABP Identity EF schema**, verifies it passes with read-only table access and becomes FALSE if `TRUNCATE` or schema `CREATE` is temporarily granted. The role and privileges are revoked/dropped in the scratch fixture. This CI evidence is NOT evidence of permissions of a deployed production login; obtaining those credentials is outside this scope.

**Release gate:** Both Identity and Building audits must have independently signed operational evidence from the **actual deployed service identities** in the intended databases, with no service/migration credential reuse. Evidence must include database name, `current_user`, `session_user`, schema, result flags, effective inherited roles and review date. No passwords or connection secrets should enter GitHub, CI logs or audit reports.

## 4. Safe validation

- Push CI `buildingmanagement-slice-a-postgres.yml` on `wip/phase-2g2`, verify migration chain, rollback refusal, concurrency, non-owner role, and separate PG HTTP ABP Identity fixture.
- Push CI `buildingmanagement-slice-a-identity.yml` for security composition and `buildingmanagement-safe-regression.yml` for `-Mode Full`.
- PostgreSQL CI only uses ephemeral `postgres:16` with random scratch schemas or generated `bm_slicea_staging_*` databases; all have explicit environment opt-ins and cleanup.
- **Never** run `dotnet ef database update`, DbMigrator, unreviewed `GRANT/REVOKE` or staging recovery against application/production connections.
- If a CI test fails, keep production Deny-All; do not mark Slice A entirely PASS until all operational and alternate-revocation gates have independent evidence.
