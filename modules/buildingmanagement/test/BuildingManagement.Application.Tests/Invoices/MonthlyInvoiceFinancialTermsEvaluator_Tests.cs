using System;
using System.Collections.Generic;
using BuildingManagement.Invoices;
using Xunit;

namespace BuildingManagement.Invoices;

public sealed class MonthlyInvoiceFinancialTermsEvaluator_Tests
{
    [Fact]
    public void MONTHLYFIN01_Missing_Fee_Review_Cannot_Pretend_Zero_Charges()
    {
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(Base(), null);
        Assert.Null(result.ReviewedGrandTotal);
        Assert.Contains("MonthlyFeeAssessmentNotProvided", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void MONTHLYFIN02_Explicit_No_Fee_Decision_Still_Cannot_Issue()
    {
        var basis = Base();
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis));

        Assert.Equal(3_050_000m, result.VerifiedBaseAmount);
        Assert.Equal(0m, result.ReviewedAuxiliaryAmount);
        Assert.Equal(3_050_000m, result.ReviewedGrandTotal);
        Assert.Equal(new DateOnly(2026, 11, 5), result.DocumentedDueDate);
        Assert.Equal(new[] { "MonthlyInvoiceIssuanceNotAuthorized" },
            result.Blockers);
        Assert.False(result.IsOfficialReceivable);
        Assert.False(result.CanSettleDeposit);
    }

    [Fact]
    public void MONTHLYFIN03_Itemized_Service_References_Can_Be_Reviewed()
    {
        var basis = Base();
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with
            {
                AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
                AuxiliaryCharges = new[]
                {
                    Fee(FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET", 100_000m),
                    Fee(FinalInvoiceAuxiliaryFeeKind.Trash, "TRASH", 20_000m)
                }
            });

        Assert.Equal(120_000m, result.ReviewedAuxiliaryAmount);
        Assert.Equal(3_170_000m, result.ReviewedGrandTotal);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void MONTHLYFIN04_Tampered_Utility_Base_Blocks_Grand_Total()
    {
        var basis = Base();
        basis.ProvisionalRentAndUtilitiesAmount = 5_000_000m;
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis));
        Assert.Contains("VerifiedMonthlyRentAndUtilitiesRequired", result.Blockers);
        Assert.Null(result.ReviewedGrandTotal);
    }

    [Fact]
    public void MONTHLYFIN05_Unknown_Billing_Policy_Does_Not_Grant_Due_Date()
    {
        var basis = Base();
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with { DueDatePolicyReference = null });
        Assert.Contains("MonthlyDueDatePolicyNotProvided", result.Blockers);
        Assert.Null(result.ReviewedGrandTotal);
    }

    [Fact]
    public void MONTHLYFIN06_Duplicate_Service_And_Amount_Mismatch_Refused()
    {
        var basis = Base();
        var line = Fee(FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET", 100_000m);
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with
            {
                AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
                AuxiliaryCharges = new[] { line, line }
            });
        Assert.Contains("DuplicateMonthlyServiceCharge", result.Blockers);
        Assert.Null(result.ReviewedGrandTotal);

        var mismatch = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with
            {
                AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
                AuxiliaryCharges = new[] { line with { Amount = 100_001m } }
            });
        Assert.Contains("MonthlyServiceLineInvalid", mismatch.Blockers);
        Assert.Null(mismatch.ReviewedGrandTotal);
    }

    [Fact]
    public void MONTHLYFIN07_Foreign_Contract_Or_Period_Does_Not_Borrow_Fees()
    {
        var basis = Base();
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with { ContractId = Guid.NewGuid() });
        Assert.Contains("MonthlyTermsScopeMismatch", result.Blockers);
        Assert.Null(result.ReviewedGrandTotal);
    }

    [Fact]
    public void MONTHLYFIN09_Null_Service_Code_And_Amount_Overflow_Fail_Closed()
    {
        var basis = Base();
        var invalid = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with
            {
                AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
                AuxiliaryCharges = new[]
                {
                    Fee(FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET", 100m)
                        with { ServiceCode = null! }
                }
            });
        Assert.Contains("MonthlyServiceLineInvalid", invalid.Blockers);
        Assert.Null(invalid.ReviewedGrandTotal);

        basis.KnownFullMonthRentAmount = decimal.MaxValue;
        basis.ProvisionalElectricityAmount = 1m;
        basis.ProvisionalWaterAmount = 1m;
        basis.ProvisionalRentAndUtilitiesAmount = decimal.MaxValue;
        var overflow = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis));
        Assert.Contains("VerifiedMonthlyRentAndUtilitiesRequired", overflow.Blockers);
        Assert.Null(overflow.ReviewedGrandTotal);
    }

    [Fact]
    public void MONTHLYFIN08_Service_Fee_Cannot_Represent_Damage_Deduction()
    {
        var basis = Base();
        var result = MonthlyInvoiceFinancialTermsEvaluator.Assess(
            basis, Terms(basis) with
            {
                AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
                AuxiliaryCharges = new[]
                {
                    Fee((FinalInvoiceAuxiliaryFeeKind)99, "DAMAGE", 200_000m)
                }
            });
        Assert.Contains("MonthlyServiceLineInvalid", result.Blockers);
        Assert.Null(result.ReviewedGrandTotal);
        Assert.False(result.CanSettleDeposit);
    }

    private static MonthlyInvoiceBasePreviewDto Base()
        => new()
        {
            ContractId = Guid.NewGuid(),
            RoomId = Guid.NewGuid(),
            BillingYear = 2026,
            BillingMonth = 10,
            KnownFullMonthRentAmount = 3_000_000m,
            ProvisionalElectricityAmount = 30_000m,
            ProvisionalWaterAmount = 20_000m,
            ProvisionalRentAndUtilitiesAmount = 3_050_000m,
            Blockers = new List<string> { "OtherServiceFeesRequireReviewBeforeIssuance" }
        };

    private static MonthlyInvoiceFinancialTermsProposal Terms(
        MonthlyInvoiceBasePreviewDto basis)
        => new(basis.ContractId, basis.RoomId, basis.BillingYear,
            basis.BillingMonth, new DateOnly(2026, 11, 5),
            "APPROVED-MONTHLY-DUE", AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "APPROVED-MONTHLY-FEES", Guid.NewGuid(),
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>());

    private static FinalInvoiceAuxiliaryChargeProposal Fee(
        FinalInvoiceAuxiliaryFeeKind kind, string code, decimal amount)
        => new(kind, code, 1m, amount, amount,
            "CONTRACT-SERVICE-PRICE", code + " service");
}
