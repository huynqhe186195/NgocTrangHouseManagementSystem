# Phase 2G.3–2G.5 — Direct contract-end P0 closure and real-data move-out blockers

**Branch:** `wip/phase-2g2`  
**Date:** 2026-10-27 (Asia/Bangkok)  
**STATUS: DEVELOPMENT ONLY / NEW TESTS NOT LOCALLY VERIFIED / NO PRODUCTION DEPLOYMENT**

This delivery addresses the highest-risk gap in the Phase 2G & 2H business process specification (v1.0, 22 October 2026): `ContractAppService.EndAsync` previously ended an active contract and released a room **without requiring final meter signoff, issued and paid final invoice, debt reconciliation, deposit settlement or completed handover**.

## 1. P0 production AppService boundary changed

`modules/buildingmanagement/src/BuildingManagement.Application/Contracts/ContractAppService.cs`

- `EndAsync` now has `[RemoteService(IsEnabled = false)]` and calls `IDirectContractTerminationGate.EnsureDirectTerminationAllowedAsync()` as its **first statement before loading any contract, room or mutating entity**.
- The runtime implementation, `DenyAllDirectContractTerminationGate`, unconditionally throws `AbpAuthorizationException`.
- This preserves a callable C# interface for migration/legacy integration analysis but **removes the standalone HTTP contract-end endpoint** and prevents live internal direct calls from bypassing settlement.
- Do not make the default gate permissive. The eventual verified `CompleteMoveOut` service must lock the contract/request/invoice/deposit/room in an agreed order and perform all state transitions in a single audited transaction with rollback.

**Operational impact:** even a legitimate operator will be unable to directly end a contract on this development branch until the full 2G.3/2G.4/2G.5 workflow is implemented and approved. Do NOT deploy it as a fully usable product yet.

## 2. Legacy state-transition tests retained without runtime bypass

`LegacyContractTerminationFixtureGate` exists ONLY in the `BuildingManagement.Application.Tests` test assembly and is wired ONLY in the isolated SQLite EF test module. It is default-deny; **11 existing historical** `ContractAppService_Tests` methods explicitly enter an `AsyncLocal`-scoped historical transition assertion. This keeps previous early/normal end reasons, reservation/renewal priority, room state and contract-history tests meaningful.

A separate new test `Should_Refuse_Direct_End_Of_Active_Contract_Without_MoveOut_Settlement` runs **without** the fixture permission, asserts an authorization exception, and verifies the Contract remains Active and the room status remains unchanged.

No production project depends on the fixture. It is NOT an owner/manager authorization mechanism.

## 3. Actual read-only close-out readiness over existing repositories

`MoveOutCompletionReadinessAppService.GetAsync(requestId)` is **RemoteService disabled** and reads:

- persisted MoveOutRequest and its Contract;
- persisted five checklist tasks and their completed flags;
- draft invoice existence for the exact Contract/Room (not treated as final invoice);
- approved-date final-meter evidence via existing `IMoveOutFinalMeterReadinessAppService`.

Its pure `MoveOutCompletionReadinessEvaluator` returns scoped blockers for unapproved request, invalid/mismatched active contract, missing/duplicate/uncompleted checklist tasks, and missing meter evidence/draft invoice.

**Non-negotiable financial blockers are explicitly retained:** `FinalInvoiceIssuanceNotImplemented`, `FinalDebtClearanceNotVerified` and `ContractDepositSettlementNotVerified`.

`MoveOutCompletionReadinessDto.CanCompleteMoveOut` remains **always false**. A draft does not authorize billing or debt recognition; existing payments/deposits are not assumed to be final settlement evidence. The service does not call `End`, `Complete`, release rooms, alter payment/deposit balances, or issue invoices.

This read model is a concrete bridge across 2G.1 + 2G.2 + draft 2G.3, not a pretend finished 2G.5 command. It remains internal because cross-building authorization has not yet been integrated for MoveOut.

## 4. New test coverage pending local or CI verification

- **Application:** 6 `CLOSECHECK` pure evaluator tests + 1 direct End denial and unchanged state test. Existing 11 End tests retain historical business assertions with explicit test-only scope.
- **SQLite EF:** 3 `CLOSEDB` tests reading real move-out/contract/checklist/invoice repositories and asserting no contract/room mutation. Meter evidence is an isolated read-only stub; no fabricated invoice issue/debt/deposit posting occurs.
- **HTTP Host:** `APIAUTO13` asserts no generated remote `ContractAppService.EndAsync` route, no remote close-out readiness service and default deny gate.
- **Source safety:** new `etc/scripts/check-move-out-lifecycle-gate.ps1` asserts remote-disable, first-statement denial, no close-out mutations, three mandatory financial blockers, and draft-only invoice model. It is read-only, NOT a DB role or real-transaction test.
- **Batch:** `verify-2g-security-batch.ps1 -Mode Full` now includes Domain.Tests, Application.Tests, SQLite EF, HTTP Host, and **all three** read-only safety scripts. Still excludes PostgreSQL test projects and any DbMigrator.

Command from repository root:

```powershell
git pull --ff-only origin wip/phase-2g2
powershell -NoProfile -ExecutionPolicy Bypass -File .\etc\scripts\verify-2g-security-batch.ps1 -Mode Full
git status --short
```

**If this batch fails, fix the first compiler/test diagnostic and rerun the batch; do not relax fail-closed boundaries or edit production DB.** Assistant cannot compile with .NET locally; new tests are not labeled PASS before user or CI confirms.

## 5. Release blockers and next full vertical slice (not fictional completion)

1. Confirm final invoice pricing/cutoff policy, issued immutable snapshot, invoice adjustment rules and settled receivable ledger. InvoiceStatus currently has only Draft.
2. Approve/deliver deposit close-out with documented damage evidence, required reason for positive compensation, refund-vs-carry-forward exclusivity, idempotency, and transaction-level anti-double-refund.
3. Authorized, persisted checklist signoff (meter evidence, inspection, final bill, handover, deposit). Existing `MoveOutChecklistTask.IsCompleted` has no public completion workflow.
4. Implement **one real CompleteMoveOut UOW** with verified active contract + approved request, reviewed final meter/bill/debt/deposit state, locks and concurrency checks; only there transition Contract.End, MoveOutRequest.Complete and Room Available/Reserved. Include rollback tests and authenticated user A/B security tests.
5. Review and apply schema/roles ONLY to disposable PostgreSQL. No production migration, no invoice posting or permission assignment authorized by this work.
6. Continue to 2H room transfer **after** 2G final settlement and contract close-out invariants are tested.

The local untracked `etc/phase-audits/Phase2G3A7_RevisionLedger_Generated_Review.sql` is untouched; it must never be staged, executed, deleted or overwritten by a batch.
