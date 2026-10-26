# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Next non-destructive persistence design slice — WIP (25/10/2026)

Added `InvoiceDraftRevisionArchiveCodec` as **offline/in-memory**
format foundation for historical revision evidence:

- Explicit `FormatVersion=1` archive JSON includes
  `InvoiceId`, original revision number, user GUID, UTC timestamp,
  correction reason and complete previous utility-only snapshot.
- Stable utility/source ordering and SHA-256 checksum over the exact
  UTF-8 JSON bytes. Decoder verifies checksum and re-runs the
  existing `InvoiceDraftRevisionEvidence.Capture` and domain
  snapshot constraints, then rejects noncanonical JSON.
- The checksum protects against accidental corruption when stored
  and checked separately. **It is not cryptographic authentication
  against a privileged actor who can rewrite both payload and hash.**
- No EF entity/DbSet/migration/application endpoint, HTTP route,
  current invoice mutation, PostgreSQL connection or issuance.

Added **9 new unit tests** in
`InvoiceDraftRevisionArchiveCodec_Tests` for roundtrip fidelity,
deterministic ordering, corrupt/missing checksums,
actor/reason scope, unsupported format, malformed JSON,
noncanonical rehashed JSON and no mutation of old evidence.
These new tests have **NOT yet run locally**.

Created a separate review document:
`etc/phase-audits/Phase2G3A7_RevisionLedger_MigrationRFC.md`.
It specifies draft-only additive DDL, append-only ledger separation
from active source uniqueness, CurrentRevision+ConcurrencyStamp,
contract→invoice row-lock ordering, loser-aborts concurrent rebuild
logic, transaction rollback guarantees and migration review gates.
**SQL is illustrative and marked DO NOT RUN.**

Next local unit command:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests|FullyQualifiedName~InvoiceDraftRevisionArchiveCodec_Tests"
```

Expected domain revision cases: **18/18** (previous REV 9 +
new ARCH 9), **not yet verified as a combined run**. If the
archive codec's compile/runtime cases fail, correct source first
and do not proceed to live EF migrations.

## PGREV04 runtime fixture VARCHAR(32) failure and fix — 26/10/2026

The user pulled the previous fix, ran the guarded
`test-invoice-revision-full-chain-local.ps1`, and supplied the
full 143-line local output:

```
BuildingManagement.PostgreSql.Tests net10.0 succeeded (compiled)
PGREV04: FAIL at "Seed synthetic FK parent and invoice records"
Npgsql.PostgresException: SQLSTATE 22001
value too long for type character varying(32)
PGREV03: PASS
Test summary: total: 2, failed: 1, succeeded: 1, skipped: 0
Build failed with 1 test error and 4 Mapperly RMG012 warnings
```

**Root cause is the synthetic RoomNumber fixture**, not the
migration's revision trigger/rollback behavior:
`'REV-' + Guid.NewGuid():N` produced 36 characters, but the
`BuildingManagementRooms.RoomNumber` column is
`character varying(32)`. PGREV04 never reached its concurrent
rollback assertion.

Fixed `InvoiceRevisionPopulatedRollbackPostgreSql_Tests.cs` at
commit `3e3aae14705374eed2b26b7602464a70373e2c5b`:

- Generate `roomNumber` as `"REV-" + Guid.NewGuid().ToString("N")[..8]`
  (12 characters).
- Assert `roomNumber.Length <= 32` in test setup.
- Pass the fixture as `'{{roomNumber}}'` in the `$"""` C# raw
  interpolated SQL.
- Leave `ContractNumber` 36-character fixture unchanged because
  that field allows 64 characters.
- No migration, existing application database or finance logic
  was modified. PGREV03 retains its last known passing result.

**Fixed source not yet re-tested** on the user's Windows environment.
Rerun guarded PGREV03/04 launcher after a fast-forward pull; target
2/2 PASS. Any new error must be diagnosed at its own failing stage.

## PGREV04 C# build failure and source fix — 26/10/2026

User pulled `7f608a1..bf90604` and ran
`test-invoice-revision-full-chain-local.ps1`.
The test project did NOT compile:

```
InvoiceRevisionPopulatedRollbackPostgreSql_Tests.cs:
(112,30) CS9006
(112,32), (116,74), (122,48), (128,56), (134,34) CS1733
Build failed with 6 error(s) and 4 warning(s)
```

**Not a PostgreSQL failure; PGREV03/PGREV04 did not start in
this invocation, and no scratch database was created by these tests.**
The compile error came from the synthetic FK fixture's SQL,
which used an interpolated raw string starting with one
`# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Next non-destructive persistence design slice — WIP (25/10/2026)

Added `InvoiceDraftRevisionArchiveCodec` as **offline/in-memory**
format foundation for historical revision evidence:

- Explicit `FormatVersion=1` archive JSON includes
  `InvoiceId`, original revision number, user GUID, UTC timestamp,
  correction reason and complete previous utility-only snapshot.
- Stable utility/source ordering and SHA-256 checksum over the exact
  UTF-8 JSON bytes. Decoder verifies checksum and re-runs the
  existing `InvoiceDraftRevisionEvidence.Capture` and domain
  snapshot constraints, then rejects noncanonical JSON.
- The checksum protects against accidental corruption when stored
  and checked separately. **It is not cryptographic authentication
  against a privileged actor who can rewrite both payload and hash.**
- No EF entity/DbSet/migration/application endpoint, HTTP route,
  current invoice mutation, PostgreSQL connection or issuance.

Added **9 new unit tests** in
`InvoiceDraftRevisionArchiveCodec_Tests` for roundtrip fidelity,
deterministic ordering, corrupt/missing checksums,
actor/reason scope, unsupported format, malformed JSON,
noncanonical rehashed JSON and no mutation of old evidence.
These new tests have **NOT yet run locally**.

Created a separate review document:
`etc/phase-audits/Phase2G3A7_RevisionLedger_MigrationRFC.md`.
It specifies draft-only additive DDL, append-only ledger separation
from active source uniqueness, CurrentRevision+ConcurrencyStamp,
contract→invoice row-lock ordering, loser-aborts concurrent rebuild
logic, transaction rollback guarantees and migration review gates.
**SQL is illustrative and marked DO NOT RUN.**

Next local unit command:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests|FullyQualifiedName~InvoiceDraftRevisionArchiveCodec_Tests"
```

Expected domain revision cases: **18/18** (previous REV 9 +
new ARCH 9), **not yet verified as a combined run**. If the
archive codec's compile/runtime cases fail, correct source first
and do not proceed to live EF migrations.

 but contained JSON `'{{}}'` and interpolation tokens
incompatible with that raw-string brace syntax.

Fixed the test fixture on the WIP GitHub branch in commit
`0e58f275c3cf3604f2fdb77d4f686008d572e850`:

- Changed `$"""` to `$"""` so real C# interpolations
  use `{{expression}}` and JSON text uses literal `'{}'`.
- Preserved the same random fixture IDs and the reviewed
  V2 migration. No database schema or migration edits.

**Fix is source-reviewed, NOT YET built on user's machine.**
Rerun the same dedicated guarded PowerShell script after
`git pull --ff-only origin wip/phase-2g2`. Goal PGREV03/04
**2/2 PASS**, do not claim success until a fresh local log.

## PGREV04 full EF populated-ledger concurrency gate — WIP (26/10/2026)

Added the separately opt-in real PostgreSQL test
`InvoiceRevisionPopulatedRollbackPostgreSql_Tests.cs`
(`PGREV04_Concurrent_Insert_Blocks_Down_And_Preserves_History`).
The previously tested PGREV03 and new PGREV04 share an xUnit
collection, so their disposable-database rehearsals run sequentially.
Updated the existing safe launcher:

`etc/scripts/test-invoice-revision-full-chain-local.ps1`

It now executes both `InvoiceRevisionFullChainPostgreSql_Tests`
and `InvoiceRevisionPopulatedRollbackPostgreSql_Tests`
in a single invocation.

**PGREV04 design:**

1. Require explicit local scratch-DB opt-in, exact previously
   reviewed Up/Down SQL SHA-256, expected newest V2 EF migration,
   and the existing strict connection guard:
   `localhost:5432 / ngoctrang_integration_test`.
   Require a PostgreSQL test role with CREATEDB/SUPERUSER.
2. Create **one fresh random**
   `ngoctrang_revision_race_<uuid>` scratch database;
   apply the **entire actual EF migration chain**, with no EF
   changes to the pre-existing integration or application DB.
3. Insert only a synthetic building/floor/room/contract and
   invoice header in that scratch database, using new random IDs;
   create one valid archived previous draft evidence blob.
   (Synthetic parents are for FK testing, not normal billing.)
4. Connection A begins a transaction and inserts the archive
   without committing. Connection B invokes a real EF
   `MigrateAsync(Phase2G3A4)` downgrade. Query
   `pg_stat_activity` to verify B actually waits for a lock
   before A commits.
5. Commit A. Verify B's downgrade fails with the explicit
   populated-ledger guard, and confirm the historical record,
   two enabled protection triggers, migration history row and
   active invoice source table remain intact.
6. In `finally`, close all scratch connections and drop **only**
   the database created by this test. Verify absence.

**NOT YET RUN on the user's machine.** This new C# test may
surface a missing DB default/constraint or provider-specific
migration lock behavior; correct any genuine failure before
approving any live migration. PGREV03's **1/1** result from the
previous invocation remains verified separately.

Run only using the existing script, which preserves the user's
uncommitted generated V2 migration/Designer/ModelSnapshot/SQL:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-invoice-revision-full-chain-local.ps1
```

Expected combined `PGREV03+PGREV04`: **2/2** (NOT VERIFIED).
This still does not establish authorization for rebuild, rate
or source edits, an end-to-end recognized-debt ledger, database
privilege separation for actual operators, or production readiness.

## PGREV03 full-chain local PostgreSQL PASS — 26/10/2026

The user invoked the dedicated opt-in disposable-DB full EF chain
rehearsal `test-invoice-revision-full-chain-local.ps1` and reported:

```
Test Run Successful.
Total tests: 1
     Passed: 1
Total time: 7.2343 Seconds
BuildingManagement.PostgreSql.Tests test net10.0 succeeded (7.7s)
Test summary: total: 1, failed: 0, succeeded: 1, skipped: 0, duration: 7.7s
Build succeeded with 4 warning(s) in 26.6s
PGREV03 PASSED; random scratch database created and deleted.
```

**PGREV03: 1/1 PASS.** Alongside PGREV01/02 **2/2 PASS**,
this gives **3/3 tested PostgreSQL revision-ledger scenarios**,
but across two separate invocations, NOT one combined test run.

The opt-in test runs the complete real EF migration chain against a
random disposable localhost PostgreSQL database, rechecks upgrade
idempotency, downgrades an **empty** ledger and re-upgrades it, with
the scratch database removed at the end. It does NOT demonstrate
that migrating a populated existing business database is safe, nor
test an EF downgrade against archived rows or concurrent writers.
The four build warnings are consistent in count with the previously
identified Mapperly RMG012 baseline; the abbreviated PGREV03 log
alone does not list warning codes.

The generated V2 EF migration and its Designer/ModelSnapshot/SQL
remain only in the user's uncommitted local worktree; no PostgreSQL
application migration, issuance or posting was authorized.

## Full EF migration chain safety rehearsal — PGREV03 verified (26/10/2026)

Following the locally confirmed **PGREV01/02 2/2 PASS** in a
transaction-local schema, added a new, separately opt-in test:

- `BuildingManagement.PostgreSql.Tests/Invoices/InvoiceRevisionFullChainPostgreSql_Tests.cs`
  with **PGREV03** `Full_Migration_Chain_Up_EmptyDown_Up_In_Disposable_Database`.
- `etc/scripts/test-invoice-revision-full-chain-local.ps1`
  sets scoped environment variables using the existing `.env`
  local PostgreSQL credentials, without echoing secrets.

**Scope and test stages:** The test requires the unchanged two
reviewed SQL exports by exact byte SHA-256; compiles and discovers
the actual locally generated `20261025150750_Phase2G3A7` EF
migration (expected newest); and only then connects through the
previously established `RequireSafeConnectionString`
localhost/port 5432/`ngoctrang_integration_test` guard. It
requires the PostgreSQL test role to have CREATEDB privileges
before any database creation.

It creates **one fresh random** `ngoctrang_revision_rehearsal_<uuid>`
database on local PostgreSQL, applies the complete EF migration
chain from empty, repeats migration to confirm idempotency,
checks ledger table, active source table, FK RESTRICT, unique
revision index, 5 CHECK constraints, 2 enabled trigger guards
and the PostgreSQL function. It then downgrades **only the
empty** revision-ledger migration to previous Phase2G3A4,
proves existing invoice table and migration history remain,
then upgrades to the latest again and rechecks the ledger.
Finally, it drops only this just-created scratch DB via
`finally` and verifies its absence.

**No existing app/integration DB is migrated or dropped**,
and no financial posting is enabled. The script does **not**
require a clean worktree; do not reset local V2 migration,
Designer, model snapshot or reviewed SQL to run it.

**NOT EXECUTED YET.** Assistant-side C# compile and actual
PostgreSQL execution are unavailable. The test may reveal
migration-chain compatibility failures and must not be reported
PASS before user logs confirm it.

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-invoice-revision-full-chain-local.ps1
```

**1/1 PASS** verified from user's 26/10/2026 local output.
If it fails, read stage/cleanup output and fix before
any PostgreSQL application DB migration. A successful
PGREV03 still does NOT prove privileged-role immutability,
populated rollback under EF `MigrateAsync`, cross-transaction
concurrency, or authorized real invoice revision behavior.

## PGREV01/02 verified locally — 26/10/2026

User ran `etc/scripts/test-invoice-revision-ledger-sql-local.ps1`
after fetching the two guarded SQL replay tests and reported:

```
Test Run Successful.
Total tests: 2
    Passed: 2
Total time: 5.5118 Seconds
BuildingManagement.PostgreSql.Tests test net10.0 succeeded (6.0s)
Test summary: total: 2, failed: 0, succeeded: 2, skipped: 0, duration: 6.0s
Build succeeded with 4 warning(s) in 25.9s
PGREV01/02 PASSED. Test-only PostgreSQL transactions were rolled back.
```

**2/2 PASS** verifies the checksum-pinned, independently
reviewed **Up and Down SQL**, replayed in a transaction-local
random PostgreSQL schema on the guarded local test database.
These tests cover row UPDATE/DELETE protection, TRUNCATE,
unique revision, invalid hex digest, restricted FK, nonpositive
revision, populated rollback rejection/preservation and empty
rollback success/cleanup. They do NOT verify the entire EF
migration chain, production role permissions, or concurrent
transactions. The final test output reports only the
four known Mapperly warnings.

User's V2 patched migration plus Designer/Model Snapshot and
generated SQL remain **uncommitted locally**, absent from the
GitHub branch. Do not reset, stash, overwrite or drop those
files to "clean" the working tree. No migration is authorized
to run on an application database.

## PostgreSQL transaction-local SQL rehearsal — IMPLEMENTED / VERIFIED 2/2 PASS (26/10/2026)

Added `InvoiceRevisionLedgerSqlPostgreSql_Tests` under
`BuildingManagement.PostgreSql.Tests/Invoices`, with two
**opt-in** real PostgreSQL scenarios and a PowerShell launcher
`etc/scripts/test-invoice-revision-ledger-sql-local.ps1`.

- **PGREV01:** Use the exact reviewed forward SQL, then store
  one synthetic historical archive and assert database rejection
  for UPDATE, DELETE, TRUNCATE, duplicate (InvoiceId, Revision),
  non-hex SHA-256 CHECK, invalid invoice FK and revision 0.
  Replay the exact reviewed Down SQL against a **populated**
  revision ledger and assert PostgreSQL raises, leaving table,
  archived record, triggers and EF history entry intact.
- **PGREV02:** Replay exact forward and reverse SQL with an
  **empty** ledger, assert ledger table, guard function and its
  EF history row are removed while the synthetic parent invoice
  test table remains available.
- Reviewed SQL files are supplied from the user's local checkout
  via `BM_PHASE2G3A7_UP_SQL` and
  `BM_PHASE2G3A7_DOWN_SQL`, but require exact filenames
  **and SHA-256 of previously reviewed uploads** before a DB
  connection is opened. The SQL wrapper START/COMMIT is stripped
  so the real SQL body runs inside the test's **single**
  outer rollback-only Npgsql transaction.
- The only database permitted by the existing
  `BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString`
  guard is **localhost:5432/ngoctrang_integration_test**.
  The tests do **NOT** inherit ABP's auto-`EnsureCreated`
  module. Each creates a fresh random schema with only
  synthetic parent invoice/migration-history stubs, sets
  `search_path` to that schema **without public**, and
  unconditionally rolls back after execution. PostgreSQL
  savepoints restore the transaction following each deliberate
  error. A final check requires zero leftover schema.
- The launcher reuses the existing local `.env` credentials
  without printing values; unlike the older full migration-chain
  launcher, it does **not** demand a clean worktree, because the
  generated migration/Designer/Snapshot/SQL files are pending
  review and must remain untouched.
- No `dotnet ef database update`, `DbMigrator`,
  `CREATE DATABASE`, `DROP DATABASE`, or production DB
  migration is invoked.

**Unverified: zero local PostgreSQL runs yet.** The source was
reviewed statically but cannot be compiled or run in the assistant
environment. The user must pull the latest WIP branch (taking care
not to discard local migration files) and run:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-invoice-revision-ledger-sql-local.ps1
```

Expected: **2/2 PASS**, but no success claim until the test
output is supplied and reviewed. This test does **not** prove full
EF migration-chain compatibility, production-role privilege
separation, cross-process concurrency (insert-versus-rollback),
or the future rebuild transaction's exact revision/stamp behavior.
Those remain hard blockers before approval to apply the migration.

## V2 rollback SQL uploaded and statically reviewed — 26/10/2026

User supplied actual 838-byte local SQL file
`Phase2G3A7_RevisionLedger_Down_Review.sql`.
All 29 SQL lines were read and checked against the prior V2
migration C# and forward SQL.

**Reverse SQL / Down(): STATIC REVIEW PASS**

1. `START TRANSACTION;` begins one rollback transaction.
2. `LOCK TABLE "BuildingManagementInvoiceDraftRevisions"
   IN ACCESS EXCLUSIVE MODE;` occurs **before** `IF EXISTS`.
3. `DO $revision_rollback$ ... IF EXISTS (SELECT 1 ... LIMIT 1)
   THEN RAISE EXCEPTION ... END` rejects rollback if archived
   data remains. The error aborts the transaction in PostgreSQL.
4. Only after the check, `DROP TRIGGER` removes exactly the row
   `ProtectRow` and statement `ProtectTruncate` triggers.
5. Then `DROP FUNCTION
   "bm_block_invoice_draft_revision_mutation"()`, and
   `DROP TABLE "BuildingManagementInvoiceDraftRevisions"`.
6. The final schema bookkeeping action is the removal of exactly
   this migration's `__EFMigrationsHistory` entry,
   followed by `COMMIT`.

No DDL/DML on existing active invoices, invoice items, source
measurements or payments appears in this **reverse** SQL.

PostgreSQL official documentation supports that
`ACCESS EXCLUSIVE` conflicts with all other lock modes and
that a table lock is held through transaction end. Therefore,
assuming the SQL runs as a single PostgreSQL transaction,
the empty-check-before-drop insert race is addressed.

**NOT a PostgreSQL execution PASS.** Neither `Up` nor `Down`
has been exercised on a disposable PostgreSQL instance. Remaining
required gates: real trigger UPDATE/DELETE/TRUNCATE rejection,
SHA-256 hex CHECK and duplicate revision constraint enforcement,
FK RESTRICT, empty-table rollback, populated-table rollback error
with preservation of table/archive and EF history row, concurrency
insert versus rollback, least-privilege role setup and failure
rollback. The user's local V2 migration and generated files are
still uncommitted and must remain protected; do not confuse this
audit-only GitHub commit with the actual generated migration.

No `database update`, `DbMigrator`, or SQL migration should run
on application or production data. Proceed with separate disposable
database test design and explicit target verification only.

## User-local V2 rollback migration build and SQL export — 26/10/2026

The user manually extracted `Phase2G3A7_Reviewed_Migration_Patch_v2.zip`,
backed up the previous migration to `Migration_before_V2.cs` in
a temporary directory, and copied the V2 migration C# file into
the uncommitted local EF migrations directory. They reported:

```
dotnet build BuildingManagement.EntityFrameworkCore.csproj --no-restore
Build succeeded in 11.8s
```

Then, with `BM_INVOICE_MIGRATION_OFFLINE=1`, they generated a
**reverse** EF SQL script:

```
dotnet ef migrations script
    20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger
    20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots
    --context BuildingManagementDbContext
    --output etc/phase-audits/Phase2G3A7_RevisionLedger_Down_Review.sql
Build started...
Build succeeded.
```

The offline environment flag was removed in a `finally` clause.
No database connections or writes are reported in this log.
The V2 C# **compiles successfully**; however, the generated
`Phase2G3A7_RevisionLedger_Down_Review.sql` is still present
**only on the user's local checkout and has NOT been reviewed**.

**Next safety gate:** inspect the exact reverse SQL before any
scratch PostgreSQL use. It must acquire an `ACCESS EXCLUSIVE`
table lock *before* the empty-ledger test, raise if any archive
row exists, and avoid touching existing invoice/utility/payment
tables. Confirm that both phases run transactionally, and review
any cleanup operations on the ledger's triggers/function.
Do not infer behavior from build success or script generation.
The application DB migration, destructive rebuild, invoice issue
and debt posting remain DISABLED.

## Static review of uploaded patched forward SQL — 26/10/2026

The user supplied the actual local
`Phase2G3A7_RevisionLedger_Patched_Review.sql`. The assistant
read the entire 2.2 KB forward SQL text and compared it against
the source of the earlier reviewed migration patch.

**Forward Up SQL: PASS (static review, not execution).**

- One `CREATE TABLE "BuildingManagementInvoiceDraftRevisions"`
  with nonnull data, PK and restrictive FK to
  `BuildingManagementInvoices`.
- One unique index `UX_BM_InvoiceDraftRevision_Invoice_Number`
  over (`InvoiceId`, `ArchivedRevision`).
- EF generated 4 base CHECK constraints plus PostgreSQL-specific
  `CK_BM_InvoiceDraftRevision_HexSha256`
  `CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}

The user manually replaced only the locally generated
`20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs`
with a reviewed-but-unverified protection patch, while saving
`OriginalMigration.cs` in their temporary local backup folder.

A first command line encountered a transient `dotnet` command
recognition error (`^Bdotnet` pasted); the next identical build
succeeded:

```
dotnet build BuildingManagement.EntityFrameworkCore.Tests.csproj
  --no-restore -t:Rebuild -v minimal
Build succeeded with 5 warning(s) in 16.0s
```

**All five compiler warning sources are now identified:**

- Four pre-existing Mapperly `RMG012` warnings on
  `TenantDto.CreatorUserId/LastModifierUserId`.
- One `xUnit2000` in
  `InvoiceDraftRevisionArchiveCodec_Tests.cs:176`
  (`ARCH09`), where `Assert.Equal(actual, 1)`
  reverses expected/actual order. This has been corrected
  **on the remote WIP branch** to `Assert.Equal(1, actual)`
  in commit `5567ec7`. The user's local uncommitted
  migration must be preserved before any subsequent pull.

The user next ran `dotnet ef migrations script` under
`BM_INVOICE_MIGRATION_OFFLINE=1`, starting from
`20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`
through `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger`.
The CLI reported `Build succeeded` and generated the file
`etc/phase-audits/Phase2G3A7_RevisionLedger_Patched_Review.sql`
**on the user's local machine only**. The environment override
was then cleared.

**Pending proof:** The patched SQL file has **not been uploaded or
read** in this audit. A successful offline export alone is not
a PostgreSQL DDL execution test or proof that the row and statement
triggers, hexadecimal CHECK and non-destructive rollback are
correct. Do not run `database update`, `DbMigrator`, `psql`
to apply the preview, or commit/rebase generated files blindly.
The next gate is inspection of the exact patched SQL and separately
provisioned scratch database tests under explicit safety controls.
The real mutating draft rebuild and financial issuing remain BLOCKED.

## Manual review of uploaded generated migration bundle — 25/10/2026

User uploaded `Phase2G3A7_ReviewBundle.zip` containing the **four**
local generated files, and the assistant read their actual contents:

- `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs`
- `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs`
- `BuildingManagementDbContextModelSnapshot.cs`
- `Phase2G3A7_RevisionLedger_Generated_Review.sql`

### Verified structure

- Generated `Up()` contains **one** `CreateTable` for
  `BuildingManagementInvoiceDraftRevisions`, **one** unique
  index `UX_BM_InvoiceDraftRevision_Invoice_Number`
  on (`InvoiceId`, `ArchivedRevision`), restrictive invoice
  FK and four basic `CHECK` constraints.
- Offline SQL mirrors these additive objects; it does not alter
  existing invoice, item, meter, rate or payment tables.
- Designer and Model Snapshot contain matching model payloads,
  with only EF-expected migration class/attribute differences.
  The auto-shortened 63-character FK identifier ending in `~`
  is a valid PostgreSQL identifier, not a typo.
- SQL emitted includes an EF migrations history insert.
  It is **not** idempotent and must **not** be executed on a
  populated application database during review.

### Required corrections before any rollout

1. `Up()` lacks PostgreSQL UPDATE/DELETE protection for archived
   evidence, and `TRUNCATE` requires a distinct statement trigger
   because row triggers do not fire for TRUNCATE.
2. `Down()` uses unconditional `DropTable`, which would
   irreversibly destroy historical revisions. Require an empty-ledger
   rollback guard; test on an isolated PostgreSQL scratch DB.
3. The lower-case/64-character checksum CHECK is **not** sufficient
   hexadecimal validation. Add PostgreSQL hex CHECK
   `^[0-9a-f]{64}# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Next non-destructive persistence design slice — WIP (25/10/2026)

Added `InvoiceDraftRevisionArchiveCodec` as **offline/in-memory**
format foundation for historical revision evidence:

- Explicit `FormatVersion=1` archive JSON includes
  `InvoiceId`, original revision number, user GUID, UTC timestamp,
  correction reason and complete previous utility-only snapshot.
- Stable utility/source ordering and SHA-256 checksum over the exact
  UTF-8 JSON bytes. Decoder verifies checksum and re-runs the
  existing `InvoiceDraftRevisionEvidence.Capture` and domain
  snapshot constraints, then rejects noncanonical JSON.
- The checksum protects against accidental corruption when stored
  and checked separately. **It is not cryptographic authentication
  against a privileged actor who can rewrite both payload and hash.**
- No EF entity/DbSet/migration/application endpoint, HTTP route,
  current invoice mutation, PostgreSQL connection or issuance.

Added **9 new unit tests** in
`InvoiceDraftRevisionArchiveCodec_Tests` for roundtrip fidelity,
deterministic ordering, corrupt/missing checksums,
actor/reason scope, unsupported format, malformed JSON,
noncanonical rehashed JSON and no mutation of old evidence.
These new tests have **NOT yet run locally**.

Created a separate review document:
`etc/phase-audits/Phase2G3A7_RevisionLedger_MigrationRFC.md`.
It specifies draft-only additive DDL, append-only ledger separation
from active source uniqueness, CurrentRevision+ConcurrencyStamp,
contract→invoice row-lock ordering, loser-aborts concurrent rebuild
logic, transaction rollback guarantees and migration review gates.
**SQL is illustrative and marked DO NOT RUN.**

Next local unit command:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests|FullyQualifiedName~InvoiceDraftRevisionArchiveCodec_Tests"
```

Expected domain revision cases: **18/18** (previous REV 9 +
new ARCH 9), **not yet verified as a combined run**. If the
archive codec's compile/runtime cases fail, correct source first
and do not proceed to live EF migrations.

 as database-only defense in depth, acknowledging
   that a rewrite of JSON *and* checksum by privileged personnel
   still bypasses SHA-256's tamper detection.
4. The approved future rebuild workflow still needs current
   revision/stamp, transaction lock order, building-scoped
   authorization, rollback-on-conflict and PostgreSQL concurrency
   tests. This scaffold does **none** of those.
5. The fifth compiler warning is still unidentified from the
   supplied summary; capture full diagnostic lines.

A separate **review-only C# patch** to the generated migration
was produced in the current conversation, adding a regex check,
row UPDATE/DELETE protection, statement-level TRUNCATE protection
and a rollback empty-table guard. This patch is **not committed to
GitHub**, because the generated files currently exist only in the
user's Windows working tree. It is **uncompiled and untested on
PostgreSQL**; it must be reviewed/recompiled before any scratch
database test.

**Status: migration structural review PASSED with blockers;
production application and financial issuance remain BLOCKED.**

## Offline migration scaffold generated locally — 25/10/2026 (unreviewed)

User confirmed working tree initially clean, checked out branch
`wip/phase-2g2`, fast-forwarded
`7c27aa5..c2c6927`, and successfully executed
`prepare-invoice-revision-ledger-migration-offline.ps1`.

The log explicitly reported:

```
SAFE MODE: only scaffold local EF migration files and generate OFFLINE SQL.
Build succeeded.
OFFLINE MIGRATION SCAFFOLD + BASIC SQL REVIEW COMPLETED.
Migration ID: 20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger
NOT APPROVED TO APPLY
```

The generated files exist on the **user's local Windows checkout**,
**not in the connected GitHub branch**. Final
`git status --short` reported exactly these four changes:

```
 M modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/BuildingManagementDbContextModelSnapshot.cs
?? etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql
?? modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs
?? modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs
```

**Basic offline scaffold checks PASSED, but full SQL/source review
has NOT happened**: the generated migration, Designer, model snapshot,
and SQL preview must be read before approving any code/migration
change. Do not infer that the PostgreSQL trigger or populated-table
rollback protection exists; the generator does not implement these.

Next review gate: collect those four exact files, check generated
Up/Down and full EF model snapshot, validate PK/FK/index/check
constraints and source table independence, and identify the exact
**fifth** build warning (older baseline was four RMG012 warnings).
Do not run any EF database update, DbMigrator, psql apply or schema
mutation; preserve the generated local files as-is until reviewed.

## Offline scaffold helper implementation (subsequently run on 25/10/2026)

- Added `etc/scripts/prepare-invoice-revision-ledger-migration-offline.ps1`.
  It scaffolds a local EF revision-ledger migration and a local
  PostgreSQL SQL text preview under strict clean-WIP-branch and
  design-time offline flag checks. The PostgreSQL design-time factory
  uses an intentionally unreachable `127.0.0.1:1` connection string.
- It validates that the new migration `Up()` only creates the
  `BuildingManagementInvoiceDraftRevisions` table, and that the
  SQL preview includes exactly that new table and unique
  (InvoiceId, ArchivedRevision) index.
- **NOT executed** on the user's Windows machine. Generated migration
  files and SQL cannot yet be claimed or cited as reviewed output.
  The assistant's runtime has no .NET SDK available for scaffolding.
- The script only **generates local files**; it does not apply any
  SQL or alter any database. The generated scaffold will lack the
  required PostgreSQL UPDATE/DELETE protection and populated-ledger
  rollback guard; it is **NOT release approved**.
- `Phase2G3A7_RevisionLedger_MigrationRFC.md` now explicitly
  documents the offline command, expected files, manual approval
  criteria and five-warning investigation.

Do not use `dotnet ef database update`, `DbMigrator` or `psql`
to apply this schema. The next gate is generated source/SQL review
and inspection of the extra compiler warning.

## Staged revision ledger SQLite integration gate — 25/10/2026

The user ran the full filtered
`InvoicePersistenceIntegration_Tests` class after adding the
staged EF revision ledger model and reported:

```
Test summary: total: 13, failed: 0, succeeded: 13, skipped: 0, duration: 10.6s
Build succeeded with 5 warning(s) in 25.7s
```

**INVDB01–13: 13/13 PASS** verifies the existing invoice persistence
tests plus three new SQLite cases covering revision-record insert
and rehydration, prevention of duplicate archived revision numbers
and two distinct archived versions for the same invoice.

**Warning investigation remains open:** Earlier builds showed four
known RMG012 tenant mapping warnings. This build reports **five**
warnings, but the user supplied only a summary, not diagnostic
codes/locations. Do not classify the fifth as harmless or new until
its exact compiler warning has been reviewed.

**Scope limitation:** SQLite's schema creation from the current EF
model is *not* proof that a real PostgreSQL revision table exists.
The staged model has no generated EF migration in the repository
as of this verification; there is no append-only PostgreSQL
UPDATE/DELETE trigger or authorized revision writer. The model
must not be used to archive actual invoices on a populated database.

## Phase 2G.3A.7 — staged EF persistence model (WIP / unverified)

After the **18/18 PASS** archive roundtrip results, the repository
now includes a **staged** EF model for storing historical draft
evidence *separately* from active charge source rows:

- `InvoiceDraftRevisionRecord` (Domain) holds invoice ID, previous
  revision number, operator GUID, UTC archive instant, correction
  reason, exact canonical JSON and its SHA-256 digest.
  `Prepare` copies data from the already validated immutable
  `InvoiceDraftRevisionEvidence`; `VerifyAndRead` validates
  JSON/digest and cross-checks the decoded metadata against the
  record's metadata columns. This is NOT a new active invoice.
- `InvoiceDraftRevisionRecordConfiguration` (EF Core) maps a
  new separate table `BuildingManagementInvoiceDraftRevisions`
  with an invoice FK (RESTRICT), unique
  `(InvoiceId, ArchivedRevision)` and provider-neutral
  check constraints for revision number, reason, JSON length,
  and lowercase checksum length.
- The entity is registered via a `DbSet` and explicit mapping
  on `BuildingManagementDbContext`; application and HTTP services
  remain unchanged. No writer endpoint calls the new repository.
- `InvoicePersistenceIntegration_Tests` now has
  **INVDB11–13**, checking actual isolated SQLite write/reload,
  duplicate archived revision rejection, and ordered distinct
  revision numbers. Expect **13/13** invoice DB tests.

**IMPORTANT model/migration staging boundary:**

- **No migration file or EF model snapshot has been generated** for
  this new model. Until a reviewed EF migration exists and is applied
  in a dedicated safe environment, a PostgreSQL database will NOT
  have the revision table. Do **NOT** access it using the running
  application or execute DbMigrator against a populated database.
- We intentionally deferred the proposed
  `BuildingManagementInvoices.CurrentRevision` column until the
  migration/update policy and optimistic conflict protocol are
  finalized. The old invoice table and its indexes were not changed.
- A domain entity with private setters and a checksum **does not
  make the database append-only**. The future PostgreSQL migration
  must add a tested reject-UPDATE/DELETE trigger or an equivalent
  independently enforced SQL privilege boundary; SQLite tests
  do not prove database immutability.
- A SHA-256 digest is a corruption check, NOT a cryptographic
  signature against a privileged database actor.
- No actual automated rebuild/cancel/issue/receivable transition
  exists or was enabled.

### Next local test gate — NOT YET EXECUTED

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoicePersistenceIntegration_Tests"
```

Expected `INVDB01–13`: **13/13 PASS — verified** (25/10/2026).
Only after this gate and a reviewed schema design should the
actual EF migration be generated, entirely offline (no DB connection
and no update). Preserve earlier 18/18 domain result separately.

## REV01–09 local verification — 25/10/2026

User ran `InvoiceDraftRevisionEvidence_Tests` after pulling
the WIP branch and reported:

```
Test summary: total: 9, failed: 0, succeeded: 9, skipped: 0, duration: 3.5s
Build succeeded in 10.2s
```

**9/9 PASS** confirms the in-memory old-draft evidence model's
metadata, independent copies, read-only collections and validation
behavior. The build reported success, with no warnings in the
supplied summary. This does **not** verify database persistence,
revision uniqueness, concurrent rebuild, or operator authorization.

## Immutable old-draft evidence prototype — test verified (25/10/2026)

Added a **non-persisted** Domain model
`InvoiceDraftRevisionEvidence.Capture`, preparing the shape of
historical data to retain *before* a future approved draft rebuild.
This is a pure in-memory value model, **not** an EF entity and
**not** a new invoice revision feature. It cannot write the
database, update current draft rows, release source uniqueness or
recognize a receivable.

Required archive metadata:

- Stable parent `InvoiceId`, positive `ArchivedRevision`,
  identified `ArchivedByUserId` and UTC `ArchivedAtUtc`.
- Correction reason trimmed to 8–500 characters.
- Prior `InvoiceDraftSnapshot` revalidated and copied by value.
  Complete electricity and water lines, source identifiers, physical
  meters, reading dates/counters, tariff rates and amounts preserved.
- Copies are wrapped in read-only collections and do not retain
  caller-owned line/source list containers.
- A prior unpriced/incomplete utility-only draft is rejected as
  archive evidence pending an explicit incomplete-draft policy.
- **No** posting, issuing, payments, cancel/supersede or invoice
  status transition.

New unit class
`BuildingManagement.Domain.Tests/Invoices/InvoiceDraftRevisionEvidence_Tests.cs`
includes **9** xUnit cases: metadata, deep copy, no mutable
collections, 2 invalid reasons, overly long reason, missing actor
or invoice ID, non-UTC timestamp/revision, and incomplete price.

New local gate (not yet executed):

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests"
```

**9/9 PASS verified** (reported user log on 25/10/2026).

### Outstanding design decisions before any persistence

1. Source-key ownership: historical revision evidence must be audit
   proof only, while active invoice source uniqueness remains
   enforced; do not blindly duplicate source keys into the active
   source table.
2. Invoice concurrency: specify the exact row-lock, optimistic
   revision/stamp check, and multi-writer conflict response.
3. Revision history table, FK constraints, unique
   (InvoiceId, ArchivedRevision), immutable evidence storage,
   UTC actor/reason metadata and rollback-ready migration.
4. Scope authorization and explicit operator policy for stale,
   unchanged, incomplete or superseded drafts.
5. Full PostgreSQL stored-draft/rebuild concurrency integration on
   an opt-in disposable DB before production migrations.
6. Do not assume the nine pure domain tests prove any of 1–5.

## Local verification — 25/10/2026 (current WIP revision `61823a2`)

The user ran `git pull --ff-only origin wip/phase-2g2`
(fast-forward `48e0077..61823a2`) followed by the
three exact tests prescribed in the previous audit:

```
Application.Tests [four invoice test filters]:
Test summary: total: 30, failed: 0, succeeded: 30, skipped: 0, duration: 1.7s
Build succeeded with 4 warning(s) in 15.7s

EntityFrameworkCore.Tests [InvoicePersistenceIntegration_Tests]:
Test summary: total: 10, failed: 0, succeeded: 10, skipped: 0, duration: 7.7s
Build succeeded in 16.4s

PostgreSql.Tests [-Scenario Isolation, PGINV02/03/07/08]:
Test summary: total: 6, failed: 0, succeeded: 6, skipped: 0, duration: 16.2s
Build succeeded in 25.1s
```

**Verified: 46/46 PASS across 3 separate invocations** (30 + 10 + 6)
with no failed or skipped test. The four Mapperly RMG012 warnings
in the Application build are pre-existing.

This validates only the **read-only** versioned-draft preflight,
stored-draft preview through SQLite, and PostgreSQL isolation
rejection before unsafe SQL. No version history table, no mutating
rebuild workflow, no issuance, and no populated application database
migration were tested or enabled. The earlier 323/323 unfiltered
regression log is from an unspecified test assembly and predates
this latest slice.

## Phase 2G.3A.7 read-only rebuild preflight — implementation slice (WIP)

The earlier versioned-draft recommendation has been implemented **ONLY
as non-mutating preflight and internal preview**, not as an approved
rebuild, cancel, issuance or receivable transaction:

- Added `InvoiceDraftRebuildPreflight.Assess(saved, current, reason)`
  in the Application layer, with a result DTO and enum in
  Application.Contracts. This code rebuilds **in-memory evidence**
  using the existing `InvoiceDraftSnapshot.Capture` domain
  invariants and checks the existing saved draft before considering
  a new candidate. No tables are read or written by the pure helper.
- A stored invoice must have status Draft, exactly one electricity
  and one water line, consistent totals and original meter evidence.
  A malformed/ambiguous old draft is rejected rather than silently
  overwritten.
- A fresh candidate must belong to the **same contract, room and
  billing month**, with all rates, source identity, counter ranges,
  dates, amount arithmetic and coverage complete. Missing history,
  price or readings blocks preflight; changing a source ID even with
  the same amount is recognized as needing a revision.
- `AlreadyCurrent` is returned for an unchanged draft. A stale
  candidate requires an explicit operator reason of 8–500
  characters, trimmed before reporting.
- `ReadyForRevisionDesign` carries only proposed total and
  normalized reason. **It is not a durable authorization, a new
  invoice, or permission to mutate any existing row.**
- Exposed `PreviewRebuildAsync(id, reason)` solely through the
  existing **internal-only** `IInvoiceDraftStorageAppService`.
  The ABP root UOW is `RepeatableRead`; the same actual PostgreSQL
  isolation guard protects an ambient transaction from bypassing it.
  Saved evidence is retrieved and the current candidate rebuilt
  within this single read snapshot. Expected business-history
  blockers become `CurrentCandidateNotReady`; infrastructure
  errors still propagate.
- **No remote API, invoice status transition, billing, payment,
  schema migration, direct SQL or production database actions**.

### Test cases (confirmed 46/46 PASS above)

- `InvoiceDraftRebuildPreflight_Tests`: **14** xUnit cases,
  including ready/unchanged, reason validation, wrong scope,
  incomplete source history, tampered saved draft, invalid status,
  same-amount/different provenance, duplicates and bad counters.
- `InvoicePersistenceIntegration_Tests`: **INVDB09/10**,
  exercising actual SQLite internal preview after tariff correction
  and fail-closed response to a soft-deleted reading, without
  changing the previously persisted invoice.
- `InvoiceDraftIsolationGate_Tests`: one additional attribute
  case for the new `PreviewRebuildAsync`.
- `InvoiceDraftIsolationPostgreSql_Tests`: **PGINV08**
  rejects a preexisting ambient READ COMMITTED UOW before
  touching any invoice row.

Narrow expected counts (in **separate test assemblies**):

```powershell
git pull --ff-only origin wip/phase-2g2

dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRebuildPreflight_Tests|FullyQualifiedName~InvoiceDraftFreshnessEvaluator_Tests|FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoicePersistenceIntegration_Tests"

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Isolation
```

Expected counts: Application **30/30** (14+10+4+2);
EF Core **10/10**; PostgreSQL isolation **6/6**.
These three groups were **reported PASS** on 25/10/2026. Verify on the user's
Windows .NET 10 environment; no .NET SDK is available in the
assistant's current runtime for local compilation.

### Safe migration architecture (review before any DDL)

Keep the logical invoice ID and the two nonfiltered room/month and
contract/month uniqueness constraints. Keep the global **active**
source attribution uniqueness. For a future approved
`RebuildDraft` operation, add a new append-only revision ledger
containing the complete *previous* snapshot: immutable rate,
source ID/type, physical meter ID, dates, old/new counters, line
and total money, reason, revision number, UTC timestamp, actor ID
and linkage to the same invoice ID. The archived evidence must be
decoupled from the current-source uniqueness index but cannot claim
another charge.

In a single authorization-checked `RepeatableRead` or stricter
transaction: follow the existing contract→invoice row lock order;
validate expected revision/concurrency stamp; reconstruct the latest
candidate; re-run the preflight; append old evidence; atomically
replace only active draft line/source rows with new evidence; update
revision number; and commit or roll back everything. Design the
EF Core delete/insert order and indexes carefully to avoid source
uniqueness conflicts and false source reuse. Two simultaneous
rebuilds must not both commit.

**No schema was created and no mutating rebuild was added.**
Review migration/backfill and test on a disposable scratch DB
before touching any populated app DB. Never implement a blind
hard-delete or soft-delete bypass of invoice uniqueness.

## Latest unfiltered regression report — 2026-10-25

The user provided one unfiltered .NET 10 test-run summary after being
given both the Application and EntityFrameworkCore project test
commands:

```
Test summary: total: 323, failed: 0, succeeded: 323, skipped: 0, duration: 31.4s
Build succeeded in 40.3s
```

**323/323 PASS — one reported test invocation; no build warnings in
the supplied summary.** The log excerpt does **not identify the
executed .csproj or test assembly**. Therefore this is a positive
unfiltered regression result, but it must **not** be represented as
323/323 passing in BOTH projects or as all projects running. The
other project, if not already executed, still requires separate
confirmation. The test command itself, or each project's separate
test summary, is necessary to attribute the result precisely.

Previous same-revision narrower gate results remain:
Application invoice **15/15 PASS**, SQLite EF invoice **8/8 PASS**,
PostgreSQL isolation **5/5 PASS**; previous PostgreSQL concurrency
PGINV04–06 **3/3 PASS**.

No source or schema changes accompany this documentation update.

## Verified test baseline

- `InvoiceDraftFreshnessEvaluator_Tests`: **10/10 PASS**.
- `InvoiceDraftIsolationGate_Tests`: **3/3 PASS**.
- `InvoiceRemoteAccessGate_Tests`: **2/2 PASS**.
- `InvoicePersistenceIntegration_Tests`: **8/8 PASS** including
  actual saved-draft stale detection after price/reading revision.
- PostgreSQL PGINV02, 03 and 07: **5/5 PASS** isolation safety.
- PostgreSQL PGINV04–06: **3/3 PASS** concurrent direct SQL, application
  mutators and physical replacement candidate behavior.
- An unfiltered **323/323** regression report was received for one
  unspecified test assembly after the latest integration changes.
  It does not yet prove that both full test projects ran. Four existing
  tenant Mapperly warnings were reported in earlier builds.

## Source-code findings

| Current behavior | Evidence in code | Operational consequence |
| --- | --- | --- |
| Status model has only `Draft = 0` | `Domain.Shared/Invoices/InvoiceStatus.cs` | No Cancelled, Superseded, Issued or adjustment status exists |
| Aggregate has only `CreateDraft`; no Issue/Post/Delete/Replace | `Domain/Invoices/Invoice.cs` | No audited draft lifecycle or controlled revision |
| Internal draft service only has Create/Get/HasCurrentSourceSnapshot | `Application.Contracts/Invoices/IInvoiceDraftStorageAppService.cs` | No supported regenerate, delete, cancel, void, supersede workflow |
| Contract + billing period unique across ALL invoices | `UX_BM_Invoice_Contract_Period` | Can't simply insert a second draft for the same contract/month |
| Room + billing period unique across ALL invoices | `UX_BM_Invoice_Room_Period` | Can't create a new room-month draft under a different contract |
| Source Type + Source Id globally unique | `UX_BM_InvoiceSource_Global` | Can't reuse an old reading/replacement evidence source in another draft |
| Invoice FKs and item/source FKs use RESTRICT | invoice EF configurations | Naive hard-delete is unsafe and risks historical evidence |
| Historical monetary/source data is copied by value | InvoiceItem/InvoiceItemMeterSource | Original draft remains auditable, but can be stale after later mutations |
| Freshness check compares saved vs current candidate | `InvoiceDraftStorageAppService.HasCurrentSourceSnapshotAsync` | Reports only snapshot freshness at *time of read*, not later authorization |
| Internal-only `[RemoteService(IsEnabled = false)]` | InvoiceDraftStorageAppService + InvoiceDraftCandidateAppService | No conventional ABP HTTP invoice access; operator scope still not implemented |

## Confirmed business dead-end

1. An October utility draft is saved with readings and tariff A.
2. A permitted meter-reading correction or tariff amendment commits B.
3. `HasCurrentSourceSnapshotAsync(invoiceId)` correctly returns **false**.
4. There is currently no business operation to repair the draft or
   supersede it, and DB-level unique constraints reject simply creating
   another invoice for the same contract/room and billing month.
5. An operator must NOT use direct SQL delete, manually change amounts,
   or bypass the uniqueness constraints to "fix" the draft.
6. In particular, soft-delete alone cannot make the unique index
   release a contract-month or source key. The invoice aggregate is
   not modeled as `ISoftDelete` and the indexes are unfiltered.

This is a **real missing business capability**, not a failed
concurrency test. Existing immutable snapshots and constraints are
safe in isolation; the recovery workflow is not yet implemented.

## Recommended controlled policy (proposal, NOT implementation approval)

**Draft may be recalculated while still unissued, but past evidence
must remain independently auditable.** Prefer a single logical
invoice/room-month identity with versioned draft snapshots, not
untracked hard-delete-and-recreate.

Proposed semantics:

1. Keep one logical invoice per room/month, preserving
   `UX_BM_Invoice_Contract_Period` and
   `UX_BM_Invoice_Room_Period`.
2. Introduce a **draft revision history** with immutable versions
   (who, when, reason, source/rate evidence, old/new totals).
   Do not simply overwrite old source rows and erase evidence.
3. Make an operator-requested **RebuildDraft** action conditional on:
   Draft status, correct building/room authorization, explicit
   reason, current unissued state, no open financial posting, and a
   fully reconstructed new candidate.
4. Verify original draft is stale or that an explicit audit-approved
   recomputation reason exists. Run replacement atomically with
   PostgreSQL `REPEATABLE READ` or stronger and consistent
   lock ordering; do not create partial line/source graphs.
5. Design the interaction with the global source uniqueness index:
   only the active draft revision can own a source key, or use
   a separate immutable revision evidence table with a clear
   accounting-owner key. Do NOT drop global uniqueness on
   released/issued source attribution without accounting review.
6. Add an explicit cancellation/supersession policy: cancellation
   must preserve audit history; it must not implicitly create new
   charges or recognize debt; invoice numbering and released
   uniqueness need an approved invariant.
7. Confirm whether "stale" means simply warning, automatic invalidation,
   or automatic block on actions. **Conservative interim behavior:
   block financial issuance and require explicit operator review.**
8. Future Issue/Post is a **separate** transition and must check
   source freshness, permissions, invoice status and receivable
   constraints **within the same transaction as posting**; a
   historical `true` freshness response is never enough.

These are recommended design constraints, not a policy already
approved or implemented.

## Decisions and test gates before coding mutating draft API

- Can a draft be rebuilt more than once, and can it be rebuilt without
  source changes? Require reason and audit stamps.
- When a tenant moves out or a contract is changed, does a new
  contract own the full room-month? Current logic disallows split
  tenancy; maintain fail-closed until policy is approved.
- If a source has been included in a **future issued invoice**, must
  correction be via explicit credit/debit adjustment rather than
  draft rebuild? Yes as a conservative design principle; accounting
  implementation pending.
- Are completed/soft-deleted meter readings editable? Existing meter
  mutation policies must be preserved and separately audited.
- Reject accidental cross-building GUID access for read/rebuild;
  dedicated invoice permissions and host route discovery are required
  before turning remote service back on.
- Add PostgreSQL tests for:
  - Stale draft rebuild succeeds without losing old audit evidence.
  - Two simultaneous rebuild requests cannot both win.
  - Rebuild versus source-reading change or meter replacement is
    serialized or rejected with no mixed-version amounts.
  - Duplicate room/month and global source reuse still fail.
  - Failed rebuild transaction rolls back all new revisions.
  - Cancel/supersede and any future issue/post cannot violate
    payment or financial-history invariants.
  - Cross-building and unauthorized operators are denied.

## Current safe operating posture

- Leave `InvoiceStatus` draft-only.
- Keep both invoice services internal-only.
- Preserve all persisted evidence and unique indexes.
- Allow the existing read-only freshness query but do not construe
  `true` as permission to issue.
- Do not change schema or touch populated application DB until the
  lifecycle design and rollback migration are agreed.
- Confirm which assembly produced the new unfiltered 323/323 result;
  run the other full test assembly if it has not yet been executed.

**Phase 2G.3A.6:** targeted application/EF/PostgreSQL gates green.
**Phase 2G.3A.7:** correction/lifecycle governance OPEN.
**Production issuance / receivables:** BLOCKED.
)`.
- `bm_block_invoice_draft_revision_mutation()` is defined
  to raise on mutation. A BEFORE UPDATE OR DELETE FOR EACH ROW
  trigger and a separate BEFORE TRUNCATE FOR EACH STATEMENT
  trigger are present, per PostgreSQL trigger semantics.
- One migration history row insert for ID
  `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger`,
  inside START TRANSACTION / COMMIT.
- No DROP/DELETE/TRUNCATE or UPDATE of existing active invoice,
  utility source or payment tables in this forward SQL.

**Important remaining blocker: Down()/rollback concurrency.**
The supplied forward migration SQL does not include `Down()`;
this was instead inspected in the previous reviewed migration
patch. Although `Down()` first checks for an empty ledger and
raises on existing history, it did **not acquire a table lock
before the empty check**. A concurrent insert could race after
an empty check but before `DropTable`, destroying an archive.
Therefore the initial patch still needs correction.

A **review-only migration patch V2** was produced in the chat:
it adds `LOCK TABLE "BuildingManagementInvoiceDraftRevisions"
IN ACCESS EXCLUSIVE MODE;` at the start of `Down()`,
before the existing empty-ledger check. PostgreSQL retains this
lock through transaction end. This is intended to close the
race when the entire Down migration runs in one transaction;
the revised C# has **not yet been compiled or executed locally**.

**No rollout approval:** Only static forward SQL checks have
passed. The patch V2 is not on the user checkout or GitHub branch
yet. Confirm its exact generated reverse SQL, test triggers,
checks, FK, uniqueness, empty rollback and populated rollback on
a strictly isolated scratch PostgreSQL database, and enforce
runtime database privilege separation before deployment. The
mutating RebuildDraft and financial posting/issuance remain BLOCKED.

## Local patched migration build + offline SQL generation — 25/10/2026

The user manually replaced only the locally generated
`20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs`
with a reviewed-but-unverified protection patch, while saving
`OriginalMigration.cs` in their temporary local backup folder.

A first command line encountered a transient `dotnet` command
recognition error (`^Bdotnet` pasted); the next identical build
succeeded:

```
dotnet build BuildingManagement.EntityFrameworkCore.Tests.csproj
  --no-restore -t:Rebuild -v minimal
Build succeeded with 5 warning(s) in 16.0s
```

**All five compiler warning sources are now identified:**

- Four pre-existing Mapperly `RMG012` warnings on
  `TenantDto.CreatorUserId/LastModifierUserId`.
- One `xUnit2000` in
  `InvoiceDraftRevisionArchiveCodec_Tests.cs:176`
  (`ARCH09`), where `Assert.Equal(actual, 1)`
  reverses expected/actual order. This has been corrected
  **on the remote WIP branch** to `Assert.Equal(1, actual)`
  in commit `5567ec7`. The user's local uncommitted
  migration must be preserved before any subsequent pull.

The user next ran `dotnet ef migrations script` under
`BM_INVOICE_MIGRATION_OFFLINE=1`, starting from
`20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots`
through `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger`.
The CLI reported `Build succeeded` and generated the file
`etc/phase-audits/Phase2G3A7_RevisionLedger_Patched_Review.sql`
**on the user's local machine only**. The environment override
was then cleared.

**Pending proof:** The patched SQL file has **not been uploaded or
read** in this audit. A successful offline export alone is not
a PostgreSQL DDL execution test or proof that the row and statement
triggers, hexadecimal CHECK and non-destructive rollback are
correct. Do not run `database update`, `DbMigrator`, `psql`
to apply the preview, or commit/rebase generated files blindly.
The next gate is inspection of the exact patched SQL and separately
provisioned scratch database tests under explicit safety controls.
The real mutating draft rebuild and financial issuing remain BLOCKED.

## Manual review of uploaded generated migration bundle — 25/10/2026

User uploaded `Phase2G3A7_ReviewBundle.zip` containing the **four**
local generated files, and the assistant read their actual contents:

- `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs`
- `20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs`
- `BuildingManagementDbContextModelSnapshot.cs`
- `Phase2G3A7_RevisionLedger_Generated_Review.sql`

### Verified structure

- Generated `Up()` contains **one** `CreateTable` for
  `BuildingManagementInvoiceDraftRevisions`, **one** unique
  index `UX_BM_InvoiceDraftRevision_Invoice_Number`
  on (`InvoiceId`, `ArchivedRevision`), restrictive invoice
  FK and four basic `CHECK` constraints.
- Offline SQL mirrors these additive objects; it does not alter
  existing invoice, item, meter, rate or payment tables.
- Designer and Model Snapshot contain matching model payloads,
  with only EF-expected migration class/attribute differences.
  The auto-shortened 63-character FK identifier ending in `~`
  is a valid PostgreSQL identifier, not a typo.
- SQL emitted includes an EF migrations history insert.
  It is **not** idempotent and must **not** be executed on a
  populated application database during review.

### Required corrections before any rollout

1. `Up()` lacks PostgreSQL UPDATE/DELETE protection for archived
   evidence, and `TRUNCATE` requires a distinct statement trigger
   because row triggers do not fire for TRUNCATE.
2. `Down()` uses unconditional `DropTable`, which would
   irreversibly destroy historical revisions. Require an empty-ledger
   rollback guard; test on an isolated PostgreSQL scratch DB.
3. The lower-case/64-character checksum CHECK is **not** sufficient
   hexadecimal validation. Add PostgreSQL hex CHECK
   `^[0-9a-f]{64}# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Next non-destructive persistence design slice — WIP (25/10/2026)

Added `InvoiceDraftRevisionArchiveCodec` as **offline/in-memory**
format foundation for historical revision evidence:

- Explicit `FormatVersion=1` archive JSON includes
  `InvoiceId`, original revision number, user GUID, UTC timestamp,
  correction reason and complete previous utility-only snapshot.
- Stable utility/source ordering and SHA-256 checksum over the exact
  UTF-8 JSON bytes. Decoder verifies checksum and re-runs the
  existing `InvoiceDraftRevisionEvidence.Capture` and domain
  snapshot constraints, then rejects noncanonical JSON.
- The checksum protects against accidental corruption when stored
  and checked separately. **It is not cryptographic authentication
  against a privileged actor who can rewrite both payload and hash.**
- No EF entity/DbSet/migration/application endpoint, HTTP route,
  current invoice mutation, PostgreSQL connection or issuance.

Added **9 new unit tests** in
`InvoiceDraftRevisionArchiveCodec_Tests` for roundtrip fidelity,
deterministic ordering, corrupt/missing checksums,
actor/reason scope, unsupported format, malformed JSON,
noncanonical rehashed JSON and no mutation of old evidence.
These new tests have **NOT yet run locally**.

Created a separate review document:
`etc/phase-audits/Phase2G3A7_RevisionLedger_MigrationRFC.md`.
It specifies draft-only additive DDL, append-only ledger separation
from active source uniqueness, CurrentRevision+ConcurrencyStamp,
contract→invoice row-lock ordering, loser-aborts concurrent rebuild
logic, transaction rollback guarantees and migration review gates.
**SQL is illustrative and marked DO NOT RUN.**

Next local unit command:

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests|FullyQualifiedName~InvoiceDraftRevisionArchiveCodec_Tests"
```

Expected domain revision cases: **18/18** (previous REV 9 +
new ARCH 9), **not yet verified as a combined run**. If the
archive codec's compile/runtime cases fail, correct source first
and do not proceed to live EF migrations.

 as database-only defense in depth, acknowledging
   that a rewrite of JSON *and* checksum by privileged personnel
   still bypasses SHA-256's tamper detection.
4. The approved future rebuild workflow still needs current
   revision/stamp, transaction lock order, building-scoped
   authorization, rollback-on-conflict and PostgreSQL concurrency
   tests. This scaffold does **none** of those.
5. The fifth compiler warning is still unidentified from the
   supplied summary; capture full diagnostic lines.

A separate **review-only C# patch** to the generated migration
was produced in the current conversation, adding a regex check,
row UPDATE/DELETE protection, statement-level TRUNCATE protection
and a rollback empty-table guard. This patch is **not committed to
GitHub**, because the generated files currently exist only in the
user's Windows working tree. It is **uncompiled and untested on
PostgreSQL**; it must be reviewed/recompiled before any scratch
database test.

**Status: migration structural review PASSED with blockers;
production application and financial issuance remain BLOCKED.**

## Offline migration scaffold generated locally — 25/10/2026 (unreviewed)

User confirmed working tree initially clean, checked out branch
`wip/phase-2g2`, fast-forwarded
`7c27aa5..c2c6927`, and successfully executed
`prepare-invoice-revision-ledger-migration-offline.ps1`.

The log explicitly reported:

```
SAFE MODE: only scaffold local EF migration files and generate OFFLINE SQL.
Build succeeded.
OFFLINE MIGRATION SCAFFOLD + BASIC SQL REVIEW COMPLETED.
Migration ID: 20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger
NOT APPROVED TO APPLY
```

The generated files exist on the **user's local Windows checkout**,
**not in the connected GitHub branch**. Final
`git status --short` reported exactly these four changes:

```
 M modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/BuildingManagementDbContextModelSnapshot.cs
?? etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql
?? modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.Designer.cs
?? modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger.cs
```

**Basic offline scaffold checks PASSED, but full SQL/source review
has NOT happened**: the generated migration, Designer, model snapshot,
and SQL preview must be read before approving any code/migration
change. Do not infer that the PostgreSQL trigger or populated-table
rollback protection exists; the generator does not implement these.

Next review gate: collect those four exact files, check generated
Up/Down and full EF model snapshot, validate PK/FK/index/check
constraints and source table independence, and identify the exact
**fifth** build warning (older baseline was four RMG012 warnings).
Do not run any EF database update, DbMigrator, psql apply or schema
mutation; preserve the generated local files as-is until reviewed.

## Offline scaffold helper implementation (subsequently run on 25/10/2026)

- Added `etc/scripts/prepare-invoice-revision-ledger-migration-offline.ps1`.
  It scaffolds a local EF revision-ledger migration and a local
  PostgreSQL SQL text preview under strict clean-WIP-branch and
  design-time offline flag checks. The PostgreSQL design-time factory
  uses an intentionally unreachable `127.0.0.1:1` connection string.
- It validates that the new migration `Up()` only creates the
  `BuildingManagementInvoiceDraftRevisions` table, and that the
  SQL preview includes exactly that new table and unique
  (InvoiceId, ArchivedRevision) index.
- **NOT executed** on the user's Windows machine. Generated migration
  files and SQL cannot yet be claimed or cited as reviewed output.
  The assistant's runtime has no .NET SDK available for scaffolding.
- The script only **generates local files**; it does not apply any
  SQL or alter any database. The generated scaffold will lack the
  required PostgreSQL UPDATE/DELETE protection and populated-ledger
  rollback guard; it is **NOT release approved**.
- `Phase2G3A7_RevisionLedger_MigrationRFC.md` now explicitly
  documents the offline command, expected files, manual approval
  criteria and five-warning investigation.

Do not use `dotnet ef database update`, `DbMigrator` or `psql`
to apply this schema. The next gate is generated source/SQL review
and inspection of the extra compiler warning.

## Staged revision ledger SQLite integration gate — 25/10/2026

The user ran the full filtered
`InvoicePersistenceIntegration_Tests` class after adding the
staged EF revision ledger model and reported:

```
Test summary: total: 13, failed: 0, succeeded: 13, skipped: 0, duration: 10.6s
Build succeeded with 5 warning(s) in 25.7s
```

**INVDB01–13: 13/13 PASS** verifies the existing invoice persistence
tests plus three new SQLite cases covering revision-record insert
and rehydration, prevention of duplicate archived revision numbers
and two distinct archived versions for the same invoice.

**Warning investigation remains open:** Earlier builds showed four
known RMG012 tenant mapping warnings. This build reports **five**
warnings, but the user supplied only a summary, not diagnostic
codes/locations. Do not classify the fifth as harmless or new until
its exact compiler warning has been reviewed.

**Scope limitation:** SQLite's schema creation from the current EF
model is *not* proof that a real PostgreSQL revision table exists.
The staged model has no generated EF migration in the repository
as of this verification; there is no append-only PostgreSQL
UPDATE/DELETE trigger or authorized revision writer. The model
must not be used to archive actual invoices on a populated database.

## Phase 2G.3A.7 — staged EF persistence model (WIP / unverified)

After the **18/18 PASS** archive roundtrip results, the repository
now includes a **staged** EF model for storing historical draft
evidence *separately* from active charge source rows:

- `InvoiceDraftRevisionRecord` (Domain) holds invoice ID, previous
  revision number, operator GUID, UTC archive instant, correction
  reason, exact canonical JSON and its SHA-256 digest.
  `Prepare` copies data from the already validated immutable
  `InvoiceDraftRevisionEvidence`; `VerifyAndRead` validates
  JSON/digest and cross-checks the decoded metadata against the
  record's metadata columns. This is NOT a new active invoice.
- `InvoiceDraftRevisionRecordConfiguration` (EF Core) maps a
  new separate table `BuildingManagementInvoiceDraftRevisions`
  with an invoice FK (RESTRICT), unique
  `(InvoiceId, ArchivedRevision)` and provider-neutral
  check constraints for revision number, reason, JSON length,
  and lowercase checksum length.
- The entity is registered via a `DbSet` and explicit mapping
  on `BuildingManagementDbContext`; application and HTTP services
  remain unchanged. No writer endpoint calls the new repository.
- `InvoicePersistenceIntegration_Tests` now has
  **INVDB11–13**, checking actual isolated SQLite write/reload,
  duplicate archived revision rejection, and ordered distinct
  revision numbers. Expect **13/13** invoice DB tests.

**IMPORTANT model/migration staging boundary:**

- **No migration file or EF model snapshot has been generated** for
  this new model. Until a reviewed EF migration exists and is applied
  in a dedicated safe environment, a PostgreSQL database will NOT
  have the revision table. Do **NOT** access it using the running
  application or execute DbMigrator against a populated database.
- We intentionally deferred the proposed
  `BuildingManagementInvoices.CurrentRevision` column until the
  migration/update policy and optimistic conflict protocol are
  finalized. The old invoice table and its indexes were not changed.
- A domain entity with private setters and a checksum **does not
  make the database append-only**. The future PostgreSQL migration
  must add a tested reject-UPDATE/DELETE trigger or an equivalent
  independently enforced SQL privilege boundary; SQLite tests
  do not prove database immutability.
- A SHA-256 digest is a corruption check, NOT a cryptographic
  signature against a privileged database actor.
- No actual automated rebuild/cancel/issue/receivable transition
  exists or was enabled.

### Next local test gate — NOT YET EXECUTED

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoicePersistenceIntegration_Tests"
```

Expected `INVDB01–13`: **13/13 PASS — verified** (25/10/2026).
Only after this gate and a reviewed schema design should the
actual EF migration be generated, entirely offline (no DB connection
and no update). Preserve earlier 18/18 domain result separately.

## REV01–09 local verification — 25/10/2026

User ran `InvoiceDraftRevisionEvidence_Tests` after pulling
the WIP branch and reported:

```
Test summary: total: 9, failed: 0, succeeded: 9, skipped: 0, duration: 3.5s
Build succeeded in 10.2s
```

**9/9 PASS** confirms the in-memory old-draft evidence model's
metadata, independent copies, read-only collections and validation
behavior. The build reported success, with no warnings in the
supplied summary. This does **not** verify database persistence,
revision uniqueness, concurrent rebuild, or operator authorization.

## Immutable old-draft evidence prototype — test verified (25/10/2026)

Added a **non-persisted** Domain model
`InvoiceDraftRevisionEvidence.Capture`, preparing the shape of
historical data to retain *before* a future approved draft rebuild.
This is a pure in-memory value model, **not** an EF entity and
**not** a new invoice revision feature. It cannot write the
database, update current draft rows, release source uniqueness or
recognize a receivable.

Required archive metadata:

- Stable parent `InvoiceId`, positive `ArchivedRevision`,
  identified `ArchivedByUserId` and UTC `ArchivedAtUtc`.
- Correction reason trimmed to 8–500 characters.
- Prior `InvoiceDraftSnapshot` revalidated and copied by value.
  Complete electricity and water lines, source identifiers, physical
  meters, reading dates/counters, tariff rates and amounts preserved.
- Copies are wrapped in read-only collections and do not retain
  caller-owned line/source list containers.
- A prior unpriced/incomplete utility-only draft is rejected as
  archive evidence pending an explicit incomplete-draft policy.
- **No** posting, issuing, payments, cancel/supersede or invoice
  status transition.

New unit class
`BuildingManagement.Domain.Tests/Invoices/InvoiceDraftRevisionEvidence_Tests.cs`
includes **9** xUnit cases: metadata, deep copy, no mutable
collections, 2 invalid reasons, overly long reason, missing actor
or invoice ID, non-UTC timestamp/revision, and incomplete price.

New local gate (not yet executed):

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Domain.Tests\BuildingManagement.Domain.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRevisionEvidence_Tests"
```

**9/9 PASS verified** (reported user log on 25/10/2026).

### Outstanding design decisions before any persistence

1. Source-key ownership: historical revision evidence must be audit
   proof only, while active invoice source uniqueness remains
   enforced; do not blindly duplicate source keys into the active
   source table.
2. Invoice concurrency: specify the exact row-lock, optimistic
   revision/stamp check, and multi-writer conflict response.
3. Revision history table, FK constraints, unique
   (InvoiceId, ArchivedRevision), immutable evidence storage,
   UTC actor/reason metadata and rollback-ready migration.
4. Scope authorization and explicit operator policy for stale,
   unchanged, incomplete or superseded drafts.
5. Full PostgreSQL stored-draft/rebuild concurrency integration on
   an opt-in disposable DB before production migrations.
6. Do not assume the nine pure domain tests prove any of 1–5.

## Local verification — 25/10/2026 (current WIP revision `61823a2`)

The user ran `git pull --ff-only origin wip/phase-2g2`
(fast-forward `48e0077..61823a2`) followed by the
three exact tests prescribed in the previous audit:

```
Application.Tests [four invoice test filters]:
Test summary: total: 30, failed: 0, succeeded: 30, skipped: 0, duration: 1.7s
Build succeeded with 4 warning(s) in 15.7s

EntityFrameworkCore.Tests [InvoicePersistenceIntegration_Tests]:
Test summary: total: 10, failed: 0, succeeded: 10, skipped: 0, duration: 7.7s
Build succeeded in 16.4s

PostgreSql.Tests [-Scenario Isolation, PGINV02/03/07/08]:
Test summary: total: 6, failed: 0, succeeded: 6, skipped: 0, duration: 16.2s
Build succeeded in 25.1s
```

**Verified: 46/46 PASS across 3 separate invocations** (30 + 10 + 6)
with no failed or skipped test. The four Mapperly RMG012 warnings
in the Application build are pre-existing.

This validates only the **read-only** versioned-draft preflight,
stored-draft preview through SQLite, and PostgreSQL isolation
rejection before unsafe SQL. No version history table, no mutating
rebuild workflow, no issuance, and no populated application database
migration were tested or enabled. The earlier 323/323 unfiltered
regression log is from an unspecified test assembly and predates
this latest slice.

## Phase 2G.3A.7 read-only rebuild preflight — implementation slice (WIP)

The earlier versioned-draft recommendation has been implemented **ONLY
as non-mutating preflight and internal preview**, not as an approved
rebuild, cancel, issuance or receivable transaction:

- Added `InvoiceDraftRebuildPreflight.Assess(saved, current, reason)`
  in the Application layer, with a result DTO and enum in
  Application.Contracts. This code rebuilds **in-memory evidence**
  using the existing `InvoiceDraftSnapshot.Capture` domain
  invariants and checks the existing saved draft before considering
  a new candidate. No tables are read or written by the pure helper.
- A stored invoice must have status Draft, exactly one electricity
  and one water line, consistent totals and original meter evidence.
  A malformed/ambiguous old draft is rejected rather than silently
  overwritten.
- A fresh candidate must belong to the **same contract, room and
  billing month**, with all rates, source identity, counter ranges,
  dates, amount arithmetic and coverage complete. Missing history,
  price or readings blocks preflight; changing a source ID even with
  the same amount is recognized as needing a revision.
- `AlreadyCurrent` is returned for an unchanged draft. A stale
  candidate requires an explicit operator reason of 8–500
  characters, trimmed before reporting.
- `ReadyForRevisionDesign` carries only proposed total and
  normalized reason. **It is not a durable authorization, a new
  invoice, or permission to mutate any existing row.**
- Exposed `PreviewRebuildAsync(id, reason)` solely through the
  existing **internal-only** `IInvoiceDraftStorageAppService`.
  The ABP root UOW is `RepeatableRead`; the same actual PostgreSQL
  isolation guard protects an ambient transaction from bypassing it.
  Saved evidence is retrieved and the current candidate rebuilt
  within this single read snapshot. Expected business-history
  blockers become `CurrentCandidateNotReady`; infrastructure
  errors still propagate.
- **No remote API, invoice status transition, billing, payment,
  schema migration, direct SQL or production database actions**.

### Test cases (confirmed 46/46 PASS above)

- `InvoiceDraftRebuildPreflight_Tests`: **14** xUnit cases,
  including ready/unchanged, reason validation, wrong scope,
  incomplete source history, tampered saved draft, invalid status,
  same-amount/different provenance, duplicates and bad counters.
- `InvoicePersistenceIntegration_Tests`: **INVDB09/10**,
  exercising actual SQLite internal preview after tariff correction
  and fail-closed response to a soft-deleted reading, without
  changing the previously persisted invoice.
- `InvoiceDraftIsolationGate_Tests`: one additional attribute
  case for the new `PreviewRebuildAsync`.
- `InvoiceDraftIsolationPostgreSql_Tests`: **PGINV08**
  rejects a preexisting ambient READ COMMITTED UOW before
  touching any invoice row.

Narrow expected counts (in **separate test assemblies**):

```powershell
git pull --ff-only origin wip/phase-2g2

dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftRebuildPreflight_Tests|FullyQualifiedName~InvoiceDraftFreshnessEvaluator_Tests|FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests"

dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoicePersistenceIntegration_Tests"

powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Isolation
```

Expected counts: Application **30/30** (14+10+4+2);
EF Core **10/10**; PostgreSQL isolation **6/6**.
These three groups were **reported PASS** on 25/10/2026. Verify on the user's
Windows .NET 10 environment; no .NET SDK is available in the
assistant's current runtime for local compilation.

### Safe migration architecture (review before any DDL)

Keep the logical invoice ID and the two nonfiltered room/month and
contract/month uniqueness constraints. Keep the global **active**
source attribution uniqueness. For a future approved
`RebuildDraft` operation, add a new append-only revision ledger
containing the complete *previous* snapshot: immutable rate,
source ID/type, physical meter ID, dates, old/new counters, line
and total money, reason, revision number, UTC timestamp, actor ID
and linkage to the same invoice ID. The archived evidence must be
decoupled from the current-source uniqueness index but cannot claim
another charge.

In a single authorization-checked `RepeatableRead` or stricter
transaction: follow the existing contract→invoice row lock order;
validate expected revision/concurrency stamp; reconstruct the latest
candidate; re-run the preflight; append old evidence; atomically
replace only active draft line/source rows with new evidence; update
revision number; and commit or roll back everything. Design the
EF Core delete/insert order and indexes carefully to avoid source
uniqueness conflicts and false source reuse. Two simultaneous
rebuilds must not both commit.

**No schema was created and no mutating rebuild was added.**
Review migration/backfill and test on a disposable scratch DB
before touching any populated app DB. Never implement a blind
hard-delete or soft-delete bypass of invoice uniqueness.

## Latest unfiltered regression report — 2026-10-25

The user provided one unfiltered .NET 10 test-run summary after being
given both the Application and EntityFrameworkCore project test
commands:

```
Test summary: total: 323, failed: 0, succeeded: 323, skipped: 0, duration: 31.4s
Build succeeded in 40.3s
```

**323/323 PASS — one reported test invocation; no build warnings in
the supplied summary.** The log excerpt does **not identify the
executed .csproj or test assembly**. Therefore this is a positive
unfiltered regression result, but it must **not** be represented as
323/323 passing in BOTH projects or as all projects running. The
other project, if not already executed, still requires separate
confirmation. The test command itself, or each project's separate
test summary, is necessary to attribute the result precisely.

Previous same-revision narrower gate results remain:
Application invoice **15/15 PASS**, SQLite EF invoice **8/8 PASS**,
PostgreSQL isolation **5/5 PASS**; previous PostgreSQL concurrency
PGINV04–06 **3/3 PASS**.

No source or schema changes accompany this documentation update.

## Verified test baseline

- `InvoiceDraftFreshnessEvaluator_Tests`: **10/10 PASS**.
- `InvoiceDraftIsolationGate_Tests`: **3/3 PASS**.
- `InvoiceRemoteAccessGate_Tests`: **2/2 PASS**.
- `InvoicePersistenceIntegration_Tests`: **8/8 PASS** including
  actual saved-draft stale detection after price/reading revision.
- PostgreSQL PGINV02, 03 and 07: **5/5 PASS** isolation safety.
- PostgreSQL PGINV04–06: **3/3 PASS** concurrent direct SQL, application
  mutators and physical replacement candidate behavior.
- An unfiltered **323/323** regression report was received for one
  unspecified test assembly after the latest integration changes.
  It does not yet prove that both full test projects ran. Four existing
  tenant Mapperly warnings were reported in earlier builds.

## Source-code findings

| Current behavior | Evidence in code | Operational consequence |
| --- | --- | --- |
| Status model has only `Draft = 0` | `Domain.Shared/Invoices/InvoiceStatus.cs` | No Cancelled, Superseded, Issued or adjustment status exists |
| Aggregate has only `CreateDraft`; no Issue/Post/Delete/Replace | `Domain/Invoices/Invoice.cs` | No audited draft lifecycle or controlled revision |
| Internal draft service only has Create/Get/HasCurrentSourceSnapshot | `Application.Contracts/Invoices/IInvoiceDraftStorageAppService.cs` | No supported regenerate, delete, cancel, void, supersede workflow |
| Contract + billing period unique across ALL invoices | `UX_BM_Invoice_Contract_Period` | Can't simply insert a second draft for the same contract/month |
| Room + billing period unique across ALL invoices | `UX_BM_Invoice_Room_Period` | Can't create a new room-month draft under a different contract |
| Source Type + Source Id globally unique | `UX_BM_InvoiceSource_Global` | Can't reuse an old reading/replacement evidence source in another draft |
| Invoice FKs and item/source FKs use RESTRICT | invoice EF configurations | Naive hard-delete is unsafe and risks historical evidence |
| Historical monetary/source data is copied by value | InvoiceItem/InvoiceItemMeterSource | Original draft remains auditable, but can be stale after later mutations |
| Freshness check compares saved vs current candidate | `InvoiceDraftStorageAppService.HasCurrentSourceSnapshotAsync` | Reports only snapshot freshness at *time of read*, not later authorization |
| Internal-only `[RemoteService(IsEnabled = false)]` | InvoiceDraftStorageAppService + InvoiceDraftCandidateAppService | No conventional ABP HTTP invoice access; operator scope still not implemented |

## Confirmed business dead-end

1. An October utility draft is saved with readings and tariff A.
2. A permitted meter-reading correction or tariff amendment commits B.
3. `HasCurrentSourceSnapshotAsync(invoiceId)` correctly returns **false**.
4. There is currently no business operation to repair the draft or
   supersede it, and DB-level unique constraints reject simply creating
   another invoice for the same contract/room and billing month.
5. An operator must NOT use direct SQL delete, manually change amounts,
   or bypass the uniqueness constraints to "fix" the draft.
6. In particular, soft-delete alone cannot make the unique index
   release a contract-month or source key. The invoice aggregate is
   not modeled as `ISoftDelete` and the indexes are unfiltered.

This is a **real missing business capability**, not a failed
concurrency test. Existing immutable snapshots and constraints are
safe in isolation; the recovery workflow is not yet implemented.

## Recommended controlled policy (proposal, NOT implementation approval)

**Draft may be recalculated while still unissued, but past evidence
must remain independently auditable.** Prefer a single logical
invoice/room-month identity with versioned draft snapshots, not
untracked hard-delete-and-recreate.

Proposed semantics:

1. Keep one logical invoice per room/month, preserving
   `UX_BM_Invoice_Contract_Period` and
   `UX_BM_Invoice_Room_Period`.
2. Introduce a **draft revision history** with immutable versions
   (who, when, reason, source/rate evidence, old/new totals).
   Do not simply overwrite old source rows and erase evidence.
3. Make an operator-requested **RebuildDraft** action conditional on:
   Draft status, correct building/room authorization, explicit
   reason, current unissued state, no open financial posting, and a
   fully reconstructed new candidate.
4. Verify original draft is stale or that an explicit audit-approved
   recomputation reason exists. Run replacement atomically with
   PostgreSQL `REPEATABLE READ` or stronger and consistent
   lock ordering; do not create partial line/source graphs.
5. Design the interaction with the global source uniqueness index:
   only the active draft revision can own a source key, or use
   a separate immutable revision evidence table with a clear
   accounting-owner key. Do NOT drop global uniqueness on
   released/issued source attribution without accounting review.
6. Add an explicit cancellation/supersession policy: cancellation
   must preserve audit history; it must not implicitly create new
   charges or recognize debt; invoice numbering and released
   uniqueness need an approved invariant.
7. Confirm whether "stale" means simply warning, automatic invalidation,
   or automatic block on actions. **Conservative interim behavior:
   block financial issuance and require explicit operator review.**
8. Future Issue/Post is a **separate** transition and must check
   source freshness, permissions, invoice status and receivable
   constraints **within the same transaction as posting**; a
   historical `true` freshness response is never enough.

These are recommended design constraints, not a policy already
approved or implemented.

## Decisions and test gates before coding mutating draft API

- Can a draft be rebuilt more than once, and can it be rebuilt without
  source changes? Require reason and audit stamps.
- When a tenant moves out or a contract is changed, does a new
  contract own the full room-month? Current logic disallows split
  tenancy; maintain fail-closed until policy is approved.
- If a source has been included in a **future issued invoice**, must
  correction be via explicit credit/debit adjustment rather than
  draft rebuild? Yes as a conservative design principle; accounting
  implementation pending.
- Are completed/soft-deleted meter readings editable? Existing meter
  mutation policies must be preserved and separately audited.
- Reject accidental cross-building GUID access for read/rebuild;
  dedicated invoice permissions and host route discovery are required
  before turning remote service back on.
- Add PostgreSQL tests for:
  - Stale draft rebuild succeeds without losing old audit evidence.
  - Two simultaneous rebuild requests cannot both win.
  - Rebuild versus source-reading change or meter replacement is
    serialized or rejected with no mixed-version amounts.
  - Duplicate room/month and global source reuse still fail.
  - Failed rebuild transaction rolls back all new revisions.
  - Cancel/supersede and any future issue/post cannot violate
    payment or financial-history invariants.
  - Cross-building and unauthorized operators are denied.

## Current safe operating posture

- Leave `InvoiceStatus` draft-only.
- Keep both invoice services internal-only.
- Preserve all persisted evidence and unique indexes.
- Allow the existing read-only freshness query but do not construe
  `true` as permission to issue.
- Do not change schema or touch populated application DB until the
  lifecycle design and rollback migration are agreed.
- Confirm which assembly produced the new unfiltered 323/323 result;
  run the other full test assembly if it has not yet been executed.

**Phase 2G.3A.6:** targeted application/EF/PostgreSQL gates green.
**Phase 2G.3A.7:** correction/lifecycle governance OPEN.
**Production issuance / receivables:** BLOCKED.
