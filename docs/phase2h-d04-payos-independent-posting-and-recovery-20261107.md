# Phase 2H / D04 — payOS independent posting and cross-database recovery

Date: **2026-11-07**  
Scope: **source-only, isolated payOS staging PR #7; not production/business accepted**.

## Current exact boundary

The existing staging checkout, signed webhook inbox and provider reconciliation do **not** complete legacy `Payment` or insert a D04 financial journal. In particular, `ConfirmedPendingPosting` means *pending independent financial review*, **not a posted receipt**. Keep payOS staging default OFF.

The code under `BuildingManagement.Application/Payments/PayOsD04PostingHandoffPolicy.cs` adds **a pure, non-exposed decision gate**, not an approved cash-writing API:

- The immutable payOS intent must target exactly the expected existing pending `ContractDeposit` Payment, tenant, building, amount and payOS order.
- The previously signed webhook inbox must already be durably reconciled and identify the same reference as the intent.
- Independent bank evidence **separate from provider PAID / webhook** must match the full VND amount and bank reference. A verified document digest and UTC review instant are required.
- A currently authorized bank reviewer and a **different** currently authorized Owner posting approver must both be present; source contract/building/deposit coverage and legacy Payment must still match.
- The legacy Payment must be locked and its globally unique bank reference must be unused, as asserted by a trusted legacy-D04 reader. **No caller-supplied flags may satisfy these assertions**.
- The result `ReadyForLockedD04Writer` means *eligible for an independently secured, transactional writer*, not that the policy itself authorizes or executes any posting.

`PlanAfterLegacyCommit` is also pure: if Payment is already Completed with the identical reference **and the authoritative existing legacy D04 cash/settlement journal is verified**, it yields `MarkPayOsPostedAfterLegacyProof`; a second retry yields `AlreadyAcknowledged`. Every other inconsistent combination is `StopAndInvestigate`.

### Explicit missing implementation — must be finished before payment acceptance

**The above flags are not wired to real readers yet, the approval is not durable, and no cross-database financial writer/outbox exists.** Do not treat source-only policies, simulated test inputs, a bank-reference hash, payOS merchant status or a webhook as independent bank-settlement evidence.

The remaining transactional protocol should be:

1. In the *payOS staging DB*, retain unique PaymentIntent, signed inbox and provider reconciliation evidence. Stage an immutable independent bank review and Owner approval with actor IDs, proof references, timestamp, scope, amount, hash and expiry. Hold the transaction when manual bank evidence is unavailable.
2. Use an authenticated *internal-only* legacy D04 command, not the public webhook and not generic `PaymentAppService.CompleteAsync`. Acquire Identity/Building grant at commit time; enforce least privilege for an independent Owner approver.
3. In the **BuildingManagement DB** SERIALIZABLE transaction, lock Contract then Payment/affected deposit sources in the established order; verify amount due after prior cash/allocations and source reference uniqueness, including soft-deleted historical receipts and journal entries. Insert exactly one audited source receipt/settlement and a deterministic unique `payos` handoff key/outbox in this **same** legacy DB transaction. Any conflict rolls back all legacy changes. Do not mark the external payOS intent Posted in this transaction.
4. After the legacy transaction commits, the coordinator may mark `PayOsPaymentIntent` Posted under a separate locked payOS transaction **only after reading back the exact committed Payment + receipt journal**. This is an acknowledgment of an existing financial posting, not a second receipt.
5. If the coordinator crashes *before* legacy commit, nothing has been posted: repeat the exact idempotency key after re-authorization/review. If it crashes *after* legacy commit but before the payOS status update, the next pass must detect the exact legacy journal and only acknowledge it. If provider/webhook/legacy evidence conflicts, STOP for manual investigation; never "fix" the gap by crediting the tenant again.
6. Reject/hold expired, canceled, posted, cross-building or revoked-role flows. Handle bank refund/payout separately; it remains HOLD.

A real two-database integration test must induce process termination at BOTH commit boundaries, test duplicate coordinators, bank reference collisions across buildings, stale permission grants, stale debt/deposit coverage, ambiguous provider timeout, and repeated ACK. PostgreSQL unique constraints must remain the ultimate guard, not read-before-write alone.

## Changes staged in this session

| Item | Source or test | Status |
|---|---|---|
| Checkout/reconciliation provider timeout => HTTP 503 on server-side timeout, client disconnect still propagates | `PayOsStagingPaymentsController.cs` | Committed; not executed at new HEAD |
| Retry keeps one reserved order after POST or recovery GET timeout | `PayOsStagingCheckoutService_Tests.cs` | 2 new source tests |
| Independent bank review / Owner approval / exact source and scope gate | `PayOsD04PostingHandoffPolicy.cs` | Pure policy only |
| Correct post-crash recovery classification | `PayOsD04PostingHandoffPolicy.cs` | Pure plan, no writer |
| Missing bank evidence, same reviewer/approver, changed amount/scope, legacy conflict, crash/replay cases | `PayOsD04PostingHandoffPolicy_Tests.cs` | 10 new source tests |

## Test and release gates

| Gate | Evidence requirement | State |
|---|---|---|
| G1 | Exact-head .NET Domain/Application/Host build and source tests | BLOCKED: GitHub Actions jobs not starting |
| G2 | EF migration model drift, actual apply/rollback/reapply, scoped FK and unique indices on disposable PG16 | Previously passed on older HEAD, **not verified on current HEAD** |
| G3 | Authenticated HTTP + PostgreSQL checkout, anonymous signed webhook, scope/revoke, duplicate concurrency, provider timeout/5xx, recheck after revoke | PARTIAL source tests; exact-head acceptance needed |
| G4 | Real independently authorized bank evidence / Owner permission / durable approval and append-only audit | NOT IMPLEMENTED |
| G5 | Unique legacy D04 receipt/outbox, cross-DB coordinator and two crash-point recovery | NOT IMPLEMENTED |
| G6 | Full D01–D04 regression, bank/provider controlled acceptance, no double journal under concurrent writers | NOT ACCEPTED |
| G7 | Production keys, live callbacks, real money, refund and release | **HOLD**, explicitly not authorized |

### CI blockage, do not bypass

The latest observed PR workflow failures (including 2026-11-07) ended in seconds with **zero job steps** and no runner assignment. This cannot be honestly classified as a failed .NET assertion. GitHub repository Owner should inspect **Actions usage/billing/limits, workflow permissions and runner availability** in the GitHub UI. Do not silence failed jobs, skip security checks, or call this green.

When runners become available, run these before considering merge:

```powershell
dotnet test modules/buildingmanagement/test/BuildingManagement.Domain.Tests/BuildingManagement.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~PayOs'
dotnet test modules/buildingmanagement/test/BuildingManagement.Application.Tests/BuildingManagement.Application.Tests.csproj -c Release --filter 'FullyQualifiedName~PayOs'
dotnet build src/NgocTrangHouseManagementSystem.HttpApi.Host/NgocTrangHouseManagementSystem.HttpApi.Host.csproj -c Release
# Then use the existing payOS isolated workflow's dedicated PostgreSQL 16
# disposable-db migration and signed-inbox/checkout tests.
# Never point those tests at a customer/production DB.
```

Do not merge PR #7, invoke provider APIs with merchant credentials, migrate production, or mark D04 BUSINESS ACCEPTED until all evidence is independently checked.
