using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Invoices;

/// <summary>
/// Proposed monthly financial terms: NOT an issuer decision, not a persisted
/// receivable, and never a permission grant. The caller must independently
/// verify all fees against authoritative contract/rate sources before posting.
/// </summary>
public sealed record MonthlyInvoiceFinancialTermsProposal(
    Guid ContractId,
    Guid RoomId,
    int BillingYear,
    int BillingMonth,
    DateOnly? PaymentDueDate,
    string? DueDatePolicyReference,
    AuxiliaryFeeAssessmentDecision AuxiliaryFeeDecision,
    string? AuxiliaryFeePolicyReference,
    Guid? AuxiliaryFeeReviewerId,
    IReadOnlyCollection<FinalInvoiceAuxiliaryChargeProposal>? AuxiliaryCharges);

public sealed class MonthlyInvoiceFinancialTermsReview
{
    public Guid ContractId { get; init; }
    public Guid RoomId { get; init; }
    public int BillingYear { get; init; }
    public int BillingMonth { get; init; }
    public decimal? VerifiedBaseAmount { get; internal set; }
    public decimal? ReviewedAuxiliaryAmount { get; internal set; }
    public decimal? ReviewedGrandTotal { get; internal set; }
    public DateOnly? DocumentedDueDate { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanIssue => false;
    public bool IsOfficialReceivable => false;
    public bool CanSettleDeposit => false;
}

/// <summary>
/// Pure monthly invoice math: source pricing and operator approval are STILL
/// required. Unlike final invoices, this never assumes a move-out or
/// chooses a mid-month proration policy.
/// </summary>
public static class MonthlyInvoiceFinancialTermsEvaluator
{
    private const string BasePreviewServiceDecisionBlocker =
        "OtherServiceFeesRequireReviewBeforeIssuance";

    public static MonthlyInvoiceFinancialTermsReview Assess(
        MonthlyInvoiceBasePreviewDto basePreview,
        MonthlyInvoiceFinancialTermsProposal? terms)
    {
        ArgumentNullException.ThrowIfNull(basePreview);
        var result = new MonthlyInvoiceFinancialTermsReview
        {
            ContractId = basePreview.ContractId,
            RoomId = basePreview.RoomId,
            BillingYear = basePreview.BillingYear,
            BillingMonth = basePreview.BillingMonth
        };
        if (basePreview.Blockers is null ||
            basePreview.Blockers.Any(x =>
                x != BasePreviewServiceDecisionBlocker) ||
            basePreview.ContractId == Guid.Empty ||
            basePreview.RoomId == Guid.Empty ||
            basePreview.BillingYear is < 1 or > 9999 ||
            basePreview.BillingMonth is < 1 or > 12 ||
            basePreview.KnownFullMonthRentAmount <= 0m ||
            decimal.Truncate(basePreview.KnownFullMonthRentAmount) !=
                basePreview.KnownFullMonthRentAmount ||
            basePreview.ProvisionalElectricityAmount is not decimal electricity ||
            basePreview.ProvisionalWaterAmount is not decimal water ||
            basePreview.ProvisionalRentAndUtilitiesAmount is not decimal baseAmount ||
            electricity < 0m || water < 0m ||
            decimal.Truncate(electricity) != electricity ||
            decimal.Truncate(water) != water ||
            !MatchesVerifiedBase(baseAmount,
                basePreview.KnownFullMonthRentAmount, electricity, water))
            result.Blockers.Add("VerifiedMonthlyRentAndUtilitiesRequired");
        else
            result.VerifiedBaseAmount = baseAmount;

        if (terms is null)
        {
            result.Blockers.Add("MonthlyFeeAssessmentNotProvided");
            result.Blockers.Add("MonthlyDueDatePolicyNotProvided");
            return Stop(result);
        }

        if (terms.ContractId != basePreview.ContractId ||
            terms.RoomId != basePreview.RoomId ||
            terms.BillingYear != basePreview.BillingYear ||
            terms.BillingMonth != basePreview.BillingMonth)
            result.Blockers.Add("MonthlyTermsScopeMismatch");

        if (!terms.PaymentDueDate.HasValue ||
            terms.PaymentDueDate == default ||
            !HasReference(terms.DueDatePolicyReference, 8, 200))
            result.Blockers.Add("MonthlyDueDatePolicyNotProvided");
        else
            result.DocumentedDueDate = terms.PaymentDueDate;

        if (terms.AuxiliaryFeeDecision is not
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges and
                not AuxiliaryFeeAssessmentDecision.ItemizedCharges ||
            !HasReference(terms.AuxiliaryFeePolicyReference, 8, 200) ||
            terms.AuxiliaryFeeReviewerId.GetValueOrDefault() == Guid.Empty ||
            terms.AuxiliaryCharges is null)
        {
            result.Blockers.Add("MonthlyFeeAssessmentNotProvided");
            return Stop(result);
        }

        var charges = terms.AuxiliaryCharges.ToArray();
        if (terms.AuxiliaryFeeDecision ==
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges)
        {
            if (charges.Length != 0)
                result.Blockers.Add("ExplicitNoFeeDecisionHasChargeLines");
            else
                result.ReviewedAuxiliaryAmount = 0m;
        }
        else if (charges.Length == 0)
        {
            result.Blockers.Add("ItemizedMonthlyFeesMissing");
        }
        else
        {
            if (charges.Any(x => !ValidServiceLine(x)))
                result.Blockers.Add("MonthlyServiceLineInvalid");
            // Invalid/null service codes must never crash the duplicate
            // check or be treated as a valid fee approval.
            if (!result.Blockers.Contains("MonthlyServiceLineInvalid") &&
                charges.GroupBy(x =>
                    (x.Kind, x.ServiceCode.Trim().ToUpperInvariant()))
                    .Any(g => g.Count() > 1))
                result.Blockers.Add("DuplicateMonthlyServiceCharge");
            if (!result.Blockers.Any(x => x is
                    "MonthlyServiceLineInvalid" or
                    "DuplicateMonthlyServiceCharge"))
            {
                try
                {
                    result.ReviewedAuxiliaryAmount = charges.Sum(x => x.Amount);
                }
                catch (OverflowException)
                {
                    result.Blockers.Add("MonthlyFeeTotalOverflow");
                }
            }
        }

        return Stop(result);
    }

    private static bool MatchesVerifiedBase(
        decimal actual, decimal monthlyRent, decimal electricity, decimal water)
    {
        try
        {
            return actual == checked(monthlyRent + electricity + water);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidServiceLine(FinalInvoiceAuxiliaryChargeProposal? line)
    {
        if (line is null || !Enum.IsDefined(line.Kind) ||
            !HasReference(line.ServiceCode, 1, 64) ||
            !HasReference(line.ContractOrRateReference, 8, 200) ||
            !HasReference(line.Description, 1, 500) ||
            line.Quantity <= 0m || line.UnitPrice <= 0m ||
            line.Amount <= 0m ||
            decimal.Truncate(line.Amount) != line.Amount)
            return false;
        try
        {
            return line.Quantity * line.UnitPrice == line.Amount;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool HasReference(string? value, int min, int max)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Trim().Length >= min && value.Trim().Length <= max;

    private static MonthlyInvoiceFinancialTermsReview Stop(
        MonthlyInvoiceFinancialTermsReview result)
    {
        if (result.Blockers.Count == 0 &&
            result.VerifiedBaseAmount is decimal baseAmount &&
            result.ReviewedAuxiliaryAmount is decimal auxiliary)
        {
            try
            {
                result.ReviewedGrandTotal = baseAmount + auxiliary;
            }
            catch (OverflowException)
            {
                result.Blockers.Add("MonthlyInvoiceTotalOverflow");
            }
        }

        // No authenticated approval, no immutable issued invoice, no posted
        // receivable and no permitted payment allocation exists here.
        result.Blockers.Add("MonthlyInvoiceIssuanceNotAuthorized");
        return result;
    }
}
