# Phase 2G.3A.6 — Invoice draft source consistency gate (pre-implementation audit)

## Verified baseline (25/10/2026)

- Phase 2G.3A.4: 109/109 selected app/EF tests, PGINV01 1/1
  PostgreSQL DDL test, PGMIG01 1/1 full BuildingManagement migration
  chain test all reported PASS. Fresh scratch DB was deleted.
- Phase 2G.3A.5 gate A: InvoiceRemoteAccessGate_Tests 2/2 PASS.
  Candidate and draft storage application-service classes are both
  explicitly `[RemoteService(IsEnabled = false)]`.
- Builds show four existing Tenant Mapperly RMG012 warnings.
- **Not a production-release gate approval.**

## Verified regression and first implementation slice — 25/10/2026

The user reran the existing 109-case selected EF/application regression
after the invoice HTTP gate changed and reported **109/109 PASS**
(duration 27.6s, build succeeded with four previously known Mapperly
warnings). The invoice HTTP attribute tests separately passed **2/2**.
Those results predate the new transaction-isolation changes described
below; a new run is required.

**Partial source-snapshot consistency hardening (not yet locally tested):**
The root `InvoiceDraftStorageAppService.CreateAsync` transaction and
the independently callable, internal
`InvoiceDraftCandidateAppService.GetMonthlyCandidateAsync` transaction
now both declare:

```csharp
[UnitOfWork(true, IsolationLevel.RepeatableRead)]
```

On PostgreSQL, this requests one MVCC read snapshot for all SELECTs
within the same unit of work, preventing a draft's electricity and
water usage/rate statements from observing *different committed
versions within that single transaction*. The existing contract row
lock remains. SQLite's provider treats this isolation level as a
minimum and promotes it to serializable.

The new `InvoiceDraftIsolationGate_Tests` has two attribute-regression
cases; **no new tests have been run locally**. Both services remain
`[RemoteService(IsEnabled = false)]`.

**Limitation / no overclaim:** ABP's `UnitOfWork` attribute does not
override an already ambient UOW. A caller that starts a PostgreSQL
READ COMMITTED UOW can still bypass the new desired isolation.
We therefore cannot claim this phase's source consistency gate is
complete. A runtime isolation-level check and controlled PostgreSQL
writer-vs-capture concurrency tests are required before the internal
draft service is made available for financial use. A later source edit
can also invalidate a stored draft; issuance still needs atomic
revalidation/correction policy.

### Next local verification

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests|FullyQualifiedName~InvoicePersistenceIntegration_Tests|FullyQualifiedName~InvoiceSchema_Tests"
```

Report exact pass/fail counts. No database migration or PostgreSQL
connection is needed for this SQLite/application regression.

## Compile blocker and corrective fix (25/10/2026)

Initial isolated gate implementation used the incorrect attribute syntax
`[UnitOfWork(IsTransactional = true, IsolationLevel = IsolationLevel.RepeatableRead)]`.
User's .NET 10 local build failed with **four CS0655 errors** in the
two invoice application services. ABP `UnitOfWorkAttribute` exposes
nullable `bool?`/`IsolationLevel?` properties, which cannot be used
as C# attribute named arguments. This was a source code error, **not
a PostgreSQL failure**.

Both methods now use the ABP-supported **positional constructor**
`[UnitOfWork(true, IsolationLevel.RepeatableRead)]`
(available in the ABP 10.x API). The two test cases inspect the
attribute's `IsTransactional` and `IsolationLevel` values and do not
need modification. This correction has not yet been compiled or tested
on the user's machine.

## Verified 9-case regression and ambient-UOW hardening (25/10/2026)

After correcting the ABP constructor syntax, the user reported:

```
Test summary: total: 9, failed: 0, succeeded: 9, skipped: 0, duration: 7.3s
Build succeeded with 4 warning(s) in 21.9s
```

This verifies **9/9** selected SQLite/EF/app tests for invoice
isolation attribute configuration, internal HTTP gate, and invoice
persistence/schema. It is not evidence of concurrent PostgreSQL execution.

### New fail-closed runtime control (WIP, awaiting local test)

ABP ignores a method's `[UnitOfWork]` isolation setting when
an outer UOW already exists. To address that bypass, the Application
layer now invokes `IInvoiceDraftReadIsolationGuard` *before*
any contract/meter/tariff reads in both draft creation and candidate
preview. The implementation lives in the EntityFrameworkCore module:

`EfCoreInvoiceDraftReadIsolationGuard` checks the **actual**
PostgreSQL EF transaction's `DbTransaction.IsolationLevel`
and rejects absent/nontransactional/READ COMMITTED ambient UOWs
unless effective isolation is `RepeatableRead` or `Serializable`.
For SQLite-only regression tests, the guard is no-op because the
existing test module explicitly disables transactional EF UOWs.
The prior root UOW attributes remain.

**Safety:** this makes an unsafe caller fail closed instead of
silently mixing versions; it does not freeze subsequent corrections,
authorize issuance, or alter meter/tariff write paths. No migrations
or production DB operations were added.

### New PostgreSQL test gate (WIP, NOT YET RUN)

`InvoiceDraftIsolationPostgreSql_Tests` contains **four cases**
(PGINV02 and PGINV03, each covering both candidate preview and
draft storage), using the existing guarded local database and
random, absent contract IDs:

- PGINV02: an explicit ambient `ReadCommitted` transaction must
  be rejected by the runtime guard before contract business logic.
- PGINV03: an ambient `RepeatableRead` transaction must pass the
  isolation gate and then reject the nonexistent contract normally.

The cases only read the dedicated local test database; they do not
insert invoice or contract rows. Existing test module setup may
ensure test-only schema tables. The `.env` helper now supports an
`Isolation` scenario while retaining its original default
`Migration` scenario and existing credential-handling behavior.

From a CLEAN `wip/phase-2g2` worktree:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Isolation
```

**Verified: 4/4 PASS** (user report, 25/10/2026). All test/database
access is guarded by `RequireSafeConnectionString()` to
`127.0.0.1:5432/ngoctrang_integration_test`.

### Remaining (still OPEN)

This PostgreSQL gate checks that an unsafe ambient isolation setting
is rejected. It does **not yet test a writer changing a meter reading,
replacement or tariff while a draft transaction is active**, so the
larger source-version concurrency gate remains open. Final invoice
issuance and financial recognition are still disabled.

## PostgreSQL ambient transaction gate VERIFIED — 25/10/2026

User ran the guarded local `-Scenario Isolation` suite and reported:

```
Test Run Successful.
Total tests: 4
     Passed: 4
 Total time: 10.3024 Seconds
Test summary: total: 4, failed: 0, succeeded: 4, skipped: 0, duration: 10.8s
Build succeeded with 4 warning(s) in 28.9s
```

**PGINV02/03: 4/4 PASS** against the existing safe local PostgreSQL
test database. Confirms that both draft services reject an ambient
READ COMMITTED UOW and accept an ambient REPEATABLE READ UOW (which
then rejects an intentionally nonexistent contract). The preceding
SQLite app/invoice gates also passed 9/9 after the ABP attribute
constructor correction.

This closes **ambient-isolation enforcement**, not the full
concurrent-mutator integrity gate. No evidence of a meter reading,
replacement or tariff update racing against a real draft capture
has yet been supplied; that remains OPEN. The previous 109/109
regression result predates the runtime guard implementation.

## New real-source overlapping-transaction test PGINV04 (WIP)

Added `InvoiceDraftConcurrentSnapshotPostgreSql_Tests` with a deterministic
real `InvoiceDraftCandidateAppService` read across a PostgreSQL
concurrent commit. **Not yet executed locally.**

Test design (localhost `ngoctrang_integration_test` ONLY):

1. Seed a *new GUID-isolated test building/floor/room/active contract*,
   electricity/water meters, month-end readings and valid whole-month
   utility tariffs. No invoice is persisted or issued.
2. Open an explicit ABP `RepeatableRead` transaction and fix its
   PostgreSQL snapshot by reading the test contract.
3. While that transaction remains open, commit updates to **both**
   meter readings and tariff prices from an independent Npgsql
   transaction, restricted by the exact generated test row IDs.
4. Call the **actual invoice candidate service** under the first UOW:
   assert it sees the **old, internally consistent** meter and rate
   values, provisional line totals and overall total, not a mixture.
5. In a new scope/UOW, call the same service again and assert it
   sees the **new, internally consistent** data.
6. Hard-delete only this test's generated IDs in explicit
   children-before-parents order in a `finally` block.

The writer uses guarded test-only SQL rather than application-level
utility mutator methods. Thus this is a **deterministic PostgreSQL
snapshot/ABP service integration check**, not yet a direct test of
all business mutator code paths, meter replacement, or draft
persistence under concurrent edits. These still need coverage.

Script `etc/scripts/test-full-invoice-from-local-env.ps1` preserves
original `Migration` and `Isolation` modes and now adds
`-Scenario Concurrency` to launch just PGINV04 through the
existing local `.env` credentials and branch/clean-worktree guard:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Concurrency
```

**Verified PGINV04: 1/1 PASS** (user log, 25/10/2026).
Review the full exception if compilation, assertion or cleanup fails
before adding further concurrency scenarios.

## PGINV04 first local run: cleanup table-name error (25/10/2026)

User reported `PGINV04 0/1` with PostgreSQL SQLSTATE **42P01**
(`relation "BuildingManagementBuildings" does not exist`) in
`InvoiceDraftConcurrentSnapshotPostgreSql_Tests.CleanupAsync`,
at the final test-fixture cleanup DELETE. The mapping in
`BuildingConfiguration.cs` explicitly uses
`builder.ToTable("Buildings")`, unlike most other prefixed module
tables. This is **a test cleanup defect**, not evidence of a live
invoice transaction inconsistency.

**Fix committed to WIP:** change only the Building cleanup table to
`"Buildings"`; restructure failure handling to preserve both the
original test exception and any cleanup exception
(`AggregateException`) rather than replacing the original failure.
The cleanup still filters DELETE statements by test-generated GUIDs
and runs inside a PostgreSQL transaction; it never deletes unrelated
business data.

**Important previous-run hygiene:** because the original cleanup
transaction failed on its last statement, all its earlier DELETE
operations would have been rolled back. If fixture seeding completed,
the failed run may have left one isolated `PGINV04-` test-only
building/room/contract/meter/rate fixture in
`ngoctrang_integration_test`. The next run uses new GUIDs and only
removes its own fixture; do **not** assume the old fixture was cleaned
or run a broad DELETE. Follow up with a separate exact-marker,
inspect-first cleanup if needed. No application/production DB changes.

**PGINV04 remains NOT VERIFIED** until rerun with the fix:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Concurrency
```

## PGINV04 final local verification — 25/10/2026

After the cleanup mapping fix (building table is `"Buildings"`),
user ran `-Scenario Concurrency` and reported:

```
Test Run Successful.
Total tests: 1
     Passed: 1
Total time: 5.0655 Seconds
Test summary: total: 1, failed: 0, succeeded: 1, skipped: 0, duration: 5.5s
Build succeeded with 4 warning(s) in 20.6s
```

**PGINV04: 1/1 PASS.** This verifies the real candidate service returns
one repeatable-read PostgreSQL snapshot across a separately committed
meter-reading + tariff SQL transaction; after the old UOW ends, a
fresh UOW reads the newly committed values. The passing test also
completed its own generated fixture cleanup without error.

The *first failed PGINV04 run* may still have left a different
test-only fixture behind. Success of the second run does **not**
delete or prove absence of the first-run fixture. Inspect separately
before any targeted cleanup; never broadly delete matching records.

**Scope boundary:** PGINV04 uses direct test-scoped SQL to update
source rows. It is NOT evidence that every application-service write,
meter replacement, or stored draft issuance transition is race-safe.
Phase 2G.3A.6 is not complete, and there have been no schema changes
or application/production database migration runs in this stage.

## Critical concurrency exposure — fail closed before financial issuance

Repository read on current WIP:

| Operation | Lock / consistency mechanism | Risk |
| --- | --- | --- |
| `InvoiceDraftStorageAppService.CreateAsync` | `IContractRepository.FindForUpdateAsync` locks contract row | Serializes draft against contract mutations using same row, but not utility rate/reading mutations |
| `MeterReadingAppService.CreateAsync/UpdateAsync` | `IUtilityMeterLock.AcquireAsync(meterId)` | Same physical meter mutations serialized, but not with invoice draft's contract row lock |
| `UtilityMeterAppService.ReplaceAsync` | Physical-meter lock | May change candidate's relevant source set during snapshot construction |
| `UtilityRateAppService.CreateAsync/UpdateAsync/DeleteAsync` | Overlap check and repository writes, no shared invoice lock | A tariff revision may race with the candidate's tariff read |
| `InvoiceDraftCandidateAppService.GetMonthlyCandidateAsync` | Transactional UOW, reads contract/utility previews | PostgreSQL READ COMMITTED can observe different committed state between SQL statements |
| `UtilityChargePreviewAppService.GetMonthlyChargePreviewAsync` | Sequential usage + floor/building + rate queries | Without shared lock or serializable validated snapshot, utility readings and tariff may not come from one atomic point in time |

An in-memory snapshot copies evidence and prices by value; this
makes it immutable **after creation** but does NOT prove it was created
from one concurrent-consistent historical state.

Existing database uniqueness prevents reusing a physical meter source
in multiple invoices. It does NOT independently guarantee the copied
amount matches a consistent pair of rate and reading versions when
other operations are concurrently committing.

## Next implementation gates (NOT complete)

1. Decide and codify draft-vs-issued source correction policy. A draft
   has no issue/cancel/delete transition yet; do not permanently block
   correcting a meter reading or tariff based solely on a draft without
   an approved correction path.
2. Map all write paths that can change a relevant source set, utility
   rate, room/building mapping, active contract, and meter replacement.
   Establish ONE documented lock order or serializable transaction/
   version-check scheme across draft capture and writers. Avoid new
   contract/meter inverted locks that can deadlock.
3. In PostgreSQL concurrency tests, synchronize competing requests to
   force a meter-reading update vs draft create, tariff update vs draft
   create, and meter replacement vs draft create. Assert that a draft
   is either built from a consistent version or is rejected; never
   allow a mixed-version amount/evidence graph.
4. Verify no existing permitted corrections become impossible;
   check repeat creation and globally unique meter-source constraints.
5. Keep issuance and receivables disabled; **revalidate all sources
   and current permissions at issuance time** even after draft
   concurrency fixes.
6. Add scoped operator permissions for cross-building read/create
   and host HTTP-route discovery tests before enabling remote
   invoice endpoints.
7. Run 109-case regression, new security tests and PostgreSQL
   concurrency tests after implementation; do not infer green from
   older 109/109 and 2/2 test results.

## No unsafe changes made in this audit

- No schema/migration changes or production DB writes.
- No locks or rate/meter mutation restrictions added without tests.
- No draft or issuance HTTP routes opened.

**Status: 2G.3A.5 internal-only remote access gate PASS; 2G.3A.6
source consistency investigation OPEN.**
