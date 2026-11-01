using System;
using System.IO;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositDebtReview_Tests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid RequestId = Guid.NewGuid();
    private static readonly Guid BuildingId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();
    private static readonly DateTime Now =
        new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
    private static ContractDepositOffsetClaim Claim(decimal amount,
        bool disputed = false) =>
        new(Guid.NewGuid(), ContractDepositOffsetCategory.UnpaidRent,
            amount, true, disputed, "SIGNED-OFFICIAL-INVOICE-RECEIVABLE-001");
    private static ContractDepositDebtReviewRecord Review(
        params ContractDepositOffsetClaim[] claims) =>
        ContractDepositDebtReviewRecord.Capture(Guid.NewGuid(),
            ContractId, RequestId, BuildingId, ReviewerId,
            new DateOnly(2026, 11, 1), Now, 2_000_000m,
            claims, "SIGNED-INDEPENDENT-RECONCILIATION-2026-001");

    [Fact]
    public void DEP_REVIEW01_Current_Claim_And_Held_Funds_Verify()
    {
        var claims = new[] { Claim(1_000_000m) };
        var record = Review(claims);
        record.RequireCurrentEvidence(ContractId, RequestId, BuildingId,
            2_000_000m, claims);
        Assert.Equal(1_000_000m, record.VerifiedOutstandingAmount);
        Assert.False(record.CanPostMoney);
    }

    [Fact]
    public void DEP_REVIEW02_Disputed_Or_Missing_Claim_Denied()
    {
        Assert.Throws<InvalidOperationException>(() => Review());
        Assert.Throws<InvalidOperationException>(() =>
            Review(Claim(1_000_000m, true)));
    }

    [Fact]
    public void DEP_REVIEW03_Balance_Drift_Invalidates_Independent_Attestation()
    {
        var claims = new[] { Claim(1_000_000m) };
        var record = Review(claims);
        Assert.Throws<InvalidOperationException>(() =>
            record.RequireCurrentEvidence(ContractId, RequestId, BuildingId,
                1_000_000m, claims));
        Assert.Throws<InvalidOperationException>(() =>
            record.RequireCurrentEvidence(ContractId, RequestId, BuildingId,
                2_000_000m, new[] { claims[0] with {
                    OutstandingVnd = 900_000m } }));
    }

    [Fact]
    public void DEP_REVIEW04_Stored_Snapshot_Index_Tamper_Detected()
    {
        var row = Review(Claim(1_000_000m));
        var prop = typeof(ContractDepositDebtReviewRecord)
            .GetProperty(nameof(ContractDepositDebtReviewRecord.VerifiedHeldAmount))!;
        prop.GetSetMethod(true)!.Invoke(row, new object[] { 2_000_001m });
        Assert.Throws<InvalidDataException>(row.VerifyStoredReview);
    }
}
