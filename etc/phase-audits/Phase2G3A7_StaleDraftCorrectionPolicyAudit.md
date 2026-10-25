# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Immutable old-draft evidence prototype — NOT YET RUN (25/10/2026)

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

**Expected 9/9 PASS; UNVERIFIED** until user supplies log.

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
