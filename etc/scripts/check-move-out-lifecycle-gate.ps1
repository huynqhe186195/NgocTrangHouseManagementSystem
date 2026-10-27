# Source-only fail-closed regression for the 2G.5 completion boundary.
# Does not access a database, mutate resources or authorize settlement.
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$branch = (& git -C $root branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'wip/phase-2g2') {
    throw 'Safety stop: expected wip/phase-2g2.'
}
function Read-Source([string]$relativePath) {
    $full = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Safety stop: missing $relativePath"
    }
    return [IO.File]::ReadAllText($full)
}

$contract = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Contracts/ContractAppService.cs'
$directGate = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Contracts/DirectContractTerminationGate.cs'
$httpConvention = Read-Source 'src/NgocTrangHouseManagementSystem.HttpApi.Host/Security/BuildingManagementAuthenticatedControllerConvention.cs'
$readiness = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/MoveOuts/MoveOutCompletionReadinessAppService.cs'
$meterSignoff = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/MoveOuts/MoveOutFinalMeterSignoffAppService.cs'
$readinessDto = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application.Contracts/MoveOuts/MoveOutCompletionReadinessDto.cs'
$invoiceStatus = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain.Shared/Invoices/InvoiceStatus.cs'
$finalReview = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalMoveOutBillReviewAppService.cs'
$finalReviewModel = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalMoveOutBillReview.cs'
$paymentEvidence = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalMoveOutPaymentEvidenceAppService.cs'
$paymentEvidenceModel = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalMoveOutPaymentEvidence.cs'
$paymentState = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Payments/Payment.cs'
$issuedDebtCalculation = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/IssuedInvoicePaymentReconciler.cs'
$issuePreflight = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceIssuePreflight.cs'
$financialTermsReview = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceFinancialTermsReview.cs'
$invoiceReleaseReadiness = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceReleaseReadiness.cs'
$invoiceReleaseService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalInvoiceReleaseReadinessAppService.cs'
$issuePreflightService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalInvoiceIssuePreflightAppService.cs'
$invoiceIssuanceScope = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/InvoiceIssuanceScopePolicy.cs'
$issuanceReviewTicket = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/InvoiceIssuanceReviewTicket.cs'
$issuanceReviewSchema = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityConfigurations/InvoiceIssuanceReviewTicketConfiguration.cs'
$issueEvidenceRecord = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceIssueEvidenceRecord.cs'
$paymentAllocationJournal = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/InvoicePaymentAllocationJournalEntry.cs'
$paymentAllocationAudit = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/InvoicePaymentAllocationJournalAudit.cs'
$paymentAllocationReviewService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/InvoicePaymentAllocationLedgerReviewAppService.cs'
$paymentAllocationSchema = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityConfigurations/InvoicePaymentAllocationJournalEntryConfiguration.cs'
$issueDriftReview = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceIssueEvidenceDriftReview.cs'
$issueDriftService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalInvoiceIssueEvidenceDriftAppService.cs'
$issueEvidenceSchema = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityConfigurations/FinalInvoiceIssueEvidenceRecordConfiguration.cs'
$issueEvidenceDbContext = Read-Source 'modules/buildingmanagement/src/BuildingManagement.EntityFrameworkCore/EntityFrameworkCore/BuildingManagementDbContext.cs'
$depositLedgerReview = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/DepositSettlements/ContractDepositLedgerReview.cs'
$depositLedgerService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/DepositSettlements/ContractDepositLedgerReviewAppService.cs'
$depositSettlement = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/DepositSettlements/DepositSettlement.cs'
$depositErrorCodes = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain.Shared/BuildingManagementErrorCodes.cs'
$finalEvidence = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Domain/Invoices/FinalInvoiceEvidencePackage.cs'
$finalEvidenceService = Read-Source 'modules/buildingmanagement/src/BuildingManagement.Application/Invoices/FinalInvoiceEvidencePackageAppService.cs'

if ($contract -notmatch '(?s)\[RemoteService\(IsEnabled\s*=\s*false\)\]\s*public\s+async\s+Task<ContractDto>\s+EndAsync\s*\(') {
    throw 'Safety stop: direct Contract.EndAsync route is no longer disabled.'
}
if ($contract -notmatch '(?s)EndAsync\(Guid id\)\s*\{\s*await _directEndGate.EnsureDirectTerminationAllowedAsync\(\);' -or
    $directGate -notmatch 'class DenyAllDirectContractTerminationGate' -or
    $directGate -notmatch 'AbpAuthorizationException') {
    throw 'Safety stop: direct Contract.EndAsync no longer denies before mutation.'
}
# ABP can still discover a [RemoteService(false)] operation as a
# conventionally routed MVC action. Removal from the HTTP ApplicationModel
# must accompany the fail-closed internal Contract.EndAsync method.
if ($httpConvention -notmatch 'typeof\(ContractAppService\)' -or
    $httpConvention -notmatch 'nameof\(ContractAppService.EndAsync\)' -or
    $httpConvention -notmatch 'controller\.Actions\.Remove\(directEnd\)') {
    throw 'Safety stop: forbidden direct Contract.EndAsync MVC action removal missing.'
}
if ($readiness -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $readiness -notmatch 'FinalInvoiceIssuanceNotImplemented' -or
    $readiness -notmatch 'FinalDebtClearanceNotVerified' -or
    $readiness -notmatch 'ContractDepositSettlementNotVerified' -or
    $readiness -match '\.Complete\s*\(|\.End\s*\(') {
    throw 'Safety stop: read-only completion preflight mutated state or lost financial blockers.'
}
# ABP proxy-based UOW/auditing/validation cannot reliably be applied
# to a sealed ApplicationService. Keep its service and method interceptable.
if ($readiness -match '\bsealed\s+class\s+MoveOutCompletionReadinessAppService\b' -or
    $readiness -notmatch 'public\s+virtual\s+async\s+Task<MoveOutCompletionReadinessDto>\s+GetAsync') {
    throw 'Safety stop: MoveOut readiness service must remain ABP-interceptable.'
}
if ($readinessDto -notmatch 'CanCompleteMoveOut\s*=>\s*false' -or
    $invoiceStatus -notmatch 'Draft\s*=\s*0') {
    throw 'Safety stop: completion approval or invoice issuance model changed; re-review financial workflows.'
}

# Final-month rent/utility reconciliation is an INTERNAL preview only.
# Never promote a draft snapshot to an issued invoice or debt posting.
if ($finalReview -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $finalReview -notmatch 'public\s+virtual\s+async\s+Task<FinalMoveOutBillReview>\s+GetAsync' -or
    $finalReviewModel -notmatch 'CanIssue\s*=>\s*false' -or
    $finalReviewModel -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $finalReview -match '\.(Issue|PostDebt|Pay|Refund|End|Complete)\s*\(') {
    throw 'Safety stop: final bill review exposed or changed to a monetary writer.'
}

# Reconciliation reads historical receipts (including soft-deleted
# records) but never treats a DRAFT as an issued receivable, a paid bill,
# or a deposit deduction. Retain the HTTP boundary and fail-closed flags.
if ($paymentEvidence -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $paymentEvidence -notmatch 'public\s+virtual\s+async\s+Task<FinalMoveOutPaymentEvidence>\s+GetAsync' -or
    $paymentEvidence -notmatch '_softDeleteFilter\.Disable\(' -or
    $paymentEvidenceModel -notmatch 'OutstandingDebt\s*=>\s*null' -or
    $paymentEvidenceModel -notmatch 'IsDebtReconciled\s*=>\s*false' -or
    $paymentEvidenceModel -notmatch 'CanSettleDeposit\s*=>\s*false' -or
    $paymentEvidence -match '\.(Issue|PostDebt|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: payment evidence must remain internal, read-only and nonsettling.'
}

# Durable meter signoff is NOT a general-purpose checklist override.
# It must remain internal, transactional, contract-locked, room scoped and
# backed by both physical reading identifiers on the approved exit date.
if ($meterSignoff -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $meterSignoff -notmatch '\[UnitOfWork\(isTransactional:\s*true\)\]' -or
    $meterSignoff -notmatch 'FindForUpdateAsync\(contractId\)' -or
    $meterSignoff -notmatch 'BuildingBusinessCapability\.ManageRoom' -or
    $meterSignoff -notmatch 'HasValidFinalMeterEvidence\(request, evidence\)' -or
    $meterSignoff -notmatch 'MarkFinalMeterReadingsVerified\(_clock\.Now\)' -or
    $meterSignoff -notmatch 'IAsyncQueryableExecuter queryExecuter' -or
    $meterSignoff -notmatch '_queryExecuter\.FirstOrDefaultAsync\(' -or
    $meterSignoff -match '\bawait\s+AsyncExecuter\.' -or
    $meterSignoff -match '\.(End|Complete|Issue|Refund|PostDebt|DeleteAsync)\s*\(') {
    throw 'Safety stop: verified final-meter checklist lost transaction, scope or evidence gate.'
}

# Finite payment terminal states and issued-only debt computation must
# remain insulated from unissued Invoice Draft and automatic deposit release.
if ($paymentState -notmatch 'EnsurePendingTransition\("complete"\)' -or
    $paymentState -notmatch 'EnsurePendingTransition\("fail"\)' -or
    $paymentState -notmatch 'EnsurePendingTransition\("cancel"\)' -or
    $issuedDebtCalculation -notmatch 'PaymentPurpose\.InvoicePayment' -or
    $issuedDebtCalculation -notmatch 'PaymentStatus\.Completed' -or
    $issuedDebtCalculation -notmatch 'DuplicatePaymentIdentifier|same PaymentId cannot be applied twice' -or
    $issuedDebtCalculation -notmatch 'CanSettleDeposit\s*=>\s*false') {
    throw 'Safety stop: payment terminal states or issued-invoice reconciliation guards missing.'
}

# Immutable final-month issuance evidence is still only a PREPARATION.
# It must include both metered utility snapshots, a SHA-256 digest and
# must NOT authorize issue, receivable posting or deposit settlement.
if ($finalEvidence -notmatch 'SHA256\.HashData' -or
    $finalEvidence -notmatch 'VerifyChecksum\(' -or
    $finalEvidence -notmatch 'CanIssue\s*=>\s*false' -or
    $finalEvidence -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $finalEvidenceService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $finalEvidenceService -notmatch 'public\s+virtual\s+async\s+Task<FinalInvoiceEvidencePackage>\s+GetAsync' -or
    $finalEvidenceService -match '\.(Issue|PostDebt|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: immutable final invoice evidence exposed or converted to a monetary writer.'
}

# The same completed security deposit must never be applied, refunded
# and forfeited through sequential Payment state changes. These guards are
# not a substitute for an authorized, locked settlement journal.
if ($paymentState -notmatch 'EnsureUnappliedCompletedDeposit\("apply"\)' -or
    $paymentState -notmatch 'EnsureUnappliedCompletedDeposit\("refund"\)' -or
    $paymentState -notmatch 'EnsureUnappliedCompletedDeposit\("forfeit"\)' -or
    $paymentState -notmatch 'Purpose != PaymentPurpose\.ReservationDeposit' -or
    $paymentState -notmatch 'DepositDisposition != Payments\.DepositDisposition\.Unapplied') {
    throw 'Safety stop: deposit disposition can be replayed or double-counted.'
}

# Existing deposit allocations are audit evidence only. A payment marked
# Unapplied is not cash held in this contract unless a completed allocation
# exists. Internal review must detect cross-contract reallocation and
# must never produce an authorized refundable balance.
if ($depositLedgerReview -notmatch 'DepositPaymentOverAllocatedOrCrossContract' -or
    $depositLedgerReview -notmatch 'DepositAllocationPaymentMismatch' -or
    $depositLedgerReview -notmatch 'CompletedContractDepositNotFullyAllocated' -or
    $depositLedgerReview -notmatch 'DuplicateDepositTransactionReference' -or
    $depositLedgerService -notmatch 'x\.ContractId == contractId' -or
    $depositLedgerService -notmatch 'PaymentPurpose\.ContractDeposit' -or
    $depositLedgerReview -notmatch 'DepositPaymentDoubleDisposed' -or
    $depositLedgerReview -notmatch 'MixedDepositDispositionRequiresReview' -or
    $depositLedgerReview -notmatch 'PendingDepositPaymentDispositionRequiresReview' -or
    $depositLedgerReview -notmatch 'RecordedOutgoingExceedsVerifiedIncoming' -or
    $depositLedgerReview -notmatch 'RefundableBalance\s*=>\s*null' -or
    $depositLedgerReview -notmatch 'CanRefund\s*=>\s*false' -or
    $depositLedgerReview -notmatch 'CanOffsetDebt\s*=>\s*false' -or
    $depositLedgerService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $depositLedgerService -notmatch '_softDeleteFilter\.Disable\(' -or
    $depositLedgerService -match '\.(Refund|Pay|Complete|End|Issue|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: deposit allocation ledger review no longer fail-closed.'
}

# Contract-held deposit forfeitures need a documented cause, including
# legacy pending records. Do not extend this constraint to the independent
# fixed reservation no-show forfeiture policy.
if ($depositSettlement -notmatch 'EnsureContractForfeitReason\(type, sourceContractId, Notes\)' -or
    $depositSettlement -notmatch 'EnsureContractForfeitReason\(Type, SourceContractId, Notes\)' -or
    $depositSettlement -notmatch 'DepositSettlementType\.Forfeit' -or
    $depositSettlement -notmatch 'sourceContractId\.HasValue' -or
    $depositErrorCodes -notmatch 'ContractDepositForfeitReasonRequired' -or
    $depositLedgerReview -notmatch 'ContractDepositForfeitReasonMissing') {
    throw 'Safety stop: contract deposit forfeiture lost documented-reason guard.'
}

# Issuance preparation must remain an internal read-only diagnostic.
# Neither a verified base amount nor completed draft-linked payments may
# turn a utility DRAFT into a payable invoice or a contract deposit credit.
if ($issuePreflight -notmatch 'CanIssue\s*=>\s*false' -or
    $issuePreflight -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $issuePreflight -notmatch 'IsDebtCalculated\s*=>\s*false' -or
    $issuePreflight -notmatch 'AuxiliaryFeePolicyUndecided' -or
    $issuePreflight -notmatch 'IssuerBuildingAuthorizationUnavailable' -or
    $issuePreflight -notmatch 'ImmutableIssuedInvoiceLedgerNotImplemented' -or
    $issuePreflight -notmatch 'AuditedPaymentAllocationNotImplemented' -or
    $issuePreflightService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $issuePreflightService -notmatch '\[UnitOfWork\(isTransactional:\s*true\)\]' -or
    $issuePreflightService -notmatch 'GetEvidenceBundleAsync\(' -or
    $issuePreflightService -notmatch 'new FinalInvoiceIssueEvidenceBundle\(' -or
    $issuePreflightService -match '\.(Issue|PostDebt|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: invoice issuance preparation lost fail-closed financial or HTTP boundary.'
}

# Invoice issue scope preparation is not a reusable permission token.
# Future issuing must still resolve the graph in a locked DB transaction,
# persist issued evidence and prevent a repeated invoice for the period.
if ($invoiceIssuanceScope -notmatch 'IssuerBuildingMemberships\.Contains\(facts\.BuildingId\)' -or
    $invoiceIssuanceScope -notmatch 'IssuerResourceRelationshipMismatch' -or
    $invoiceIssuanceScope -notmatch 'PersistedInvoiceEvidenceMismatch' -or
    $invoiceIssuanceScope -notmatch 'ExistingIssuedInvoiceForPeriod' -or
    $invoiceIssuanceScope -notmatch 'FinancialIssuancePrerequisitesIncomplete' -or
    $invoiceIssuanceScope -notmatch 'CanIssue\s*=>\s*false' -or
    $invoiceIssuanceScope -notmatch 'CanPostReceivable\s*=>\s*false') {
    throw 'Safety stop: design-only invoice issuer scope escaped its financial authorization boundary.'
}

# Prepared final-invoice proof is STAGED evidence, NOT an issued ledger.
# The approved PostgreSQL migration and lock-protected authorized issuer
# do not yet exist. Preserve the DB uniqueness and FK relationships while
# keeping all financial status transitions disabled.
if ($issueEvidenceRecord -notmatch 'IsInvoiceIssued\s*=>\s*false' -or
    $issueEvidenceRecord -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $issueEvidenceRecord -notmatch 'VerifyStoredEvidence\(' -or
    $issueEvidenceRecord -notmatch 'SHA256\.HashData' -or
    $issueEvidenceRecord -notmatch 'staged\.VerifyStoredEvidence\(\)' -or
    $issueEvidenceRecord -notmatch 'source\.Current - source\.Previous' -or
    $issueEvidenceRecord -notmatch 'source\.RecordedDate\.Month != BillingMonth' -or
    $issueEvidenceRecord -notmatch 'source\.SourceId == Guid\.Empty' -or
    $issueEvidenceRecord -notmatch 'x\.RateId == Guid\.Empty' -or
    $issueEvidenceRecord -notmatch 'capturedAtUtc\.Kind != DateTimeKind\.Utc' -or
    $issueEvidenceSchema -notmatch 'UX_BM_FinalIssueEvidence_Draft' -or
    $issueEvidenceSchema -notmatch 'UX_BM_FinalIssueEvidence_Contract_Period' -or
    $issueEvidenceSchema -notmatch 'UX_BM_FinalIssueEvidence_Room_Period' -or
    $issueEvidenceSchema -notmatch 'DeleteBehavior\.Restrict' -or
    $issueEvidenceDbContext -notmatch 'FinalInvoiceIssueEvidenceRecordConfiguration' -or
    $issueEvidenceRecord -match '(?m)^\s*public\s+(?:async\s+)?(?:void|Task|Task<\w+>)\s+(?:Issue|Post|Pay|Refund|Delete)\s*\(') {
    throw 'Safety stop: staged invoice issue evidence was promoted to a live financial ledger.'
}

# Archive checksum integrity alone cannot prove that the live draft
# remains unchanged. Rebuild the current persisted source graph using the
# ORIGINAL capture time, and reject stale/mismatched evidence. The audit
# must remain hidden and must not issue, settle or post anything.
if ($issueDriftReview -notmatch 'archived\.VerifyStoredEvidence\(' -or
    $issueDriftReview -notmatch 'CapturedAtUtc' -or
    $issueDriftReview -notmatch 'FinalInvoiceEvidencePackage\.Capture\(' -or
    $issueDriftReview -notmatch 'ArchivedEvidenceDiffersFromCurrentDraft' -or
    $issueDriftReview -notmatch 'CurrentDraftEvidenceInvalid' -or
    $issueDriftReview -notmatch 'CanIssue\s*=>\s*false' -or
    $issueDriftReview -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $issueDriftService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $issueDriftService -notmatch '\[UnitOfWork\(isTransactional:\s*true\)\]' -or
    $issueDriftService -notmatch '_sources\.GetListAsync\(' -or
    $issueDriftService -match '\.(Issue|PostDebt|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: staged evidence drift review lost its source-graph or nonposting guard.'
}

# The new invoice payment allocation journal is STAGED ONLY.
# No writer, production migration or Issued status exists. A completed
# receipt linked to a Draft cannot be treated as debt payment. The initial
# model refuses duplicate PaymentId and split allocations, and never
# reports money as posted or available to offset a security deposit.
if ($paymentAllocationJournal -notmatch 'IsRecognizedAllocation\s*=>\s*false' -or
    $paymentAllocationJournal -notmatch 'CanReduceOutstandingDebt\s*=>\s*false' -or
    $paymentAllocationJournal -notmatch 'CanOffsetDeposit\s*=>\s*false' -or
    $paymentAllocationJournal -notmatch 'StageForValidation\(' -or
    $paymentAllocationJournal -notmatch 'PaymentStatus\.Completed' -or
    $paymentAllocationJournal -notmatch 'PaymentPurpose\.InvoicePayment' -or
    $paymentAllocationAudit -notmatch 'PaymentAlreadyUsedInAnotherAllocation' -or
    $paymentAllocationAudit -notmatch 'AllocationSourceReceiptMismatch' -or
    $paymentAllocationAudit -notmatch 'IssuedInvoiceLedgerAndAllocationAuthorizationMissing' -or
    $paymentAllocationAudit -notmatch 'CanPost\s*=>\s*false' -or
    $paymentAllocationAudit -notmatch 'CanReduceDebt\s*=>\s*false' -or
    $paymentAllocationSchema -notmatch 'UX_BM_InvoiceAllocation_Payment' -or
    $paymentAllocationSchema -notmatch 'HasOne<Payment>' -or
    $paymentAllocationSchema -notmatch 'DeleteBehavior\.Restrict' -or
    $issueEvidenceDbContext -notmatch 'InvoicePaymentAllocationJournalEntryConfiguration' -or
    $paymentAllocationJournal -match '(?m)^\s*public\s+(?:async\s+)?(?:void|Task|Task<\w+>)\s+(?:Issue|Post|Pay|Refund|Delete)\s*\(') {
    throw 'Safety stop: staged allocation journal escaped source-receipt uniqueness or money-posting boundary.'
}

# A contract-wide audit must discover completed invoice receipts that
# have NO journal entry, and detect claims made from another contract.
# Include soft-deleted Payment history. This remains an internal read-only
# diagnostic and must never reduce debt or allocate security deposits.
if ($paymentAllocationAudit -notmatch 'CompletedInvoicePaymentUnjournaled' -or
    $paymentAllocationAudit -notmatch 'CompletedInvoicePaymentUnattributed' -or
    $paymentAllocationAudit -notmatch 'CrossContractAllocationInLedger' -or
    $paymentAllocationReviewService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $paymentAllocationReviewService -notmatch '\[UnitOfWork\(isTransactional:\s*true\)\]' -or
    $paymentAllocationReviewService -notmatch '_softDeleteFilter\.Disable\(' -or
    $paymentAllocationReviewService -notmatch 'receiptIds\.Contains\(x\.PaymentId\)' -or
    $paymentAllocationReviewService -notmatch 'journalPaymentIds\.Contains\(x\.Id\)' -or
    $paymentAllocationReviewService -notmatch 'journal, receipts, contractId' -or
    $paymentAllocationReviewService -match '\.(Issue|Post|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: contract-wide invoice allocation review lost full receipt scope or nonposting boundary.'
}

# Two-person invoice issue reviews are STAGED administrative evidence,
# never invoice issuance permission or paid debt. Future service must
# re-resolve live Identity/building entitlements under DB locks.
if ($issuanceReviewTicket -notmatch 'CanIssue\s*=>\s*false' -or
    $issuanceReviewTicket -notmatch 'CanPostReceivable\s*=>\s*false' -or
    $issuanceReviewTicket -notmatch 'CanReduceDebt\s*=>\s*false' -or
    $issuanceReviewTicket -notmatch 'reviewerUserId == SubmittedByUserId' -or
    $issuanceReviewTicket -notmatch 'InvoiceIssuanceScopePolicy\.Assess\(' -or
    $issuanceReviewTicket -notmatch 'scope\.IsScopeConsistent' -or
    $issuanceReviewTicket -notmatch 'acceptForDesign && preflight\.Blockers\.Any' -or
    $issuanceReviewTicket -notmatch 'Invoice evidence anomalies must be resolved' -or
    $issuanceReviewTicket -notmatch 'PersistedEvidenceSha256' -or
    $issuanceReviewTicket -notmatch 'Status != InvoiceIssuanceReviewStatus\.Submitted' -or
    $issuanceReviewSchema -notmatch 'UX_BM_IssueReview_Evidence' -or
    $issuanceReviewSchema -notmatch 'UX_BM_IssueReview_Draft' -or
    $issuanceReviewSchema -notmatch 'CK_BM_IssueReview_IndependentReviewer' -or
    $issuanceReviewSchema -notmatch 'HasOne<FinalInvoiceIssueEvidenceRecord>' -or
    $issuanceReviewSchema -notmatch 'DeleteBehavior\.Restrict' -or
    $issueEvidenceDbContext -notmatch 'InvoiceIssuanceReviewTicketConfiguration' -or
    $issuanceReviewTicket -match '(?m)^\s*public\s+(?:async\s+)?(?:void|Task|Task<\w+>)\s+(?:Issue|Post|Pay|Refund|End|Delete)\s*\(') {
    throw 'Safety stop: staged two-person invoice review lost its audit or nonposting boundary.'
}

# Pending, failed or cancelled invoice receipts must never carry PaidAt.
# Preserve the anomaly in final invoice preflight; do not infer payment.
if ($paymentEvidenceModel -notmatch 'p\.Status != PaymentStatus\.Completed && p\.PaidAt\.HasValue' -or
    $paymentEvidenceModel -notmatch 'InvoicePaymentAttributionInvalid' -or
    $issuePreflight -notmatch 'Payment:' ) {
    throw 'Safety stop: final invoice payment completion timestamp provenance is unchecked.'
}

# Final invoice auxiliary charges and payment due date have NO approved
# default policy in the current SRS. An explicitly documented no-fee
# assessment is not an authorization and may not be silently synthesized.
# Damage compensation and deposit offsets have separate approval workflows.
if ($financialTermsReview -notmatch 'AuxiliaryFeeAssessmentNotProvided' -or
    $financialTermsReview -notmatch 'InvoiceDueDatePolicyNotProvided' -or
    $financialTermsReview -notmatch 'AuxiliaryFeeAssessmentDecision\.ExplicitlyNoCharges' -or
    $financialTermsReview -notmatch 'ProposedTermsDoNotMatchInvoiceEvidence' -or
    $financialTermsReview -notmatch 'DuplicateAuxiliaryServiceCharge' -or
    $financialTermsReview -notmatch 'AuxiliaryChargeMissingValidPricingEvidence' -or
    $financialTermsReview -notmatch 'FinancialTermsProposalIsNotPostingAuthorization' -or
    $financialTermsReview -notmatch 'CanIssue\s*=>\s*false' -or
    $financialTermsReview -notmatch 'IsReceivablePosted\s*=>\s*false' -or
    $financialTermsReview -notmatch 'CanSettleDeposit\s*=>\s*false' -or
    $issuePreflight -notmatch 'AuxiliaryFeePolicyUndecided' -or
    $issuePreflight -notmatch 'InvoiceDueDatePolicyUndecided') {
    throw 'Safety stop: proposed invoice financial terms bypassed fee, due-date or posting approval.'
}

# Consolidated final invoice issuance readiness remains read-only:
# it MUST inspect the live draft, archived evidence, independent review,
# stale evidence and contract-wide payment allocations. Policy blockers
# may not be inferred away; an audit result cannot become a permission.
if ($invoiceReleaseReadiness -notmatch 'StagedInvoiceEvidenceNoLongerCurrent' -or
    $invoiceReleaseReadiness -notmatch 'IndependentReviewApplicableToCurrentDraft\s*\{' -or
    $invoiceReleaseReadiness -notmatch 'IndependentInvoiceReviewSupersededByDraftChange' -or
    $invoiceReleaseReadiness -notmatch 'IndependentInvoiceReviewSupersededByNewAnomalies' -or
    $invoiceReleaseReadiness -notmatch 'IndependentInvoiceReviewSupersededByAllocationAnomalies' -or
    $invoiceReleaseReadiness -notmatch 'IndependentInvoiceReviewNotReady' -or
    $invoiceReleaseReadiness -notmatch 'FinalInvoiceFinancialTermsEvaluator\.Assess\(' -or
    $invoiceReleaseReadiness -notmatch 'proposal:\s*null' -or
    $invoiceReleaseReadiness -notmatch 'Allocations:' -or
    $invoiceReleaseReadiness -notmatch 'CanIssue\s*=>\s*false' -or
    $invoiceReleaseReadiness -notmatch 'CanPostReceivable\s*=>\s*false' -or
    $invoiceReleaseReadiness -notmatch 'CanSettleDeposit\s*=>\s*false' -or
    $invoiceReleaseService -notmatch '\[RemoteService\(IsEnabled\s*=\s*false\)\]' -or
    $invoiceReleaseService -notmatch '\[UnitOfWork\(isTransactional:\s*true\)\]' -or
    $invoiceReleaseService -notmatch '_preflight\.GetAsync\(' -or
    $invoiceReleaseService -notmatch '_drift\.GetAsync\(' -or
    $invoiceReleaseService -notmatch '_allocationLedger\.GetAsync\(' -or
    $invoiceReleaseService -match '\.(Issue|Post|Pay|Refund|End|Complete|InsertAsync|UpdateAsync|DeleteAsync)\s*\(') {
    throw 'Safety stop: consolidated invoice release readiness lost source audit, policy blockers or nonposting boundary.'
}

Write-Host 'MOVE-OUT LIFECYCLE SOURCE GATE PASS.'
Write-Host 'PASS: direct contract end is remote-disabled and denies before mutation.'
Write-Host 'PASS: completion readiness remains internal, read-only and financially blocked.'
Write-Host 'READ-ONLY SOURCE CHECK. This does NOT prove transaction safety, Identity authorization or DB roles.'
