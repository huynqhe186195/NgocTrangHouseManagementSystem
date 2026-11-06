# Payment provider decision — payOS (2026-11-06)
**Status:** APPROVED PROVIDER CHOICE / DESIGN ONLY. Owner selected payOS for the future Payments module. Not a payOS API implementation, not production approval.

## Boundaries
- **D04:** immutable H4 obligations, manually attested staging evidence, deposit top-up/issued-rent credit/refund journal, accounting balances, authorization, SQL integrity, idempotency and transaction safety.
- **Payments:** creates and tracks payment intent, uses payOS to create payment link / VietQR, receives signed callbacks, verifies signature + amount/orderCode/currency/status and reads provider state as needed, handles duplicate/late/out-of-order webhook, and emits one immutable `PaymentVerified` event or payment source record.
- **Consumer interface:** D04 only accepts a trusted, canonical `VerifiedPaymentEvidence` (provider, providerEventId, orderCode, paymentLinkId, bank reference, amount VND, provider status, verified timestamp, beneficiary/tenant/contract, source type, source hash). Keep any provider-specific objects OUT of D04 domain model. Validation cannot depend solely on returnUrl navigation.
- **Refund:** not automatically assumed available or approved. If payout APIs/provider eligibility or authorization are not available, retain a separate reviewed manual refund path. Refund payout and any external banking reconciliation belong to Payments, not D04's staging closeout.
- **No real bank integration now.** No keys, tokens, checksum_key, webhook URL, sandbox account, or live requests in repository. Never infer bank settlement from a test fixture or user-submitted transaction reference.

## Future payment acceptance gates
1. Secrets managed outside source; allowlist and provider signature verification using documented payOS HMAC-SHA256; invalid or missing signature => reject.
2. Independently match immutable internal invoice/obligation identifier, merchant orderCode, expected amount and currency (VND); mismatches => reject.
3. Durable webhook inbox with unique (provider,event/payment identity), idempotent transitions, replay safety, recoverable retries, logged audit trail without secrets.
4. Pending, expired, cancelled, successfully paid and disputed outcomes; never rely on browser redirect as authoritative settlement.
5. Tenant/building ownership and payment-to-invoice association enforced at commit.
6. External refund capability, beneficiary verification, maker-checker and reversal workflow requires a separate decision and acceptance gate.
7. Contract/API version pinned before coding; CI tests use isolated signed fixtures, not a real payment.

## Provider references
- https://payos.vn/docs/api/
- https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/
- https://payos.vn/docs/du-lieu-tra-ve/webhook/

**D04 release rule:** Internal staging acceptance can close without bank connection; external cash-collected/refunded claims remain deferred and must never be labelled verified. Production, main merge and live payments remain HOLD.
