# Phase 2H / payOS — EF Core migration baseline drift inventory
Date: 2026-11-06
Review status: **BLOCKING / source-only / no database mutation**

## Verified evidence

- Branch: `feat/phase2h-payos-signature-foundation-20261106`; commit `705be043c9b816ce44d4fccf66e670349dfd75c6`
- GitHub Actions: https://github.com/huynqhe186195/NgocTrangHouseManagementSystem/actions/runs/42228907372
- Offline `dotnet ef migrations add StagePayOsPaymentIntentsAndWebhookInbox` built and scaffolded successfully, against the existing `BuildingManagementDbContextModelSnapshot`. The fail-closed review correctly rejected the output.
- Generated `Up()`: **34 CreateTable** operations and **109 CreateIndex** operations. Exactly **two** intended payOS tables plus **32 unrelated tables** listed below.
- The 32 entries are **not evidence of 32 missing production tables**. They represent discrepancies between migration snapshot and the current EF model. Production database contents were not inspected.
- Source-only isolated tests and disposable hand-authored PG16 rehearsal are separate PASS gates. The official EF migration remains unreviewed and unapplied.

## Unrelated CreateTable operations (exact names emitted by CI)

### Contracts / deposits / financial closeout (6)
- BuildingManagementContractDepositCloseoutApprovals
- BuildingManagementContractDepositDebtOffsetApprovals
- BuildingManagementContractDepositDebtOffsetPostings
- BuildingManagementContractDepositDebtReviews
- BuildingManagementContractDepositRefundBankReviews
- BuildingManagementContractDepositSettlementDecisions

### Invoice, debt and payment ledger (10)
- BuildingManagementFinalInvoiceIssueEvidence
- BuildingManagementInvoiceIssuanceReviews
- BuildingManagementInvoicePaymentAllocationJournal
- BuildingManagementIssuedInvoiceLedger
- BuildingManagementMonthlyInvoiceAdjustmentDecisions
- BuildingManagementMonthlyInvoiceAdjustments
- BuildingManagementMonthlyInvoiceCandidates
- BuildingManagementMonthlyInvoicePaymentAllocation
- BuildingManagementMonthlyInvoiceReviews
- BuildingManagementMonthlyIssuedInvoiceLedger

### Move-out (1)
- BuildingManagementMoveOutRoomDispositions

### Official receipts and receivables (3)
- BuildingManagementOfficialInvoiceReceiptReviews
- BuildingManagementOfficialInvoiceReceivables
- BuildingManagementOfficialMonthlyReceiptLineAllocations

### Room change and service tariffs (12)
- BuildingManagementRoomChangeBusinessPolicies
- BuildingManagementRoomChangeBusinessPolicyReviews
- BuildingManagementRoomChangeCompletions
- BuildingManagementRoomChangeDayPricingRuleCandidates
- BuildingManagementRoomChangeDepositDispositionIntents
- BuildingManagementRoomChangeEvidenceArchives
- BuildingManagementRoomChangeHandoverEvidence
- BuildingManagementRoomChangeMidMonthCompletions
- BuildingManagementRoomChangeRequests
- BuildingManagementRoomChangeTenantRosterSnapshots
- BuildingManagementRoomScopedServiceTariffVersions
- BuildingManagementRoomServiceInvoiceSourceBindings

## Required remediation sequence

1. Find ownership/status for all 32 tables in existing Phase 2G/2H design and migration gates. Some are intentionally staged (source-only); do not treat all as overdue production schema.
2. Compare **three distinct states**: module EF snapshot, main/host EF migration snapshot/history, and a disposable PostgreSQL database after the approved full migration chain. Never substitute production inspection or arbitrary manual edits.
3. Decide whether payOS belongs to a separately versioned, isolated schema context or a reconciled approved baseline. Review migration ownership before choosing.
4. The newly generated migration must contain only the two payOS tables, the scoped composite FK and expected indexes/check constraints. Use the fail-closed review script and artifact from CI to verify the exact generated code.
5. Run the actual approved migration chain + rollback on disposable PostgreSQL 16 and authenticated building-scope HTTP tests. Verify concurrent duplicate webhook and order rejection under a DB transaction.
6. Never modify the EF snapshot manually simply to suppress drift; do not apply the 34-table generated migration or loosen the CI gate.

## Non-authorizations

This report does not approve cash-backed settlement, signed invoice issuance, financial journal posting, new permission grants, production database schema changes or merging PR #7.
