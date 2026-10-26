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
    $meterSignoff -match '\.(End|Complete|Issue|Refund|PostDebt|DeleteAsync)\s*\(') {
    throw 'Safety stop: verified final-meter checklist lost transaction, scope or evidence gate.'
}

Write-Host 'MOVE-OUT LIFECYCLE SOURCE GATE PASS.'
Write-Host 'PASS: direct contract end is remote-disabled and denies before mutation.'
Write-Host 'PASS: completion readiness remains internal, read-only and financially blocked.'
Write-Host 'READ-ONLY SOURCE CHECK. This does NOT prove transaction safety, Identity authorization or DB roles.'
