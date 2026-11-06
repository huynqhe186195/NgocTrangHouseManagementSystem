# Phase 2H Payment — payOS implementation boundary (2026-11-06)

Status: **FOUNDATION ONLY / NOT BUSINESS ACCEPTED**. Source branch is `wip/phase-2h-h4-execution`. No production deployment, database migration, bank transfer, refund, or automatic posting is authorized.

## Implemented in this branch

- Source-only payOS intent domain model and EF mapping with unique orderCode/paymentLinkId/providerReference, scoped building and payer IDs, immutable target and strict lifecycle. No migration has been generated; database persistence is NOT available until reviewed migration and tests.
- A pure checkout-request signature generator requiring positive integer VND/order codes and HTTPS redirect URLs.
- A signed payment confirmation guard requiring matching order code, VND amount, payment-link ID, provider success status and bank reference; it is read-only and does not settle or acknowledge payment.
- A pure payment-request webhook `data` signature verifier using sorted keys, HMAC-SHA256 and fixed-time byte comparison. It does **not** share logic with payOS payout signatures.
- Unit test source for valid reordered fields, amount tampering, incorrect key, malformed signatures, checkout signing and signed-but-mismatched receipt data.
- No endpoint and no `Payment.Complete` invocation. Tests and compiler execution have **not yet been verified**.

Official protocol references:
- https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/
- https://payos.vn/docs/api/

## Remaining implementation gates (HOLD)

1. Generate and review PostgreSQL migration for the staged intent mapping and apply only against disposable test database first; verify unique/check constraints, null-key semantics, race/concurrent inserts and complete EF model snapshot. The source mapping is NOT a migrated table. Define explicit purpose/provider and irreversible payment allocation target; verify building and payer ownership with authenticated API tests.
2. Generate monotonic collision-free payOS numeric orderCode; create payment link server-side with checksum key and server-only credentials; persist request/response and use provider idempotency/reconciliation strategy before retry. Do not create checkout links for unauthorized buildings or unissued invoice lines.
3. Add a dedicated anonymous HTTPS webhook ingress with request size limits, parsed payload validation, valid signature requirement, unique event/transaction deduplication, safe raw evidence storage and safe ACK only after durable recording. Never trust a browser return/cancel redirect as payment confirmation.
4. Match provider/channel, amount, orderCode, currency=VND, paymentLinkId, reference and state against the persisted intent. Under a transaction and row lock, apply once, emit outbox event for downstream invoicing and preserve audit trail; reject cross-building, expired/replayed/conflicting events.
5. Decide explicit mapping to existing `Payment` and separate D04-H4 obligations. Do not call generic `CompleteAsync` from a public untrusted webhook. An H4 top-up must still satisfy owner/role, independent-review and source-of-funds gates. Refund/payout is separate and explicitly HOLD.
6. Provide reconciling read-side job with payOS query API for lost/out-of-order events; map failures/retries without changing previously completed payments.
7. Add signed, invalid, replayed, concurrent, mismatched amount/currency, wrong building, unknown order, cancellation-after-paid, out-of-order, crashed-transaction and recovery tests. Run exact-head .NET build and domain/application/EF/PostgreSQL authenticated HTTP tests and inspect CI logs.
8. Supply user-owned payOS credentials via secret manager, configured HTTPS callback domain, verified channel, and optional sandbox/test-channel evidence before payment UAT. **Never commit credentials or use real funds in CI.**

## Non-goals

- Bank statement settlement or proof of actual collection in this branch.
- Deploying to production, merging to main, changing original D04 staging evidence.
- Treating any checkout URL, browser success redirect or syntactically valid webhook signature as sufficient proof to post a financial journal without transaction reconciliation.

## 2026-11-06 follow-up

Added source-only `PayOsPaymentIntent` state model, EF Core configuration and domain test source. Transitions allow linked intent -> confirmation pending posting -> posted, but **do not perform posting**. The application/DB writer must validate authoritative provider status, lifecycle of delayed payments, linked IDs, expiry and ownership in a serializable or locked transaction. A 'cancel unpaid' state is not permission to dismiss a subsequently confirmed bank payment: it must enter manual reconciliation rather than be lost or double-booked. No production migration, CI pass or runtime use has been verified.


## 2026-11-06 provider transport increment

Added source-only `PayOsPaymentLinkClient` for **POST /v2/payment-requests** and read-only **GET /v2/payment-requests/{orderCode}**, with server-injected credentials, explicit HTTPS merchant host, signed create-request fields, positive order/amount, secure redirect URLs and response match checking. The create response provides checkout URL/QR but does **not** persist an intent or post a receipt. Provider queries are for eventual reconciliation, not proof of an approved journal.

Official webhook sample golden signature is independently embedded in application tests; ambiguous duplicate JSON field names are rejected. Added dedicated secret-free, no-DB GitHub workflow `.github/workflows/payos-foundation-isolated.yml` to build and run both domain and application PayOs tests. Workflow status must be checked on exact commit; **a file committed to GitHub is not a test pass**.

PAYMENT API HOLD: A service must use a durable reservation/intent before making an HTTP create call and reconcile uncertain provider outcomes before retry; never issue a second payment request merely because the first timed out. Store checkout URL and QR only after verifying intent scope, amount and ownership.

## 2026-11-06 staged webhook inbox increment

Source-only `PayOsWebhookInboxRecord` and EF Core configuration were added, including globally unique provider reference, a foreign key to `PayOsPaymentIntent`, building scope, SHA-256 payload fingerprint, received timestamp and terminal reconciliation/manual-review transitions. The inbox explicitly does **not** represent paid funds or a posted journal. Source-level tests cover invalid data and one-way transitions; CI now includes them.

**Must remain HOLD:** This is not a durable deployed inbox until a complete EF migration/model snapshot has been generated and tested on a disposable PostgreSQL database. The production application has no webhook writer, endpoint, journal or outbox. A real ingress must verify the authentic payOS signature, match stored intent by orderCode/link/amount/tenant/building, acquire an intent-level DB lock, atomically insert the inbox record under a unique constraint, and ACK only once safely persisted. A second event with a conflicting reference/amount must go to manual review, not silently deduplicated. Raw webhook bodies/keys must not be logged publicly; a hash alone is not proof of settlement.

No existing D04 settlement state or production schema has been modified.

## 2026-11-06 generated migration drift — BLOCKING, never bypass

Exact-head CI `7835316d3b8fb07de891c5f2111f72339c7024ea`, [run 42216603287](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/42216603287):
- PostgreSQL 16 disposable hand-authored rehearsal: SUCCESS (unique, FK, concurrent insert, rollback).
- Domain + Application payOS tests and EF Core compile: SUCCESS.
- **Offline EF migration preview: intentionally FAILED the drift gate**. A real `dotnet ef migrations add` succeeds after restore/build, but its generated `Up()` contains **34 `CreateTable` calls instead of the expected 2**. The pre-existing EF snapshot and the live model are not aligned. The preview is uploaded as CI artifact `payos-ef-migration-preview` (artifact ID 13501238051) for review; **do not commit/apply it**.
- The gate is **correctly red** and must not be weakened to claim acceptance.

Recommended safe remediation:
1. Audit existing migration history and authoritative PostgreSQL baseline across both module and host contexts; identify which 32 unrelated tables arise from legacy snapshot drift. Do not assume missing tables are absent in live DB.
2. Reconcile the baseline snapshot on a dedicated review branch, with a DB-design reviewer checking every entity/index/FK and matching the historical migration chain. Do not hand-delete unrelated operations from the migration while leaving snapshot/model inconsistent.
3. Regenerate payOS EF migration. Require exactly two new tables, the composite foreign key (PaymentIntentId, BuildingId, OrderCode), unique transaction references and no D04 journal modifications.
4. Execute the **actual** complete migration chain on a fresh disposable PostgreSQL 16 database and verify reverse migration, rollback, concurrent inserts and authenticated building-scope tests. This is separate from the passing rehearsal SQL.
5. Retain PR Draft, stop all production schema changes and any external financial posting until evidence gates pass.

## 2026-11-06: isolated EF payOS migration — source committed

**The payOS-only migration has been separated from legacy BuildingManagementDbContext migrations.** A bounded `PayOsPaymentMigrationDbContext` applies the same EF table mappings as runtime `BuildingManagementDbContext`, but owns migration history in its own `__EFMigrationsHistory_PayOs` table. Runtime tables are explicitly marked `ExcludeFromMigrations()` in the legacy context so subsequent legacy model snapshots cannot create them a second time.

Reviewed source generated by EF Core 10.0.9 and committed in:
- `modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/Migrations/PayOs/20261106141311_StagePayOsPaymentIntentsAndWebhookInbox.cs`
- the matching `.Designer.cs`
- `PayOsPaymentMigrationDbContextModelSnapshot.cs`

The two-table migration has exactly 2 `CreateTable` and 11 `CreateIndex` operations, including global transaction-reference uniqueness and a composite `PaymentIntentId + BuildingId + OrderCode` foreign key. The 32 unrelated legacy module entities are not part of the bounded context. **The old legacy snapshot drift remains a distinct, unresolved workstream**, not silently repaired.

[GitHub CI #42231216407](https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/42231216407) verified the generated isolated EF migration on disposable PostgreSQL 16: apply, check indexes/scoped FK/invalid inserts, roll back to migration 0, reapply, and check the dedicated migration history. No production database was used. The committed migration source now has its own CI drift check via `dotnet ef migrations has-pending-model-changes` and final exact-head CI should be consulted before merging.

**No financial writer is authorized.** Even when schema migrations pass, this does not constitute API/webhook integration, bank reconciliation, authenticated multi-building acceptance or D04 journal acceptance. The dedicated factory only permits offline scaffolding or fixed-name loopback disposable PostgreSQL acceptance; it is not a production migration procedure.
