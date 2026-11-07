# Phase 2H D04 — Technical Acceptance A (disposable PostgreSQL, no live money)

## 2026-11-07 21:18 D04A_REAL01 failure — root cause fixed in source, not retested

- User-provided real Windows stack trace: `19a-real-legacy-writer-acceptance.log`, lines 424–433, `System.FormatException: Index (zero based) must be greater than or equal to zero and less than the size of the argument list`, at test line **84** inside `Microsoft.EntityFrameworkCore.Storage.Internal.RawSqlCommandBuilder.Build` invoked by `ExecuteSqlRawAsync(up)`.
- **Confirmed cause:** the *repository-owned SQL migration* contains a PostgreSQL SHA256 regex quantifier `{64}`; EF Core treats it as a composite string-format placeholder before executing SQL. This is **not** a missing using/namespace or failed EF migration; the complete canonical EF migration chain already advanced past its prior line 77.
- **Fix on feature branch:** execute the unmodified `up` SQL through an explicitly transacted `NpgsqlCommand` on the hard-coded disposable `127.0.0.1:15432/payos_d04_legacy_test` connection; preserve and assert `'^[0-9a-f]{64}
**Decision as of 2026-11-07:** **HOLD — partial acceptance only**. This is a controlled evidence record, not a declaration of payment or business acceptance. PR #7 remains DRAFT. Do not merge, deploy, run a production migration, register a cash writer or start a payOS worker.

## Exact, user-observed baseline

- Source commit **`d7313738f515565a90c1053f9741c5a7109631e2`**. Windows **BuildOnly PASS**; local log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261107-163720`.
- Same exact commit: **PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS**; local log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261107-164943`. This exercises staging .NET/PG tests, rollback/reapply, an isolated receipt schema trigger and **synthetic** two-DB recovery SQL. The actual local TRX and log contents were not uploaded, so fine-grained test timestamps/counts are not independently audited here.
- This baseline **does not** contain an authenticated real money-writing HTTP endpoint, a real bank statement reviewer/Owner workflow, a revocation-safe Identity/Building commit lease or an activated cash writer.

## New gates on this feature branch (after baseline, UNEXECUTED)

1. `StagePayOsD04LegacySourceJournal` is now a **deliberately unregistered** source-only Migration class (no `[Migration]` or `[DbContext]` attribute). Its SQL must NOT become part of the normal BuildingManagement EF `MigrateAsync()` chain. A dedicated test `Draft_D04_receipt_migration_must_not_be_discoverable_in_canonical_ef_chain` checks this offline.
2. The new `D04RealLegacyWriterAcceptance_Tests.D04A_REAL01_Approved_ContractDeposit_Writes_One_Payment_Allocation_Receipt_And_Outbox` runs against **a second hard-coded disposable DB** (`127.0.0.1:15432/payos_d04_legacy_test`) and is gated by a synthetic-test-only opt-in environment value. It runs the actual complete canonical BuildingManagement EF migration chain, then applies D04 draft ledger SQL explicitly **only** on that disposable DB.
3. D04A_REAL01 tests the actual C# `PayOsD04LegacyPostingWriter` with **test doubles for** payOS reconciled evidence and active dual-authority lease, using actual EF Contract/Room/Floor/Tenant/Payment aggregates. It checks: missing review => **no money**, mocked revocation/authorization denial => **no money**, synthetic independent review and distinct Owner approval => exactly one Completed Payment, completed ContractDeposit DepositSettlement, UNIQUE receipt and outbox, independent legacy DB read-back, repeated same intent => **no extra cash rows**, staged intent stays **ConfirmedPendingPosting** until separately ACKed.
4. In `verify-payos-d04-local.ps1`, new fail-closed stage `19a-real-legacy-writer-acceptance` runs **after** creating the second temporary database and **before** synthetic D04 SQL rehearsal. The exact TRX `payos-d04-real-writer.trx` is mandatory via `Assert-Trx`. Earlier `PayOsStaging` filter does not include this opt-in test. Container cleanup remains in `finally`.

**The new gates are not green until a new owner-run exact-SHA BuildOnly + full disposable PostgreSQL suite returns PASS.** No .NET SDK, Docker or PostgreSQL are available to the assistant execution container.

## D04 A acceptance matrix (12 required scenarios)

| ID | Expected acceptance | Prior verified layer | Missing evidence |
|---|---|---|---|
| D04-01 | Authenticated, correctly scoped tenant checkout, QR/order/amount | Isolated checkout idempotency / fake provider tests PASS at baseline | End-to-end authenticated tenant HTTP and real payOS response |
| D04-02 | Valid signed webhook stored once, but not cash posted | Staging signed inbox and reconciliation PG tests PASS | Real HTTP signed webhook with policy-approved route |
| D04-03 | Wrong webhook signature/amount rejected without money | Staging negative PG tests PASS | End-to-end HTTP negative proof |
| D04-04 | No independent verified bank evidence, Owner approval => no posting | Pure policy and staging review negative tests PASS; new real writer negative test UNEXECUTED | Real independent authenticated bank review workflow |
| D04-05 | Different currently authorized bank reviewer and Owner => one actual financial commit | SQL schema/receipt fake review PASS at baseline; new actual C# writer test UNEXECUTED | Authenticated Owner/reviewer intake and revocation-safe lease |
| D04-06 | 10 signed webhook replays cannot create second payment/receipt | Staging inbox idempotency PASS; synthetic SQL one-source constraints PASS | Real financial writer replay under authenticated HTTP |
| D04-07 | Two concurrent writers yield one financial journal | Synthetic two-DB SQL concurrency PASS | Concurrent actual C# writer on canonical EF DB |
| D04-08 | Failure during writer transaction => zero partial money | Synthetic SQL pre-commit rollback PASS; new writer early-deny UNEXECUTED | Fault injection after Payment update and before receipt commit |
| D04-09 | Crash between legacy DB commit and staging ACK recovers without second money | PostgreSQL staging ACK + fake committed legacy reader PASS, synthetic SQL two-DB PASS | Two real DbContext commits with induced process termination |
| D04-10 | Cross-building tenant/Owner denied | Staging scope/reconciliation tests and isolated SQL guard PASS | Authenticated Owner/Tenant cross-building HTTP |
| D04-11 | Revoked role before/during commit never posts unauthorized cash | Staging mocked grant revocation PASS | Real ABP Identity/Building revoke-vs-post race with held lease |
| D04-12 | Globally reused case-folded bank reference (refund/payment) denied | SQL receipt/global reference claim trigger on disposable PG PASS | Real writer and refund collision concurrency on canonical DB |

### Final decision

- **Isolated staging engineering tests:** ACCEPTED at `d7313738f515` based on owner console evidence.
- **Real legacy writer source and new acceptance test:** **PENDING fresh execution** at new feature HEAD; do not transfer baseline PASS.
- **Authenticated two-actor, true HTTP and bank statement evidence:** **HOLD, implementation missing**.
- **D04 TECHNICALLY ACCEPTED:** **NO** (not all 12 conditions passed with real HTTP and ledger).
- **D04 BUSINESS ACCEPTED / production:** **NO**. No official D04 posting and no live payOS/production DB.

Next technical work after green real-writer rehearsal: build a separately authenticated identity+bank-evidence+Owner approval service, transaction-safe capability checks and a genuine HTTP/PG two-database kill/replay harness. These must be explicitly reviewed before allowing any runtime financial write.
` in SQL. The command commits only if the D04 draft DDL succeeds as a unit. No production DDL is activated, no bank/Owner checks are bypassed.
- Diagnostic printout has been separately improved to show actual xUnit exception and stack before unrelated `prune package data` warnings.
- **Result status:** D04A_REAL01 was **FAIL** on the user's last run and remains **NOT RETESTED** at the fix commit. Neither D04 technical nor business acceptance has been granted. Re-run the disposable test gate before changing any status to PASS.

**Decision as of 2026-11-07:** **HOLD — partial acceptance only**. This is a controlled evidence record, not a declaration of payment or business acceptance. PR #7 remains DRAFT. Do not merge, deploy, run a production migration, register a cash writer or start a payOS worker.

## Exact, user-observed baseline

- Source commit **`d7313738f515565a90c1053f9741c5a7109631e2`**. Windows **BuildOnly PASS**; local log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261107-163720`.
- Same exact commit: **PAYOS D04 LOCAL ISOLATED TEST BUNDLE PASS**; local log `C:\Users\Huynq\NgocTrang-Test-Logs\payos-20261107-164943`. This exercises staging .NET/PG tests, rollback/reapply, an isolated receipt schema trigger and **synthetic** two-DB recovery SQL. The actual local TRX and log contents were not uploaded, so fine-grained test timestamps/counts are not independently audited here.
- This baseline **does not** contain an authenticated real money-writing HTTP endpoint, a real bank statement reviewer/Owner workflow, a revocation-safe Identity/Building commit lease or an activated cash writer.

## New gates on this feature branch (after baseline, UNEXECUTED)

1. `StagePayOsD04LegacySourceJournal` is now a **deliberately unregistered** source-only Migration class (no `[Migration]` or `[DbContext]` attribute). Its SQL must NOT become part of the normal BuildingManagement EF `MigrateAsync()` chain. A dedicated test `Draft_D04_receipt_migration_must_not_be_discoverable_in_canonical_ef_chain` checks this offline.
2. The new `D04RealLegacyWriterAcceptance_Tests.D04A_REAL01_Approved_ContractDeposit_Writes_One_Payment_Allocation_Receipt_And_Outbox` runs against **a second hard-coded disposable DB** (`127.0.0.1:15432/payos_d04_legacy_test`) and is gated by a synthetic-test-only opt-in environment value. It runs the actual complete canonical BuildingManagement EF migration chain, then applies D04 draft ledger SQL explicitly **only** on that disposable DB.
3. D04A_REAL01 tests the actual C# `PayOsD04LegacyPostingWriter` with **test doubles for** payOS reconciled evidence and active dual-authority lease, using actual EF Contract/Room/Floor/Tenant/Payment aggregates. It checks: missing review => **no money**, mocked revocation/authorization denial => **no money**, synthetic independent review and distinct Owner approval => exactly one Completed Payment, completed ContractDeposit DepositSettlement, UNIQUE receipt and outbox, independent legacy DB read-back, repeated same intent => **no extra cash rows**, staged intent stays **ConfirmedPendingPosting** until separately ACKed.
4. In `verify-payos-d04-local.ps1`, new fail-closed stage `19a-real-legacy-writer-acceptance` runs **after** creating the second temporary database and **before** synthetic D04 SQL rehearsal. The exact TRX `payos-d04-real-writer.trx` is mandatory via `Assert-Trx`. Earlier `PayOsStaging` filter does not include this opt-in test. Container cleanup remains in `finally`.

**The new gates are not green until a new owner-run exact-SHA BuildOnly + full disposable PostgreSQL suite returns PASS.** No .NET SDK, Docker or PostgreSQL are available to the assistant execution container.

## D04 A acceptance matrix (12 required scenarios)

| ID | Expected acceptance | Prior verified layer | Missing evidence |
|---|---|---|---|
| D04-01 | Authenticated, correctly scoped tenant checkout, QR/order/amount | Isolated checkout idempotency / fake provider tests PASS at baseline | End-to-end authenticated tenant HTTP and real payOS response |
| D04-02 | Valid signed webhook stored once, but not cash posted | Staging signed inbox and reconciliation PG tests PASS | Real HTTP signed webhook with policy-approved route |
| D04-03 | Wrong webhook signature/amount rejected without money | Staging negative PG tests PASS | End-to-end HTTP negative proof |
| D04-04 | No independent verified bank evidence, Owner approval => no posting | Pure policy and staging review negative tests PASS; new real writer negative test UNEXECUTED | Real independent authenticated bank review workflow |
| D04-05 | Different currently authorized bank reviewer and Owner => one actual financial commit | SQL schema/receipt fake review PASS at baseline; new actual C# writer test UNEXECUTED | Authenticated Owner/reviewer intake and revocation-safe lease |
| D04-06 | 10 signed webhook replays cannot create second payment/receipt | Staging inbox idempotency PASS; synthetic SQL one-source constraints PASS | Real financial writer replay under authenticated HTTP |
| D04-07 | Two concurrent writers yield one financial journal | Synthetic two-DB SQL concurrency PASS | Concurrent actual C# writer on canonical EF DB |
| D04-08 | Failure during writer transaction => zero partial money | Synthetic SQL pre-commit rollback PASS; new writer early-deny UNEXECUTED | Fault injection after Payment update and before receipt commit |
| D04-09 | Crash between legacy DB commit and staging ACK recovers without second money | PostgreSQL staging ACK + fake committed legacy reader PASS, synthetic SQL two-DB PASS | Two real DbContext commits with induced process termination |
| D04-10 | Cross-building tenant/Owner denied | Staging scope/reconciliation tests and isolated SQL guard PASS | Authenticated Owner/Tenant cross-building HTTP |
| D04-11 | Revoked role before/during commit never posts unauthorized cash | Staging mocked grant revocation PASS | Real ABP Identity/Building revoke-vs-post race with held lease |
| D04-12 | Globally reused case-folded bank reference (refund/payment) denied | SQL receipt/global reference claim trigger on disposable PG PASS | Real writer and refund collision concurrency on canonical DB |

### Final decision

- **Isolated staging engineering tests:** ACCEPTED at `d7313738f515` based on owner console evidence.
- **Real legacy writer source and new acceptance test:** **PENDING fresh execution** at new feature HEAD; do not transfer baseline PASS.
- **Authenticated two-actor, true HTTP and bank statement evidence:** **HOLD, implementation missing**.
- **D04 TECHNICALLY ACCEPTED:** **NO** (not all 12 conditions passed with real HTTP and ledger).
- **D04 BUSINESS ACCEPTED / production:** **NO**. No official D04 posting and no live payOS/production DB.

Next technical work after green real-writer rehearsal: build a separately authenticated identity+bank-evidence+Owner approval service, transaction-safe capability checks and a genuine HTTP/PG two-database kill/replay harness. These must be explicitly reviewed before allowing any runtime financial write.
