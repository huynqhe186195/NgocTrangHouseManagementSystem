# Phase 2H Payment — payOS implementation boundary (2026-11-06)

Status: **FOUNDATION ONLY / NOT BUSINESS ACCEPTED**. Source branch is `wip/phase-2h-h4-execution`. No production deployment, database migration, bank transfer, refund, or automatic posting is authorized.

## Implemented in this branch

- A pure payment-request webhook `data` signature verifier using sorted keys, HMAC-SHA256 and fixed-time byte comparison. It does **not** share logic with payOS payout signatures.
- Unit test source for valid reordered fields, amount tampering, incorrect key and malformed signatures.
- No endpoint and no `Payment.Complete` invocation. Tests and compiler execution have **not yet been verified**.

Official protocol references:
- https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/
- https://payos.vn/docs/api/

## Remaining implementation gates (HOLD)

1. Define durable `PaymentIntent` with unique (provider, orderCode), unique provider payment-link ID when non-null, building scope, payer, exact integer VND amount, currency, expiry, provider status and immutable purpose (invoice/deposit).
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
