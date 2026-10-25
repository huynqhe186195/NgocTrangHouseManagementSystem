# Phase 2G.3A.7 — Versioned draft evidence ledger: migration and locking RFC

**State:** DESIGN REVIEW ONLY — NOT AN EF MIGRATION. DO NOT EXECUTE SQL BELOW.  
**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`

## 1. Non-negotiable invariants

- **One logical invoice ID** per room/month and contract/month. Retain
  `UX_BM_Invoice_Room_Period` and
  `UX_BM_Invoice_Contract_Period`.
- **Only the current invoice item-meter-source rows have charge attribution.**
  Retain `UX_BM_InvoiceSource_Global` on active rows. Archived
  evidence is an audit copy, never a second financial charge.
- Never allow a draft rebuild to recognize debt, issue an invoice,
  alter payments or remove previously captured rate/counter evidence.
- A draft can be versioned only if the operator is authorized for its
  building/room and the candidate is complete. The read-only
  `PreviewRebuildAsync` response does not authorize a write.
- Preserve transaction scope and lock order: **contract row**, then
  **invoice row**; do not grab a physical meter lock first from invoice
  rebuild. Normal meter/rate app writers continue without adopting an
  inverted lock order.
- Source changes between successful rebuild and future issue are
  detected again in the eventual *atomic issue transaction*; stale
  drafts must never automatically issue.

## 2. Candidate schema, for human review ONLY

The version model stores an **archived previous revision**, not the
new revision, because current invoice header/items/sources already
contain the active version.

- Existing `BuildingManagementInvoices`: add
  `CurrentRevision integer NOT NULL DEFAULT 1`; CHECK >=1.
  Use existing ABP `ConcurrencyStamp` as optimistic token; do not
  replace or remove it.
- New `BuildingManagementInvoiceDraftRevisions`: a write-once ledger
  keyed by independent `Id uuid` and unique
  `(InvoiceId, ArchivedRevision)`. The row stores
  `ArchivedByUserId uuid NOT NULL`, `ArchivedAtUtc timestamptz NOT NULL`,
  `Reason varchar(500) NOT NULL`,
  `EvidenceJson text NOT NULL`,
  `EvidenceSha256 varchar(64) NOT NULL`.
  A restricted FK to `BuildingManagementInvoices(Id)` preserves history.
- `EvidenceJson` intentionally uses **text rather than jsonb**:
  the checksum covers the exact deterministic UTF-8 JSON string.
  PostgreSQL jsonb's representation/normalization is not the
  serialized canonical byte sequence.
- `InvoiceDraftRevisionArchiveCodec` defines format version **1**,
  includes invoice/revision/actor/time/reason/scope, all utility
  rates/quantities/amounts and meter source identity/date/counters.
  SHA-256 detects accidental changes when checksum integrity is
  separately protected. It is **not** a signature and does not defend
  against a malicious DBA rewriting JSON *and* checksum.

The following DDL is **illustrative and not connected to the active
migration chain**. No DB schema or live data was changed:

```sql
-- RFC ONLY / DO NOT RUN
ALTER TABLE "BuildingManagementInvoices"
  ADD COLUMN "CurrentRevision" integer NOT NULL DEFAULT 1,
  ADD CONSTRAINT "CK_BM_Invoice_CurrentRevision_Positive"
    CHECK ("CurrentRevision" >= 1);

CREATE TABLE "BuildingManagementInvoiceDraftRevisions" (
  "Id" uuid NOT NULL PRIMARY KEY,
  "InvoiceId" uuid NOT NULL,
  "ArchivedRevision" integer NOT NULL,
  "ArchivedByUserId" uuid NOT NULL,
  "ArchivedAtUtc" timestamp with time zone NOT NULL,
  "Reason" character varying(500) NOT NULL,
  "EvidenceJson" text NOT NULL,
  "EvidenceSha256" character varying(64) NOT NULL,
  CONSTRAINT "FK_BM_InvoiceDraftRevision_Invoice"
    FOREIGN KEY ("InvoiceId")
    REFERENCES "BuildingManagementInvoices" ("Id")
    ON DELETE RESTRICT,
  CONSTRAINT "CK_BM_InvoiceDraftRevision_Positive"
    CHECK ("ArchivedRevision" >= 1),
  CONSTRAINT "CK_BM_InvoiceDraftRevision_Reason"
    CHECK (length(trim("Reason")) BETWEEN 8 AND 500),
  CONSTRAINT "CK_BM_InvoiceDraftRevision_Evidence"
    CHECK (length("EvidenceJson") BETWEEN 1 AND 1000000),
  CONSTRAINT "CK_BM_InvoiceDraftRevision_Hash"
    CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$')
);

CREATE UNIQUE INDEX "UX_BM_InvoiceDraftRevision_Invoice_Number"
  ON "BuildingManagementInvoiceDraftRevisions"
    ("InvoiceId", "ArchivedRevision");
```

**Immutability is not provided by INSERT-only application code alone.**
The approved migration must also include a tested PostgreSQL
`BEFORE UPDATE OR DELETE` trigger on the revision ledger (or an
equivalent separately enforced database permission boundary), with
explicit administrative archival/retention policy. Tenant/operator
permissions cannot bypass it. A rollback must refuse to drop the
ledger if it holds archived rows. DDL above is **not safe for direct
execution**, and is intentionally incomplete without these controls.

## 3. Transactional rebuild protocol (DESIGN, not yet implemented)

1. Authenticate operator and establish authorized building scope,
   require explicit reason and expected `CurrentRevision` **and**
   expected `ConcurrencyStamp` from the original draft read.
2. Start a single PostgreSQL `REPEATABLE READ` transaction;
   reject inherited weaker ambient isolation.
3. Lock the contract via the existing contract row-lock repository
   (`FindForUpdateAsync`), then lock the invoice row
   (`SELECT ... FOR UPDATE`) in that order.
4. Recheck invoice still `Draft`, room/contract/period identity,
   expected revision/stamp and operator permission. Reject any
   mismatch as a concurrency conflict; do NOT transparently
   overwrite the other editor's result.
5. Load current invoice and physical meter/rate candidate in this
   same snapshot; run `InvoiceDraftRebuildPreflight` **again**.
6. Build the complete archive from the **OLD** persisted active
   invoice/item/source graph and compute canonical JSON + SHA-256.
   Store ledger row with unique `(InvoiceId, ArchivedRevision)`.
7. Replace the active draft source rows **inside the same transaction**.
   Delete old source graph before inserting new active source graph
   to avoid `UX_BM_InvoiceSource_Global` conflicts. Keep the archive
   ledger independent from charge-owning indexes. Ensure EF
   change-tracking does not accidentally reinsert stale owned rows.
8. Set `CurrentRevision = previous + 1` and change the invoice
   concurrency stamp. Commit only when header/items/sources/archive
   all pass unique and foreign-key constraints.
9. On conflicts, PostgreSQL `40001` serializability errors, deadlock,
   or any failed validation, **ROLL BACK EVERYTHING** and return a
   safe retry-required result. Never accept "last writer wins".
10. Do **not** emit debt, financial journal, invoice issue status or
    tenant payment changes. Future issuance is a separate feature.

### Concurrency example

A and B both read invoice revision 1. A and B submit expected
revision 1 with its original stamp. Under the contract/invoice
lock order, only one may commit revision 2; the other must fail
expected-version/stamp or PostgreSQL transaction serialization.
A retry must explicitly reload/review the newly committed draft.
Unique `(InvoiceId, ArchivedRevision)` is a final database
defense against double-archiving the same revision.

The read-only `PreviewRebuildAsync` is not part of this lock and
cannot replace any of steps 1–10.

## 4. Backward compatibility and migration gates

- Additive migration only; existing persisted `Draft` records
  start at `CurrentRevision=1`, with **no fabricated archive**
  for initial creation. The first approved rebuild archives
  previous revision 1.
- Existing `UX_BM_InvoiceSource_Global`,
  `UX_BM_Invoice_Room_Period`,
  `UX_BM_Invoice_Contract_Period` remain unchanged.
- Do not add an archive FK to physical source rows: old readings
  may later be corrected/soft-deleted; archived values must remain.
- Require **EF-generated migration + updated model snapshot** only
  once the RFC is approved, then inspect an **offline idempotent SQL**
  preview and test scratch PostgreSQL full migration chain plus
  reverse migration behavior on disposable data.
- No `database update`, DbMigrator, `DROP DATABASE`, manual
  deletes, or production schema changes as part of this RFC.
- Migrations with populated archive rows must not silently
  drop historical evidence during down/rollback.
- Before wiring a mutating RebuildDraft endpoint: SQLite and real
  PostgreSQL tests for revision uniqueness, two simultaneous
  editors, source-edit/replacement races, crash/rollback atomicity,
  permissions, and unchanged/missing source fail-closed paths.
- **Issuance remains disabled**, regardless of any successful
  revision-schema tests.

## 5. Present implementation state

Already available and tested: in-memory old-draft
`InvoiceDraftRevisionEvidence` (REV01–09 **9/9 PASS**),
read-only `InvoiceDraftRebuildPreflight`,
read-only `PreviewRebuildAsync`, PostgreSQL isolation guard.

New WIP under test: deterministic archive codec and roundtrip
integrity checks (see `InvoiceDraftRevisionArchiveCodec_Tests`).
This RFC does not add DbSets, EF configurations, migration classes
or business mutation endpoints.
