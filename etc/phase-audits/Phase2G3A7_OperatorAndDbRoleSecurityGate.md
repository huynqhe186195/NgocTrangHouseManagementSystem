# Phase 2G.3A.7 — Invoice revision: authorization and database-role security gate

**Status:** SECURITY DESIGN GATE / NOT APPROVED FOR PRODUCTION  
**Branch:** `wip/phase-2g2`  
**Date:** 2026-10-26

## 1. Verified code state (source review)

1. `InvoiceDraftStorageAppService` is marked `[RemoteService(IsEnabled = false)]`.
   It implements draft creation, read, freshness comparison, and
   `PreviewRebuildAsync`, but **not** a mutating `RebuildDraftAsync`.
2. `IInvoiceDraftStorageAppService` exposes the same internal methods
   and no mutation endpoint for invoice revision.
3. `InvoiceDraftRebuildPreflight` is a **read-only** check. Its
   `ReadyForRevisionDesign` result is **not** a permission grant
   and cannot authorize a later transaction.
4. `BuildingManagementPermissions` currently defines only its
   group name; the permission definition provider creates an
   empty group. **No invoice revision permission is implemented.**
5. `Invoice` stores a draft header and active utility snapshot, but
   it currently has **no `CurrentRevision` column/property/transition**.
6. The reviewed V2 migration adds only the separate
   `BuildingManagementInvoiceDraftRevisions` archive ledger,
   constraints, and PostgreSQL triggers. It **does not** create
   an operator authorization boundary or DB-role GRANT/REVOKE.
7. Earlier user-local PostgreSQL tests **PGREV01–04: 4/4 PASS**
   verified ledger DDL/trigger/empty and populated rollback,
   including an insert-versus-downgrade race on a throwaway database.
   They **did not** simulate the non-owner runtime PostgreSQL role.

**Therefore: migrating application data, enabling revision writes or
issuing invoices is NOT authorized by the current test results.**

## 2. Required separation of PostgreSQL roles

These are **requirements** for future deployment, **not executable SQL
for the current database**.

| Principal | Invoice draft revision ledger | Migration / trigger control |
| --- | --- | --- |
| Runtime application role | At most `SELECT, INSERT`, scoped through authorized server operations | Must not own table, trigger function or schema; no `ALTER`, `DROP`, `TRIGGER`, `UPDATE`, `DELETE`, or `TRUNCATE` privilege |
| Migration owner / migration operator | DDL allowed only during reviewed, explicitly approved deployment | Separate credentials; must never be used by the running web application |
| Database owner / administrator | Full exceptional access by design | Audited, break-glass only; backup and independent recovery controls |
| End user / tenant / receptionist | No direct database credentials | Application authorization + building/room scope checks required |

PostgreSQL table ownership (and superuser-level access) can bypass
or disable triggers and execute destructive DDL. An append-only trigger
is only a defense-in-depth boundary for **non-owner** runtime roles.
Testing triggers while connected as an administrative account is not
a substitute for testing actual deployed runtime privileges.

Future migration rollout must validate the **effective role**, including
role inheritance/membership, superuser status and permissions for schema,
table and trigger function. SQL `CHECK` constraints verify format
only; SHA-256 is not an HMAC or digital signature and does not prove
authenticity if privileged actors rewrite both evidence and digest.

No automatic `GRANT`, `REVOKE`, `CREATE ROLE`, `ALTER ROLE`,
`CREATE DATABASE`, or changes to real application credentials should
be made during this design gate.

## 3. Application-level authorization and write contract

Before exposing a mutating revision command:

1. Define a dedicated permission such as
   `BuildingManagement.Invoices.RebuildDraft` and a distinct
   `BuildingManagement.Invoices.ReadRevisionHistory` permission.
   Define and test the permission provider; avoid implicitly assigning
   revision rights to everyone who can view a room.
2. Resolve server-side authenticated operator identity; reject an
   anonymous identity, an empty actor ID or a client-supplied actor ID.
3. Resolve the invoice -> contract -> room -> floor -> building
   relationship and verify the operator has **current** access to that
   specific building and room, independently from a generic permission.
4. Read-only `PreviewRebuildAsync` is advisory. A later mutating
   command must recheck authorization, complete source/rate candidate,
   invoice status `Draft`, expected revision **and** expected
   `ConcurrencyStamp` inside its own transaction.
5. Preserve PostgreSQL lock order **contract -> invoice**.
   The transaction must use `REPEATABLE READ` or stronger and
   reject a weaker ambient UOW. Record an immutable archive
   **before replacing** active utility sources, in the same atomic
   transaction. A concurrency loser must not overwrite the winner.
6. Enforce `CurrentRevision >= 1` in an independently reviewed future
   additive migration with default 1; it does **not** exist today.
   Old revision is stored once under the unique
   `(InvoiceId, ArchivedRevision)` key.
7. Do not alter historical evidence, recognize debt, create charges,
   issue invoices, touch payments or move deposit balances as a side
   effect of rebuilding a draft.
8. On conflicts, invalid meter/rate provenance, missing authorization,
   stale source state or insertion errors, **roll back every new active
   source and ledger write**. Log correlation without disclosing
   sensitive resident information.

## 4. Mandatory remaining verification

- **SEC01**: an ordinary runtime DB role is distinct from table owner
  and cannot update/delete/truncate revision rows or disable triggers.
- **SEC02**: actual deployed runtime role can perform the intended
  authorized append/read operations, without owning ledger/schema.
- **SEC03**: application user without building scope cannot preview or
  rebuild another building's draft, even if they know its invoice ID.
- **SEC04**: expired/revoked permission is checked again *inside* the
  transaction, never inferred from old UI preflight state.
- **REV05**: version/stamp mismatch causes full rollback, including
  no orphan archive and no partial meter-source replacement.
- **REV06**: duplicate rebuild of the same draft under concurrent
  requests yields exactly one committed revision.
- **REV07**: correction made while meter/rate source changes must
  either atomically use one valid snapshot or abort; issuance
  separately revalidates freshness.
- **DEP01**: reviewed migration is rehearsed against a sanitized
  production-shaped database with expected role and backup policy.
- **OPS01**: migration `Down()` must never be used to destroy archived
  evidence. The tested populated-ledger refusal is deliberate.

These are pending gates, **not PASS**. No runtime role or auth policy
can be inferred just because the scratch PostgreSQL migration tests pass.

## 5. Safest next engineering increment

**Build the permission and scoped authorization design/tests first**,
separately from adding a public API or an EF write path. Keep
`[RemoteService(IsEnabled = false)]` and no mutating invoice
revision method until its authorization and transaction protocols
have dedicated negative and concurrency tests.

Do not run `dotnet ef database update`, DbMigrator, handwritten
GRANT/REVOKE on app data, or turn on issue/debt/posting functionality
as part of this gate.
