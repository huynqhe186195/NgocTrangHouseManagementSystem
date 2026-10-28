using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Issue preparation only. Copies the known, approved full-month rent and
/// metered utility evidence into three stable line previews, while reporting
/// unresolved policy, authorization and posted-ledger requirements.
/// Never a payable invoice, posting instruction or reusable authorization.
/// </summary>
public sealed class FinalInvoiceIssuePreflight
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public Guid RoomId { get; init; }
    public Guid? UtilityDraftId { get; internal set; }
    public string? EvidenceSha256 { get; internal set; }
    public decimal? KnownBaseChargeTotal { get; internal set; }
    public IReadOnlyList<FinalInvoiceBaseCharge> BaseCharges { get; internal set; }
        = Array.Empty<FinalInvoiceBaseCharge>();
    public List<string> Blockers { get; } = new();

    // No policy flag or caller-supplied approval can turn this PREVIEW into
    // a financial mutation. Issuing requires a new authorized transaction.
    public bool CanIssue => false;
    public bool IsReceivablePosted => false;
    public bool IsDebtCalculated => false;

    /// <summary>
    /// Rebind a freshly captured preflight to a VERIFIED historical evidence
    /// digest. CapturedAtUtc changes with every fresh read; only the drift
    /// and original-source auditors can establish the CURRENT draft still
    /// matches that historical digest. This method does NOT approve money.
    /// </summary>
    public FinalInvoiceIssuePreflight AnchorToVerifiedArchivedEvidence(
        FinalInvoiceIssueEvidenceRecord archived)
    {
        ArgumentNullException.ThrowIfNull(archived);
        archived.VerifyStoredEvidence();
        if (MoveOutRequestId != archived.MoveOutRequestId ||
            ContractId != archived.ContractId ||
            RoomId != archived.RoomId ||
            UtilityDraftId != archived.DraftInvoiceId ||
            KnownBaseChargeTotal != archived.KnownBaseChargeTotal ||
            BaseCharges.Count != 3 ||
            BaseCharges.Sum(x => x.Amount) != archived.KnownBaseChargeTotal)
            throw new InvalidOperationException(
                "The current invoice preflight does not match archived evidence scope or base amount.");

        var anchor = new FinalInvoiceIssuePreflight
        {
            MoveOutRequestId = MoveOutRequestId,
            ContractId = ContractId,
            RoomId = RoomId,
            UtilityDraftId = UtilityDraftId,
            KnownBaseChargeTotal = KnownBaseChargeTotal,
            BaseCharges = BaseCharges,
            EvidenceSha256 = archived.EvidenceSha256
        };
        anchor.Blockers.AddRange(Blockers);
        return anchor;
    }
}

public enum FinalInvoiceBaseChargeType
{
    FullMonthRent = 1,
    Electricity = 2,
    Water = 3
}

public sealed record FinalInvoiceBaseCharge(
    FinalInvoiceBaseChargeType Type, decimal Amount,
    Guid? SourceRateId, decimal? Quantity, decimal? UnitPrice);

public static class FinalInvoiceIssuePreflightEvaluator
{
    private static readonly string[] PaymentAnomalies =
    {
        "InvoicePaymentAttributionInvalid",
        "DuplicatePaymentIdentifier",
        "DuplicateTransactionReference",
        "UnallocatedInvoicePayment",
        "CompletedPaymentLinkedToUnissuedDraft"
    };

    public static FinalInvoiceIssuePreflight Assess(
        FinalMoveOutBillReview bill,
        FinalInvoiceEvidencePackage? package,
        FinalMoveOutPaymentEvidence paymentEvidence)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(paymentEvidence);

        var result = new FinalInvoiceIssuePreflight
        {
            MoveOutRequestId = bill.MoveOutRequestId,
            ContractId = bill.ContractId,
            RoomId = bill.RoomId,
            UtilityDraftId = bill.UtilityDraftId
        };

        // Capture actionable persisted-source failures. The three expected
        // financial blockers are replaced with more detailed reasons below.
        foreach (var blocker in bill.Blockers.Where(x =>
            x is not "FinalInvoiceNotIssued" and
                not "OutstandingDebtNotReconciled" and
                not "DepositSettlementNotVerified"))
            result.Blockers.Add("Bill:" + blocker);

        if (paymentEvidence.MoveOutRequestId != bill.MoveOutRequestId ||
            paymentEvidence.ContractId != bill.ContractId ||
            paymentEvidence.DraftInvoiceId != bill.UtilityDraftId)
            result.Blockers.Add("PaymentReviewScopeMismatch");

        foreach (var anomaly in PaymentAnomalies.Where(x =>
            paymentEvidence.Blockers.Contains(x)))
            result.Blockers.Add("Payment:" + anomaly);

        if (package is null)
        {
            result.Blockers.Add("VerifiedFinalInvoiceEvidenceMissing");
        }
        else if (!package.VerifyChecksum() ||
                 package.MoveOutRequestId != bill.MoveOutRequestId ||
                 package.ContractId != bill.ContractId ||
                 package.RoomId != bill.RoomId ||
                 package.DraftInvoiceId != bill.UtilityDraftId ||
                 bill.ProvisionalRentAndUtilities is not decimal quoted ||
                 bill.FullMonthRent is not decimal rent ||
                 quoted != package.KnownRentAndUtilities ||
                 rent != package.FullMonthRent ||
                 bill.BillingYear != package.FinalBillingDate.Year ||
                 bill.BillingMonth != package.FinalBillingDate.Month ||
                 package.Utilities.Count != 2 ||
                 !package.Utilities.Any(x => x.UtilityType == UtilityType.Electricity) ||
                 !package.Utilities.Any(x => x.UtilityType == UtilityType.Water) ||
                 package.Utilities.Select(x => x.UtilityType).Distinct().Count() != 2 ||
                 package.Utilities.Any(x => x.Amount != x.Quantity * x.UnitPrice ||
                                            x.Sources.Count == 0) ||
                 rent <= 0m ||
                 package.Utilities.Sum(x => x.Amount) + rent != quoted)
        {
            result.Blockers.Add("VerifiedEvidenceMismatch");
        }
        else
        {
            var electricity = package.Utilities.Single(x =>
                x.UtilityType == UtilityType.Electricity);
            var water = package.Utilities.Single(x =>
                x.UtilityType == UtilityType.Water);
            result.BaseCharges = Array.AsReadOnly(new[]
            {
                new FinalInvoiceBaseCharge(
                    FinalInvoiceBaseChargeType.FullMonthRent, rent,
                    null, null, null),
                new FinalInvoiceBaseCharge(
                    FinalInvoiceBaseChargeType.Electricity, electricity.Amount,
                    electricity.UtilityRateId,
                    electricity.Quantity, electricity.UnitPrice),
                new FinalInvoiceBaseCharge(
                    FinalInvoiceBaseChargeType.Water, water.Amount,
                    water.UtilityRateId, water.Quantity, water.UnitPrice)
            });
            result.KnownBaseChargeTotal = quoted;
            result.EvidenceSha256 = package.EvidenceSha256;
        }

        // The approved business specification does not yet define service
        // charges, due-date policy, invoice signing authority, immutable
        // issued ledger or audited allocations. Never assume zero fees,
        // derive a due date, or infer approval from this report.
        result.Blockers.Add("AuxiliaryFeePolicyUndecided");
        result.Blockers.Add("InvoiceDueDatePolicyUndecided");
        result.Blockers.Add("IssuerBuildingAuthorizationUnavailable");
        result.Blockers.Add("ImmutableIssuedInvoiceLedgerNotImplemented");
        result.Blockers.Add("AuditedPaymentAllocationNotImplemented");
        return result;
    }
}
