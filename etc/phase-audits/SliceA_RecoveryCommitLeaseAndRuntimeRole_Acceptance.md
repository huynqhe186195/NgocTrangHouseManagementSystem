# Slice A — Durable owner recovery, cross-DB commit lease and PostgreSQL runtime-role gate

**Branch:** `wip/phase-2g2`  
**Production:** `DENY-ALL / HOLD` — no production rollout, migration, credentials, grants or deployment.

## 1. Owner recovery (isolated staging only)

- One **BootstrapOwner** ID retains exclusive authority to create a building.
- Recovery requires a **separate** allowlisted co-signer with dedicated `Buildings.CoSignOwnerRecovery` ABP permission. Co-signing does **not** grant `Buildings.BootstrapOwner`.
- Both signoffs are written under independently authenticated **server principals**, for the **same** `EvidenceId`, `BuildingId`, original founding account and recipient. Evidence lifetime is limited to 24 h.
- The additive `20261028190000_SliceAOwnerRecoveryApprovals` migration creates `BuildingManagementOwnerRecoverySignoffs` with a separate row per signer. Audit history is protected by DB trigger; populated rollback is rejected.
- A previously active founder blocks recovery. The original self-issued grant remains immutable.
- Recovery writer locks both matching, unexpired, unconsumed rows, inserts exactly one new operator grant with evidence metadata, and consumes the two approvals in the **same Building DB transaction**. Concurrent reuse cannot succeed.
- Defaults remain `DenyAllBuildingOwnerRecoveryApprovalReader`, `DenyAllBuildingOwnerRecoveryFounderReader`, and `DenyAllBuildingOwnerRecoveryWriter` unless staging host and explicit separate recovery flag are enabled.
- No public recovery or signoff HTTP endpoint has been added. Internal signoff workflow does **not** accept a client-supplied authenticated actor.

## 2. Cross-database Identity transaction lease

- `SliceAStagingIdentityCommitLease` starts an independent read-committed transaction in the **isolated Identity PostgreSQL database**, selects the enabled user row `FOR SHARE`, and also locks the **explicit direct-user ABP permission grant row** for the exact Building/Floor/Room mutation capability. Missing row fails closed (no role-claim-only bypass).
- The Building writer still locks the operator-building membership row in the **Building database** and revalidates account/permission after the mutation. Identity row locks remain held through Building Unit-of-Work commit, then are released by `OnCompleted`; failed or disposed units close the lease.
- Direct Identity account `UPDATE` and direct-user permission `UPDATE/DELETE` conflict with those locks; the race is serialized by PostgreSQL row locks. Tests must assert that the Identity mutation waits for Building commit and that later requests are denied.
- This **is not 2PC/XA**. Role-level permission changes, role membership, privileged direct SQL, out-of-band cache mutation, grant bootstrap/delegation mutations and uncoordinated migrations are not all protected by this Building CRUD lease. A privileged DBA can bypass these rules. These paths remain HOLD for production.
- Lock order: **Identity user + direct permission row → Building membership → Building business mutation**. Do not introduce inverse lock acquisition in revocation services.

## 3. PostgreSQL runtime role verification

The CI role test uses a freshly generated `LOGIN` account with non-owner credentials, connects in an **independent session**, and verifies normal grant insert and one-time revoke. It confirms denial of row deletion, grant issuer/recipient rewrite, `ALTER TABLE`, `TRUNCATE`, and grant un-revocation. This proves a **scratch-role candidate**, not the grants of an unknown deployment login.

**Required before actual staging/production acceptance:** a separate deployment owner must provision and inspect actual effective DB roles, including inherited membership and ownership. The runtime web account must not own the Building schema, signoff/grant tables, functions or triggers, and must have no superuser, `CREATEDB`, `CREATEROLE` or schema DDL permission. Runtime identity and Building accounts must be separate from migration/administrative credentials. Audit both databases using a *read-only* session as the actual service login; capture `current_user`, `session_user`, `pg_roles`, effective `has_table_privilege`/`has_column_privilege`, schema ownership and trigger enablement. Do not paste secrets into reports.

An actual deployed-role audit is **not PASS** merely because CI runs on an isolated role. No commands here grant/revoke privileges on a non-test database.

## 4. Safe validation

- Push CI `buildingmanagement-slice-a-postgres.yml` on `wip/phase-2g2`, verify migration chain, rollback refusal, concurrency, non-owner role, and separate PG HTTP ABP Identity fixture.
- Push CI `buildingmanagement-slice-a-identity.yml` for security composition and `buildingmanagement-safe-regression.yml` for `-Mode Full`.
- PostgreSQL CI only uses ephemeral `postgres:16` with random scratch schemas or generated `bm_slicea_staging_*` databases; all have explicit environment opt-ins and cleanup.
- **Never** run `dotnet ef database update`, DbMigrator, unreviewed `GRANT/REVOKE` or staging recovery against application/production connections.
- If a CI test fails, keep production Deny-All; do not mark Slice A entirely PASS until all operational and alternate-revocation gates have independent evidence.
