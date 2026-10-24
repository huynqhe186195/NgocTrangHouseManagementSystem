# Phase 2G.3A.4 — Full EF migration chain rehearsal (WIP)

## Confirmed baseline, 23/10/2026

- **109/109** selected application/EF Core/SQLite tests PASS; build
  succeeded with four existing Tenant Mapperly RMG012 warnings.
- **PGINV01 1/1 PASS** against localhost PostgreSQL:
  exact reviewed invoice DDL worked in a rollback-only temporary schema.
- Commit `d6a058b` contains the exact EF-generated, 72-line PostgreSQL
  invoice migration SQL preview, already manually audited.
- No production or existing application database schema was modified.

## First local run — compile-only blocker (24/10/2026)

The user ran the isolated full-chain rehearsal with test credentials
supplied through PowerShell's credential prompt. The build stopped at
`FullInvoiceMigrationChainPostgreSql_Tests.cs:98` with **CS1061**:
`DatabaseFacade.GetMigrationsAsync()` does not exist for the installed
EF Core API. This was a test coding error rather than a database/migration
failure. Changed it to the EF Core 10 supported synchronous API:
`db.Database.GetMigrations().ToArray()`.

All previous `109/109` application/SQLite checks and `PGINV01 1/1`
rollback-only DDL test remain the latest independently confirmed passes.
The updated `PGMIG01` has **not** been re-run. The failed compilation
did not enter the test body or create a temporary database.

## Second local run — test failure, root error not supplied (24/10/2026)

The user reported `PGMIG01` compiled and was discovered but ended
**1 failed / 0 passed**. Only the overall test summary and outer
PowerShell wrapper error were supplied. This is NOT proof that the
migration chain itself failed: the exception may have occurred during
privilege checking, migration application, assertions or scratch cleanup.
The exact root cause and cleanup outcome cannot be determined from the
summary alone.

Added diagnostic improvements:
- The launcher now uses the VSTest detailed console reporter to print
  the underlying xUnit exception instead of only the final summary.
- PGMIG01 records the current rehearsal stage and wraps the original
  exception as its inner exception, preserving any PostgreSQL SQLSTATE.
- The test still executes only on the explicitly opted-in, guarded
  localhost integration-test connection; the new diagnostic code does
  NOT change migration targets or database cleanup policy.

After pulling, rerun through the existing credential-protected workflow.
Share the **first** PGMIG01 exception block (`PGMIG01 failed at stage`,
`Failed ...`, `PostgresException`, `SqlState` and relevant stack)
without connection strings, usernames or passwords. Then fix the
identified cause and rerun the complete audit. The latest confirmed
PostgreSQL success remains PGINV01 **1/1 PASS**; PGMIG01 is still open.

## Third local run — PostgreSQL authentication failure (24/10/2026)

The user supplied the complete detailed xUnit log. This time the .NET
test project **compiled successfully**, but `PGMIG01` stopped in
`NpgsqlConnection.OpenAsync()` with PostgreSQL SQLSTATE **28P01**
(`password authentication failed`). The attempted username in the log
was `postgre`; this may be a typo for the common `postgres` local
role, but the actual test-role credentials must be confirmed locally.

This failure occurred **before the scratch CREATE DATABASE operation**:
no disposable test database or migration schema was created in this run.
The failure is an environment/credential gate, not evidence of a broken
migration chain. No connection string or password is stored here.

Next step: securely retry with the **same valid local PostgreSQL test
role** used for earlier successful PGINV01 runs. Prefer
`Get-Credential -UserName 'postgres'` only if `postgres` really is the
configured local role; otherwise enter the actual authorized test
username. Do not paste credentials into GitHub or chat. A successful
connection must still satisfy the existing `CREATEDB` permission
preflight before scratch database creation.

PGMIG01 still awaits its first authenticated execution; the separately
verified PGINV01 remains **1/1 PASS**.

## What the new PGMIG01 test verifies

This is the full sequence of migrations for
`BuildingManagementDbContext`, not the separate host application's
`NgocTrangHouseManagementSystemDbContext` migrations.

`FullInvoiceMigrationChainPostgreSql_Tests.PGMIG01`:

1. Requires explicit opt-in `BM_ALLOW_FULL_MIGRATION_REHEARSAL` and
   uses the existing mandatory `BM_PG_TEST_CONNECTION` guard:
   localhost/127.0.0.1:5432,
   existing `ngoctrang_integration_test` database, no other target.
   Credentials are never printed.
2. Checks that the **test DB role has CREATEDB or SUPERUSER** before
   creating anything. If not, stops without modifications.
3. Creates exactly one uniquely named scratch database with prefix
   `ngoctrang_migration_rehearsal_`, using `TEMPLATE template0`.
   This requires PostgreSQL privilege; no existing DB is reset, cloned
   or emptied. The existing `ngoctrang_integration_test` schema is
   unchanged.
4. Uses only `BuildingManagementDbContext` EF Core migrations, on this
   fresh scratch DB. It does **not** call ABP DbMigrator or
   `EnsureCreated()`.
5. Checks the initial EF migration history is empty, runs
   `MigrateAsync()`, verifies every EF migration including
   `20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots` is applied,
   and checks there are no pending migrations.
6. Runs `MigrateAsync()` again to verify idempotence.
7. Checks three invoice tables, five unique indexes, six restrictive
   invoice foreign keys and the `btree_gist` extension.
8. Disposes all scratch DB connections, then performs **DROP DATABASE
   only on the randomly generated scratch database** within `finally`.
   On success verifies the scratch DB no longer exists.

The test deliberately does NOT run against a business, production,
staging or the existing integration test database. It never executes
migration SQL against an existing database.

### One important limitation

PostgreSQL `CREATE DATABASE` and `DROP DATABASE` are real DDL outside a
transaction, but *only for the randomly named, test-created, disposable
database*. A hard process crash can leave that scratch DB behind; the
test's cleanup error will identify the exact database for manual
inspection. Do NOT guess or drop another database. If the test role has
no CREATEDB privilege, the test safely fails without creating anything.

This test proves the BuildingManagement module migration chain from a
fresh database; it is **not** proof of applying the migration to an
existing populated database, nor of the separate host application
DbContext chain. That is another stage and requires a controlled
backup/restore or test-data-based rehearsal.

## Local execution

From repository root on the WIP branch with a clean worktree:

```powershell
git pull --ff-only origin wip/phase-2g2

git status --short

# No connection strings should be pasted into chat or the terminal log.
# The existing BM_PG_TEST_CONNECTION must already target localhost:5432
# and database ngoctrang_integration_test.

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-migration-chain.ps1

git status --short
```

The wrapper enables the explicit test opt-in for that one child
`dotnet test` process only, then restores the prior environment value.
It does not call `dotnet ef database update` or the production DbMigrator.

**Expected PGMIG01: 1/1 PASS (NOT YET VERIFIED).**

## Remaining business release blockers

- Operator-specific authorization for invoice draft persistence;
  remote write remains disabled.
- Concurrency controls covering simultaneously edited meter readings,
  replacements, tariffs and invoice draft creation.
- Partial tenancy/move-out final meter authorization, bill cutoff,
  rent/service fees, shared meters, deposit and invoice correction
  policies.
- Tenant access/financial privacy audits and historical invoice
  change controls.
- Separate host application DbContext migration chain, populated DB
  upgrade rehearsal and reviewed deploy plan.

Do not merge to `main` or apply database migrations to any production
database at this stage.
