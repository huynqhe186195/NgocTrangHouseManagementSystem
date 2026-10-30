using System;
using System.IO;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class MonthlyInvoiceIssuance_Tests
{
    private static readonly DateTime PreparedAt =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MONTISS01_Independent_Approved_Review_Can_Issue_Staging_Bill()
    {
        var archive = Archive();
        var reviewer = Guid.NewGuid();
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, reviewer, PreparedAt.AddHours(1),
            true, "Original rates and physical readings checked");
        var issuer = archive.ReviewedByUserId;
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202610-001", issuer,
            PreparedAt.AddHours(2), archive, review);

        issued.VerifyAgainst(archive, review);
        Assert.Equal(3_050_000m, issued.TotalAmount);
        Assert.Equal(archive.DueDate, issued.DueDate);
        Assert.Equal(archive.FrozenEvidenceSha256, issued.CandidateSha256);
        Assert.True(issued.IsStagingIssued);
        Assert.False(issued.IsOfficialReceivable);
        Assert.False(issued.HasIndependentlyVerifiedBankReceipt);
    }

    [Fact]
    public void MONTISS02_Self_Review_Fails_Closed()
    {
        var archive = Archive();
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, archive.ReviewedByUserId,
                PreparedAt.AddHours(1), true, "Not independent"));
    }

    [Fact]
    public void MONTISS03_Rejected_Review_Cannot_Issue()
    {
        var archive = Archive();
        var review = Review(archive, false);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-002", archive.ReviewedByUserId,
                PreparedAt.AddHours(2), archive, review));
    }

    [Fact]
    public void MONTISS04_Issuer_Cannot_Be_Independent_Reviewer()
    {
        var archive = Archive();
        var review = Review(archive);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-003", review.ReviewedByUserId,
                PreparedAt.AddHours(2), archive, review));
    }

    [Fact]
    public void MONTISS05_Changed_Archive_Or_Review_Is_Rejected()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-202610-004", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        typeof(MonthlyInvoiceIndependentReviewRecord)
            .GetProperty(nameof(MonthlyInvoiceIndependentReviewRecord.CandidateSha256))!
            .SetValue(review, new string('a', 64));

        Assert.Throws<InvalidDataException>(() =>
            issued.VerifyAgainst(archive, review));
    }

    [Fact]
    public void MONTISS06_Due_Date_Must_Be_On_Or_After_Issue_Date()
    {
        var archive = Archive();
        var review = Review(archive);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-005", archive.ReviewedByUserId,
                new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc),
                archive, review));
    }

    [Fact]
    public void MONTISS07_Review_Must_Follow_Preparation_And_Have_Reason()
    {
        var archive = Archive();
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                PreparedAt.AddSeconds(-1), true, "Source checked"));
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                PreparedAt.AddHours(1), true, "short"));
    }

    [Fact]
    public void MONTISS08_UTC_Time_Roundtrips_Through_PostgreSQL_Precision()
    {
        var archive = Archive();
        var reviewer = Guid.NewGuid();
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, reviewer,
            PreparedAt.AddHours(1).AddTicks(9), true,
            "Independent line item and rate approval");
        Assert.Equal(0L, review.ReviewedAtUtc.Ticks % 10L);
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202610-008", archive.ReviewedByUserId,
            PreparedAt.AddHours(2).AddTicks(7), archive, review);
        Assert.Equal(0L, issued.IssuedAtUtc.Ticks % 10L);
        issued.VerifyAgainst(archive, review);
    }

    private static MonthlyInvoiceIndependentReviewRecord Review(
        MonthlyInvoiceCandidateLedgerRecord archive, bool accepted = true)
        => MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, Guid.NewGuid(),
            PreparedAt.AddHours(1), accepted,
            "Reviewed source rates and contractual fee decisions");

    private static MonthlyInvoiceCandidateLedgerRecord Archive()
    {
        var lines = new[]
        {
            Line(UtilityType.Electricity, 10m, 3_500m, 100m),
            Line(UtilityType.Water, 1m, 15_000m, 40m)
        };
        var candidate = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), PreparedAt,
            new DateOnly(2026, 11, 5), "APPROVED-MONTHLY-DUE-001",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "EXPLICIT-NO-AUXILIARY-SERVICES",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, lines);
        return MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), candidate);
    }

    private static MonthlyInvoiceCandidateUtilityLine Line(
        UtilityType kind, decimal quantity, decimal price, decimal previous)
        => new(kind, Guid.NewGuid(), quantity, price, quantity * price,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, previous + quantity) });
}
