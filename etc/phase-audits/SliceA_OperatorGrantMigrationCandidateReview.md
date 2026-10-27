# Slice A — operator/building membership SQL candidate (NOT DEPLOYABLE)

**Branch:** `wip/phase-2g2`  
**Status:** SQL REVIEW CANDIDATE ONLY. No EF migration, production model mapping, role change, or live grant provisioning.  
**Prerequisites:** First-owner authority decision, ABP Identity integration and PostgreSQL sandbox rehearsal.

## Scope of the candidate

- `SliceA_OperatorBuildingGrant_Up_Candidate.sql` creates only `BuildingManagementOperatorBuildingGrants`, with a restricted FK to `Buildings`, unique *unrevoked* user/building membership, nonzero IDs, positive revision and expiry/revocation constraints.
- `SliceA_OperatorBuildingGrant_Down_Candidate.sql` obtains an ACCESS EXCLUSIVE lock before an existence check and refuses rollback if **any** grant record, even revoked, remains. No automatic history deletion.
- The table intentionally has **no FK to Identity users**: the Identity database/DbContext is separate, and issuer, recipient and tenant checks must occur in a server-authorized transaction.
- `ExtraProperties` and `ConcurrencyStamp` are ABP aggregate convention columns. `RevisionNumber` is the explicit optimistic concurrency token defined in the isolated EF configuration.
- This schema does not on its own enforce privileged grant issuance, revocation/audit authority, Identity account state or concurrency with a protected business write. Positive membership alone is NOT authorization.

## Safety & review requirements

1. Candidate scripts are **not** included in the EF migration chain, `BuildingManagementDbContext`, service DI or `DbMigrator`.
2. No production database, account roles or existing integration schema may be touched. A PostgreSQL test may run only using `BM_PG_TEST_CONNECTION` targeting `localhost:5432/ngoctrang_integration_test`; it creates a random schema inside one transaction and rolls it back.
3. PostgreSQL `PGMEMBER01–03` check uniqueness, FK, invalid revision/identity/clock values, re-grant after explicit revoke, loss-preventing Down and transactional cleanup. These are **not** part of the automatic SQLite Full regression; optional local PostgreSQL credentials are required.
4. Before a real EF migration is authored: compare this SQL against the **generated EF model and model snapshot**, decide whether to require immutable grant issuance facts at the DB layer, test first-owner/reader transaction races, ensure proper non-owner runtime role permissions, approve the grant/owner recovery protocol, and rehearse complete Up/Down on a dedicated disposable PostgreSQL database.
5. Do not silently add `DbSet<BuildingOperatorAccessGrant>` to the running context or replace the Deny-All account/grant providers. Do not modify the local untracked revision-ledger review SQL.

## One-command CI/local baseline

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
```

Full adds a **compile-only** check for the PostgreSQL test project; it does **not** connect to PostgreSQL. When test credentials are set in the guarded local test environment, execute the focused PostgreSQL case separately:

```powershell
dotnet test .\modules\buildingmanagement\test\BuildingManagement.PostgreSql.Tests\BuildingManagement.PostgreSql.Tests.csproj --filter 'FullyQualifiedName~BuildingOperatorGrantCandidatePostgreSql_Tests' --logger 'console;verbosity=detailed'
```

The current release decision remains **HOLD**.
