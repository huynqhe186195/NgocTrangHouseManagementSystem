# Phase 2G.3A.4 — Offline invoice draft migration preparation

## Last validated build and business audit

- User report, 23/10/2026: **89/89 selected SQLite/EF/application
  regression tests PASS**, build succeeded with 4 existing Tenant
  Mapperly RMG012 warnings.
- Contract-period AND room-period uniqueness were tested in the real
  SQLite provider; globally unique meter source IDs were independently
  tested against two physically distinct rooms. Snapshot numbers
  survived source-reading soft deletion.
- The internal service is still not an HTTP endpoint, and no issued
  invoice, debt, allocation or collection logic has been enabled.

## Generated migration reviewed on 24/10/2026

The user generated and pushed commit `ed8165e`:
`20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`.
Its `Up()` contains only three `CreateTable` operations with
restrictive FKs, five unique indexes and two supporting FK indexes.
Its `Down()` drops the invoice source, item and header tables in
dependency order. The auto-generated Designer and current ModelSnapshot
contain the three new entities. The user's offline EF Core CLI
(`10.0.12`) reported no pending model changes.

- **Migration schema review: PASS, subject to SQL/provider verification.**
- **Not applied to PostgreSQL.** An EF schema diff alone does not prove
  the generated SQL will execute on the user's isolated test database.
- The new `UnitPrice` field uses `numeric(18,0)`, consistent with
  the existing `UtilityRate` PostgreSQL mapping. The API previously
  allowed fractional or out-of-range decimals, risking silent rounding
  or DB failure. Create/update rate validation now rejects such input;
  new UR19-UR20 tests must be run on the user's machine.

## What this change does

The repository now provides:
- An explicit offline mode for `BuildingManagementDbContextFactory`,
  selected ONLY when `BM_INVOICE_MIGRATION_OFFLINE=1`. It uses an
  unreachable local port rather than loading any real DB credentials.
  Design-time `migrations add` builds the EF model without connecting.
- A guarded PowerShell helper
  `etc/scripts/scaffold-invoice-draft-migration.ps1` that:
  1. Requires `wip/phase-2g2` and a clean working tree.
  2. Requires the existing EF Core CLI (`dotnet ef`) and does NOT install
     tools, start a database or run DbMigrator.
  3. Calls ONLY `dotnet ef migrations add` and
     `dotnet ef migrations has-pending-model-changes`.
  4. Clears its offline flag in a `finally` block.
  5. Inspects the generated `Up` operations and requires exactly
     THREE `CreateTable` operations (invoice/header, item, evidence)
     with four essential unique indexes and no destructive Up changes.
  6. Prints the migration source, Designer and model snapshot to review.

This prepares scaffolding; **the migration files do not exist in Git
until the local .NET SDK/EF CLI generates and commits them**. No
PostgreSQL migration is applied by this helper.

## Local steps (no database write)

From the repository root:

```powershell
git pull --ff-only origin wip/phase-2g2

# Ensure a clean git status before running.
git status --short

# This only creates migration files; no database schema update:
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\scaffold-invoice-draft-migration.ps1

# Show exactly what EF Core generated:
git status --short
git diff -- modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/BuildingManagementDbContextModelSnapshot.cs

# The migration .cs and Designer.cs are NEW, so inspect them directly too.
```

If the helper reports missing `dotnet ef`, stop and install/restore the
EF Core 10 CLI using your project's approved tool setup. Do not switch
to SQL manually.

## Read-only PostgreSQL SQL preview

After pulling the reviewed migration and the latest WIP scripts:

```powershell
git pull --ff-only origin wip/phase-2g2
git status --short
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\review-invoice-draft-migration-sql.ps1
git status --short
```

The script uses the offline factory to render SQL **only for**
`20261023103354_Phase2G2B1_AddMeterReplacements` to
`20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`.
It produces `etc/phase-audits/Phase2G3A4_InvoiceDraftSchema_Review.sql`,
checks exactly three new tables, five expected unique indexes and no
destructive commands. It does not connect to PostgreSQL or apply SQL.
Review its output and the file content before considering a test DB.

```powershell
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~UtilityRateAppService_Tests|FullyQualifiedName~InvoicePersistenceIntegration_Tests|FullyQualifiedName~InvoiceSchema_Tests|FullyQualifiedName~UtilityMeterAppService_Tests"
```

The two new tariff validation cases UR19-UR20 are not yet verified.

## Review gates before commit/deployment

1. Up creates only:
   `BuildingManagementInvoices`,
   `BuildingManagementInvoiceItems`,
   `BuildingManagementInvoiceItemMeterSources`.
2. Verify that contract-period and room-period uniqueness both exist;
   item utility type uniqueness and global evidence-source uniqueness
   also exist. FKs must be restrictive, not cascading into financial
   history.
3. Compare generated metadata (ABP audited columns, types, nullability,
   precision/scale, indexes) with the current EF Core model.
4. Do not commit if the migration contains unrelated schema changes,
   especially `DropTable`, `AlterColumn`, raw SQL, or other model drift.
5. Only after code/schema review, rerun the EF Core test suite and
   separately validate PostgreSQL on an *isolated test database*
   following the project's `BM_PG_TEST_CONNECTION` convention.
6. Do NOT run `dotnet ef database update`, DbMigrator or SQL against
   real production data as part of generation/testing.
7. Keep issuance, debt recognition, deposits, payments and remote draft
   storage disabled until authorization, business policy and audited
   invoice lifecycle are implemented.

## Still open

- The script and offline design-time mode have **not been executed here**
  because the working environment has no .NET 10 SDK or PowerShell.
- Migration generation, actual output review and isolated PostgreSQL
  validation remain to be performed locally. SQLite PASS is not proof
  that the eventual migration applies cleanly on PostgreSQL.
- Meter/tariff modifications do not share the same locking discipline
  as draft creation: final issuance needs atomic revalidation and
  concurrency tests. Tenant/contract split policy and operator
  permissions remain unresolved.

**Do not mark Phase 2G.3A ready for production or the issuance phase.**
