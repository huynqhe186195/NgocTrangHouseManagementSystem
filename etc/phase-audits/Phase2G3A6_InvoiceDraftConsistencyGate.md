# Phase 2G.3A.6 — Invoice draft source consistency gate (pre-implementation audit)

## Latest application test gate verified — 25/10/2026

The user ran `BuildingManagement.Application.Tests.csproj` with the
three intended invoice filters on the current WIP revision and reported:

```
Test summary: total: 15, failed: 0, succeeded: 15, skipped: 0, duration: 1.4s
Build succeeded with 4 warning(s) in 8.7s
```

**15/15 PASS** = `InvoiceDraftFreshnessEvaluator_Tests` (10),
`InvoiceDraftIsolationGate_Tests` (3) and
`InvoiceRemoteAccessGate_Tests` (2).

This complements the separately verified same-revision
`EntityFrameworkCore.Tests` invoice persistence **8/8 PASS** and
PostgreSQL `Isolation` scenario **5/5 PASS**. Thus all 23
intended Application + EF Core cases were actually executed, as
**two separate assemblies** (15 + 8), rather than a single 23-case
invocation. PostgreSQL concurrency PGINV04–06 last passed **3/3**.

Four Mapperly RMG012 warnings are unchanged. The old **109/109**
all-regression test result still predates the latest
`HasCurrentSourceSnapshotAsync` changes. A fresh wider regression
run is the next gate; do not label this phase or finance issuance
production-ready yet.

## Latest verification — 25/10/2026 (after internal freshness integration)

The user ran the freshly added changes at branch commit
`9d7b9f9`, confirmed by a successful fast-forward pull
from `f44ab6b`.

| Suite / exact test assembly | Result | Scope |
| --- | --- | --- |
| `BuildingManagement.EntityFrameworkCore.Tests` with invoice filters | **8/8 PASS**, build with 4 old Mapperly RMG012 warnings | `InvoicePersistenceIntegration_Tests` INVDB01–08, including the two newly wired freshness cases |
| `BuildingManagement.PostgreSql.Tests` `-Scenario Isolation` | **5/5 PASS**, build succeeded | PGINV02/03 (4) plus new PGINV07 (1), rejected unsafe ambient isolation before reading stored draft |
| `BuildingManagement.Application.Tests` with freshness/attribute/HTTP filters | **NOT RUN on this code revision** | INVFRESH01–10 (10), invoice `UnitOfWork` attributes (3), internal HTTP gate (2) |

**Corrected test count:** An earlier audit instruction said the
`EntityFrameworkCore.Tests.csproj` command should execute
**23** tests. That was incorrect: test projects are separate test
assemblies. Even though the EF test project *references/builds*
`Application.Tests`, the test runner executes only 8 matching
cases *within the selected EF test assembly*. It does not discover or
run the other 15 cases. This is not a failure of the 8 tests, but
the advertised 23/23 total is NOT verified.

The older standalone `InvoiceDraftFreshnessEvaluator_Tests`
ran **10/10 PASS** before the latest internal integration change,
but the latest 15-case application test slice should be rerun to
verify the current revision:

```powershell
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftFreshnessEvaluator_Tests|FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests"
```

Expected **15/15**; not yet locally verified on the latest revision.
The separately verified EF (8) + PostgreSQL isolation (5) +
previous PostgreSQL concurrency (3) results must not be conflated
with an all-project regression. The earlier full **109/109**
regression predates the newest freshness integration.

Nothing in these tests authorizes financial issuance. The
`HasCurrentSourceSnapshotAsync` method remains an **internal-only,
point-in-time check**, not an atomic issuance transition. Scoped
operator authorization, draft correction policy and populated-database
rehearsal remain open release blockers.

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

## PGINV05 — actual application-service writer overlap (WIP, not yet locally verified)

Added a second scenario in
`InvoiceDraftConcurrentSnapshotPostgreSql_Tests`:

- PGINV05 seeds a separate, unique, test-only fixture.
- Reader enters a real PostgreSQL `RepeatableRead` ABP UOW, anchors
  the MVCC snapshot with the seeded test contract.
- An independent execution context and DI scope (no leaked ambient
  `AsyncLocal` UOW) then calls the **real**
  `IMeterReadingAppService.UpdateAsync` for electricity and water
  and `IUtilityRateAppService.UpdateAsync` for both tariffs.
  Thus the meter write lock/validation and rate application logic
  are actually traversed rather than bypassed by direct test SQL.
- The anchored invoice candidate must read the *old* values and
  copied provisional amounts; a new candidate UOW must read the
  *updated* values after the writer commits.
- Cleanup uses the proven test-only ID-filtered children-first
  deletion, preserving original exceptions separately from cleanup
  failures. No invoice or production data is created/updated.

The `Concurrency` mode already selects the full
`InvoiceDraftConcurrentSnapshotPostgreSql_Tests` class. With PGINV04
and PGINV05, the expected result is now **2/2 PASS**, not just 1/1.

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Concurrency
```

**Not yet executed.** The two source updates/rate updates are
independent business service calls; their results may each commit
separately. This proves the reader's single captured snapshot, not
that several separate writes form one global atomic financial event.
Meter replacement-vs-draft creation, persisted draft freshness,
issuance revalidation and cross-building permissions remain OPEN.

## PGINV04 + PGINV05 verified — 25/10/2026

The user reran the guarded `-Scenario Concurrency` test suite after
adding the real application service mutator scenario and reported:

```
Test summary: total: 2, failed: 0, succeeded: 2, skipped: 0, duration: 6.3s
Build succeeded with 4 warning(s) in 19.2s
```

**2/2 PASS** proves that `InvoiceDraftCandidateAppService` reads a
consistent transaction snapshot in a concurrent PostgreSQL writer
scenario with both (PGINV04) direct test SQL updates and (PGINV05)
real `IMeterReadingAppService.UpdateAsync` and
`IUtilityRateAppService.UpdateAsync` calls in independent scopes.
Existing four Mapperly warnings persist.

The new fixture created by each successful test is removed by that
test's own cleanup. However, the **original failed** PGINV04 run
may have left a separate old GUID-isolated fixture; do not assume
all older artifacts were removed by later successful runs.

**Not proven:** concurrent meter replacement, persisted draft
freshness, draft correction/cancellation, invoice issuance, operator
authorization or populated production migration. Phase 2G.3A.6
still has open gates. The previous 109/109 regression predates
these latest code changes.

## PGINV06 — actual meter replacement overlapping candidate (WIP)

After PGINV04 + PGINV05 **2/2 PASS**, added a third independent
scenario (not yet run on the user's machine):

`PGINV06_Concurrent_Meter_Replacement_Keeps_Old_Snapshot_And_Blocks_Unverified_New_Meter`

- Seeds another GUID-isolated test building/room/active contract,
  full-month electricity and water rates, meters and October 31
  readings in `ngoctrang_integration_test` only.
- Anchors a PostgreSQL `RepeatableRead` ABP UOW before the writer.
- Independently calls the **actual**
  `IUtilityMeterAppService.ReplaceAsync` to replace the electricity
  meter on October 31, with final old counter **20→25** and new meter
  initially **0**, without a new meter's month-end reading.
- The old transaction MUST still return the original ready invoice
  candidate (10 electricity units at 3,500; 5 water units at 15,000),
  with no leaked replacement state.
- A new transaction MUST see replacement evidence, but **reject
  provisional pricing** due to
  `ActiveMeterMissingMonthlyReading` on the new active meter. Missing
  reading is not zero consumption.
- Cleanup locates only replacement records referencing the exact
  randomly generated old electricity meter GUID, deletes these first
  and then the generated new meter together with the other fixture
  rows, all in one transaction. It retains the previous
  combined-exception cleanup diagnostics and does not touch any
  unrelated business rows.

The guarded `Concurrency` scenario now runs PGINV04, 05 and 06.
**Expected: 3/3 PASS; NOT YET TESTED LOCALLY**. No migration or
production database action is involved.

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Concurrency
```

This tests the candidate/utility-history read and fail-closed
behavior. It does not yet prove *persisted draft creation*
against concurrent replacement, historical revisions after
draft persistence, issuance revalidation, or authorization.

## Verified PGINV04–06 (25/10/2026)

The user ran the `Concurrency` scenario with PGINV04, PGINV05
and PGINV06. Local result:

```
Test Run Successful.
Total tests: 3
     Passed: 3
Total time: 6.8021 Seconds
Test summary: total: 3, failed: 0, succeeded: 3, skipped: 0, duration: 7.2s
Build succeeded with 4 warning(s) in 18.4s
```

**PGINV04–06: 3/3 PASS.** These verify repeatable-read snapshot
behavior across direct SQL meter/rate updates, actual meter-reading
and tariff application-service updates, and actual physical-meter
replacement with fail-closed coverage for the new meter.

The four Mapperly warnings are pre-existing; no new warnings were
reported. This also confirms the new test cleans its own fixture on
successful execution. A separate leftover fixture from the initial
failed PGINV04 run is not automatically covered by that cleanup.

**Remaining gate:** a persisted draft can be stale when source
readings, prices, occupancy or replacement history changes after
it was written. Before issuance, compare the saved snapshot against
a freshly reconstructed candidate (including source identity,
counter ranges, date, rate and amount); reject any missing history
or disagreement. This is NOT equivalent to, nor authorized by,
the successful snapshot-concurrency tests. Issuance remains disabled.

## Draft freshness comparison foundation (WIP, not yet tested locally)

Added a pure, fail-closed `InvoiceDraftFreshnessEvaluator` (Application
layer, no remote API/DB reads) comparing a previously persisted
`InvoiceDraftDto` with a newly reconstructed,
server-side `InvoiceDraftCandidateDto`.

It requires:

- Same contract, room and billing period; stored status is Draft.
- Two distinct utility lines with complete rate and amount evidence,
  zero blockers and zero coverage issues; matching copied line totals
  and provisional total.
- Matching physical source identity by both `SourceType` and
  `SourceId`, with meter ID, reading date, previous/current values,
  recorded quantity and amount all equal.
- No duplicate/missing/extra sources and no inconsistent arithmetic.
  This rejects repricing, altered readings, changed replacement
  source history and "same total but different provenance".
- Any missing or unpriced current reading is treated as stale
  (never silently zero-priced).

Created 10 xUnit cases in
`InvoiceDraftFreshnessEvaluator_Tests`, including the positive
unchanged-state case and nine stale/disagreement cases.

**IMPORTANT: Not yet wired to any live read/issue endpoint.**
This is an internal comparison building block, not proof that saved
drafts are automatically invalidated or issuance is safe. A future
authorized issuance transaction must reload and compare under
an appropriate repeatable-read/serializable transaction, revalidate
operator scope, then make the final state change atomically. Any
later change invalidates an earlier comparison result.

No schema changes or production data writes. Neither draft nor
preview HTTP access is enabled.

### Validate locally (new 10-case unit gate)

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.Application.Tests\BuildingManagement.Application.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftFreshnessEvaluator_Tests"
```

**Expected: 10/10 PASS; not yet run on the user's machine.**
Keep the previous verified PostgreSQL concurrency result at 3/3.

## INVFRESH01–10 verified; internal persisted-draft freshness path (25/10/2026)

The user ran `InvoiceDraftFreshnessEvaluator_Tests` locally:

```
Test summary: total: 10, failed: 0, succeeded: 10, skipped: 0, duration: 1.6s
Build succeeded with 4 warning(s) in 8.6s
```

**10/10 PASS.** This verifies the detached pure comparator against
the unchanged state and altered tariff, reading, source identity/date,
replacement history, missing reading, room/period and inconsistent
arithmetic.

### Next read-only integration slice (WIP, NOT YET TESTED)

- Extended the **already internal-only** `IInvoiceDraftStorageAppService`
  and `InvoiceDraftStorageAppService` with
  `HasCurrentSourceSnapshotAsync(Guid id)`.
- It first verifies actual PostgreSQL `RepeatableRead` or
  `Serializable` isolation (rejects weaker ambient UOWs), retrieves
  the saved draft via the existing child-evidence loader,
  rebuilds the live candidate via
  `IInvoiceDraftCandidateAppService`, and invokes
  `InvoiceDraftFreshnessEvaluator`.
- Missing contract/room or untrustworthy meter history results in
  `false`. Unexpected infrastructure failures are not silently
  suppressed. A missing *draft* retains the existing NotFound error.
- No new remote HTTP API: the containing application service is
  still `[RemoteService(IsEnabled = false)]`. This check only
  reports freshness *as of the checking transaction*; **true does
  not authorize issuance**.
- Added two SQLite integration cases to
  `InvoicePersistenceIntegration_Tests` (`INVDB07`,
  `INVDB08`) that create an actual October utility-only draft,
  verify it matches live data, then modify the electricity tariff
  or monthly reading and assert that the draft is stale while the
  stored historical evidence is preserved.
- Expanded `InvoiceDraftIsolationGate_Tests` to three cases,
  including the new freshness method.
- Added PostgreSQL `PGINV07` to the existing safe isolation suite,
  confirming a caller with an explicit outer READ COMMITTED
  transaction cannot even query stored draft freshness. This test
  rejects before touching any invoice table (which may not yet
  exist on the old local PostgreSQL test DB).

**New local tests have NOT been executed.** Count expectations
for the narrow SQLite/EF filter: 10 evaluator cases + 8
invoice persistence cases + 3 isolation-attribute cases + 2
HTTP gate cases = **23 cases**. The PostgreSQL Isolation mode
has **5 cases** after PGINV07 (previously 4).

```powershell
git pull --ff-only origin wip/phase-2g2
dotnet test .\modules\buildingmanagement\test\BuildingManagement.EntityFrameworkCore.Tests\BuildingManagement.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~InvoiceDraftFreshnessEvaluator_Tests|FullyQualifiedName~InvoicePersistenceIntegration_Tests|FullyQualifiedName~InvoiceDraftIsolationGate_Tests|FullyQualifiedName~InvoiceRemoteAccessGate_Tests"
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\test-full-invoice-from-local-env.ps1 -Scenario Isolation
```

All persisted draft / application integration tests here use
SQLite test fixture; the PostgreSQL test only confirms runtime
isolation rejection, not a full migrated PostgreSQL stored-draft
integration or a concurrency-safe issuance transition.

The **next** security/business gates are scoped operator
authorization, supported correction/cancel/rebuild policy for stale
drafts, and atomic final issuance revalidation. No issue/post/pay,
receivable, or schema/data migration has been enabled in this change.

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
