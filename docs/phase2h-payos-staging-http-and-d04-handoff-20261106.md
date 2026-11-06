# Phase 2H D04/payOS — Staging HTTP checkout, webhook and reconciliation (2026-11-06)

**Delivery scope: source implemented, release HOLD.** This is a constrained pilot for an **existing pending ContractDeposit Payment**, operated by an authenticated building-scoped account. It is NOT an enabled tenant self-service portal, a production payOS integration, an accepted D04 cash journal or an approved refund channel.

## API routes

All routes are `/api/payments/payos/staging/` and **default OFF**. Controllers return 404 unless a separately reviewed opt-in configuration is active on a host with `ASPNETCORE_ENVIRONMENT=Staging`.

| Endpoint | Caller | Input | Result | Money moved? |
|---|---|---|---|---|
| `POST checkout/{paymentId}` | Authenticated + active building-scoped ManageContract | Existing Payment GUID only | New or recovered orderCode, checkout URL, QR when freshly created | No automatic journal; checkout URL may accept **real bank funds** if merchant credentials are live |
| `POST webhook` | payOS callback without ABP identity, **HMAC required** | Signed JSON up to 64 KiB | 2xx **only after durable insert or exact duplicate**; known payOS confirm-webhook test fixture acknowledged separately | No D04 posting |
| `POST reconcile/{paymentId}` | Same authorization as checkout | Existing Payment GUID only | Provider lookup + signed inbox, produces **ConfirmedPendingPosting** review state | No D04 posting |

A webhook/return-url is NOT proof of paid money until independently reconciled. The browser return URL is informational only.

### Configuration and security

Never place real API credentials in this repository, CI logs, client payloads or mobile binaries. From **server-side secret storage only**, provide the following configuration keys:

`BuildingManagement:PayOsStaging:Enabled=true`
`BuildingManagement:PayOsStaging:Activation=I_ACCEPT_ISOLATED_PAYOS_STAGING_ONLY`
`BuildingManagement:PayOsStaging:ClientId`
`BuildingManagement:PayOsStaging:ApiKey`
`BuildingManagement:PayOsStaging:ChecksumKey`
`BuildingManagement:PayOsStaging:ReturnUrl` = operator-approved absolute HTTPS URL
`BuildingManagement:PayOsStaging:CancelUrl` = operator-approved absolute HTTPS URL
`ConnectionStrings:PayOsStaging` = **dedicated** PostgreSQL database named `bm_payos_staging_<unique_suffix>` reachable **only at 127.0.0.1 / localhost** from the staging host

The payOS staging database must differ from BuildingManagement and Identity DBs, so EF migration ownership remains isolated. Host DI is conditionally registered only if every hard gate passes at startup. Do not set these flags in production: a non-Staging host fails startup if enabled. Any real merchant key can issue a **real-money** link even when the application calls itself staging; do not enter credentials or register payOS webhook until a controlled financial acceptance session is explicitly authorized.

### Existing Payment + authorization

The API does NOT accept BuildingId, payerId, amount, orderCode, returnUrl or callback URL. It loads the existing Payment and requires:
- authenticated operator, ABP `ManageContract` permission, active identity account and unrevoked membership of the contract's actual building;
- `Payment.Status=Pending`, purpose `ContractDeposit`, method `BankTransfer`, channel `VietQr`, linked Contract Signed or Active and integer VND amount from the stored Payment only;
- persisted PaymentIntent created **before** any provider request; unique PaymentId and orderCode. An unknown provider response keeps the SAME orderCode. Never create an alternative order because of a timeout/5xx/409.
- account scope and resource authorization checked again before a provider operation.

This is not permission for an arbitrary staff member or tenant to issue new charges. It is a pilot restricted to existing contract-deposit Payments. Other invoice/deposit source types require separate accepted D04 policy and endpoint-level permissions.

### Webhook + reconciliation

- Enforce payOS payment webhook HMAC, exact link ID, integer VND amount/order, reference and success code. Duplicate JSON keys and oversized payloads rejected.
- PostgreSQL transaction acquires `FOR UPDATE` on the intended order. Unique provider reference prevents double attribution across multiple building orders. The exact repeated payload can be ACK'd; payload mismatch is **not** considered idempotent.
- Events arriving during `Created` -> `AwaitingPayment` transition trigger a **non-2xx retry**, not a silent loss. Paid-after-cancel goes to manual review.
- An unknown order returns non-2xx (retry), **except** payOS's signed, documented `confirm-webhook` test fixture (orderCode 123/amount 3000/reference TF230204212323/description VQRIO123) when checksum verification also succeeds; this fixture is not inserted as business revenue.
- Reconciliation requires both a durable signed inbox row and payOS merchant GET showing `PAID` with full matching amount/link/order. It marks the payOS intent `ConfirmedPendingPosting` and the inbox `ReconciledNoPosting`. It **does not** call `PaymentAppService.CompleteAsync`, issue a D04 receipt, allocate a deposit, release a room or refund.

### Explicitly incomplete before BUSINESS ACCEPTED

1. A separate, reviewed **financial posting authorization** (independent approver, audit evidence, amount due after other settlements, bank reference uniqueness across legacy D04, tenant/room ownership and approved D04 locks). `ManageContract` alone must never permit cash posting.
2. Durable **cross-database** posting-outbox/saga: the payOS staging DB and legacy Building DB have separate transactions. Simultaneous Payment completion, revoke or other deposit receipts must not double-credit funds. Recovery after a crash between posting D04 and marking PayOs Posted must be idempotent and independently verified.
3. Tenant self-service ABP/Firebase identity linkage and authenticated HTTP/Pg tests for denied account, revoked membership, cross-building Payment IDs, exact checkout amount, provider timeout, webhook retries and race conditions.
4. Verify complete Phase 2H/D04 regression gates on the *exact* commit and the full historical migration chain. The old BuildingManagement EF snapshot drift for 32 unrelated entities remains a separate issue.
5. A controlled **merchant callback/QR confirmation** with the account owner's explicit authorization, live webhook delivery proof, reconciliation evidence, and eventual bank-verification plan. No bank integration or production deployment is assumed.

### Current CI boundary

Earlier checked commit `e24172b706d0f5c5f4e89ced8c71b512dc2d508e`: four isolated jobs succeeded, including .NET HTTP Host Release build and PostgreSQL checkout/timeout source tests. Subsequent commit(s) have not been fully verified: GitHub Actions workflow #42238828690 (attempts 1 and 2) and #42239295387 reported failure **before a runner was assigned**, with zero job steps and no build/test logs. Do NOT re-label these as code test failures or declare full PASS. Repeat exact-head CI when a runner is available; keep PR Draft / no merge until then.

This note is not a production runbook, an instruction to activate real payments or a permit to skip business acceptance.
