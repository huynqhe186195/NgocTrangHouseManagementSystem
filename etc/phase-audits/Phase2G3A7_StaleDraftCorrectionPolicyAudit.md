# Phase 2G.3A.7 — Stale draft correction, replacement and issuance policy audit

**Date:** 2026-10-25  
**Branch:** `wip/phase-2g2`  
**Status:** OPEN / design gate only; no destructive draft commands, DB migrations or financial issuance enabled.

## Verified test baseline

- `InvoiceDraftFreshnessEvaluator_Tests`: **10/10 PASS**.
- `InvoiceDraftIsolationGate_Tests`: **3/3 PASS**.
- `InvoiceRemoteAccessGate_Tests`: **2/2 PASS**.
- `InvoicePersistenceIntegration_Tests`: **8/8 PASS** including
  actual saved-draft stale detection after price/reading revision.
- PostgreSQL PGINV02, 03 and 07: **5/5 PASS** isolation safety.
- PostgreSQL PGINV04–06: **3/3 PASS** concurrent direct SQL, application
  mutators and physical replacement candidate behavior.
- Existing 109-case broader regression **predates latest code** and
  must be repeated before approving this phase. Four existing
  tenant Mapperly warnings remain.

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
- Run the full selected 109-case regression on latest WIP revision.

**Phase 2G.3A.6:** targeted application/EF/PostgreSQL gates green.
**Phase 2G.3A.7:** correction/lifecycle governance OPEN.
**Production issuance / receivables:** BLOCKED.
