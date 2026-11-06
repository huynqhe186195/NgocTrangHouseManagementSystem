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
