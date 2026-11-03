using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeMidMonthFinancialReconciliationPacket_Tests
{
    private static readonly DateTime Created =
        new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string PricingReference =
        "SIGNED-TRANSFER-DAY-TARGET-AND-VND-ROUNDING-2026";
    private const string DepositPolicyReference =
        "OWNER-SIGNED-NEW-CONTRACT-DEPOSIT-RULE-2026";

    private sealed record Evidence(
        Contract Source,
        RoomChangeRequest Request,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        Guid TargetContractId,
        RoomChangeDailyRentQuote Quote,
        RoomChangeSourceRentSnapshot SourceRent,
        RoomChangeDepositDifferenceFacts DepositFacts);

    private static Evidence Fixture(
        decimal targetRent = 3_000_000m,
        decimal sourceReceipts = 0m,
        decimal previousSourceCorrection = 0m,
        decimal approvedDeductions = 0m,
        bool accepted = true,
        RoomChangePricingTreatment pricing =
            RoomChangePricingTreatment.AgreedDayProration,
        RoomChangeDepositTreatment? depositLane = null)
    {
        var source = new Contract(Guid.NewGuid(),
            "SOURCE-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(),
            new DateTime(2026, 8, 1), null, 3_000_000m);
        source.Activate();

        var request = RoomChangeRequest.Submit(
            Guid.NewGuid(), source.Id, source.RoomId, Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 9, 15),
            "Signed request to transfer every current tenant",
            Guid.NewGuid(), Created,
            ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);

        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, new[]
            {
                new RoomChangeRosterMember(
                    Guid.NewGuid(), ContractTenantRole.PrimaryTenant),
                new RoomChangeRosterMember(
                    Guid.NewGuid(), ContractTenantRole.Member)
            }, Guid.NewGuid(), Created.AddMinutes(1));

        var targetId = Guid.NewGuid();
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            request.RequestedDate, source.MonthlyRent, targetRent,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            PricingReference);

        var sourceRent = new RoomChangeSourceRentSnapshot(
            source.Id, 2026, 9, Guid.NewGuid(), 3_000_000m,
            previousSourceCorrection, sourceReceipts,
            new string('f', 64),
            sourceReceipts > 0m
                ? "INDEPENDENT-SOURCE-RENT-RECEIPT-2026" : null);

        var deposit = new RoomChangeDepositDifferenceFacts(
            source.Id, targetId, 3_000_000m, approvedDeductions,
            targetRent, "ACTUAL-RECEIPTS-REVIEWED-HELD-CASH-2026",
            DepositPolicyReference,
            approvedDeductions > 0m ? "OWNER-SIGNED-POSTED-OFFSET-2026" : null,
            HasUnresolvedDispute: false,
            HasEarlierRefundOrCarryForward: false);

        var choice = depositLane ??
            RoomChangeDepositDifferencePreviewCalculator
                .Calculate(deposit).ProposedPolicyLane;
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            pricing, choice, RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-NEW-LINKED-CONTRACT-2026",
            PricingReference,
            DepositPolicyReference,
            "SIGNED-CONSENT-OF-ALL-EXISTING-TENANTS-2026",
            Guid.NewGuid(), Created.AddMinutes(2));

        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster,
            Guid.NewGuid(), Created.AddMinutes(3), accepted,
            "INDEPENDENT-ROOT-POLICY-REVIEW-2026",
            "Reviewed and bound to the specific transfer proposal");

        return new Evidence(source, request, roster, policy, review,
            targetId, quote, sourceRent, deposit);
    }

    private static RoomChangeMidMonthFinancialReconciliationPacket Prepare(
        Evidence f, Guid? packetId = null,
        Guid? target = null, RoomChangeDailyRentQuote? quote = null,
        RoomChangeSourceRentSnapshot? rent = null,
        RoomChangeDepositDifferenceFacts? deposit = null,
        DateTime? at = null)
        => RoomChangeMidMonthFinancialReconciliationPacketFactory.Prepare(
            packetId ?? Guid.NewGuid(), f.Request, f.Source,
            target ?? f.TargetContractId, f.Roster, f.Policy,
            f.Review, quote ?? f.Quote, rent ?? f.SourceRent,
            deposit ?? f.DepositFacts, Guid.NewGuid(),
            at ?? Created.AddMinutes(5));

    [Fact]
    public void TRF_PACKET01_Binds_Same_Request_Reviewed_Policy_And_All_Tenants()
    {
        var f = Fixture();
        var p = Prepare(f);
        p.VerifyFingerprint();
        p.VerifyAgainst(f.Request, f.Source, f.Roster, f.Policy, f.Review);
        Assert.Equal(f.Request.Id, p.RequestId);
        Assert.Equal(f.Roster.RosterSha256, p.RosterSha256);
        Assert.Equal(f.Policy.EvidenceSha256, p.PolicySha256);
        Assert.Equal(f.Review.Id, p.ReviewId);
        Assert.Equal(64, p.EvidenceSha256.Length);
        Assert.Equal(1_400_000m, p.Rent.ProposedSourceRentVnd);
        Assert.Equal(1_600_000m, p.Rent.ProposedTargetRentVnd);
        Assert.Equal(3_000_000m, p.Deposit.ProposedCarryForwardVnd);
        Assert.False(p.CanExecuteRoomChange);
        Assert.False(p.IsOfficialInvoicePosting);
        Assert.False(p.IsCompletedBankTransfer);
        Assert.False(p.IsIndependentlyCashVerified);
    }

    [Fact]
    public void TRF_PACKET02_Higher_Target_Deposit_Needs_Unpaid_Topup()
    {
        var p = Prepare(Fixture(targetRent: 4_000_000m));
        Assert.Equal(1_000_000m, p.Deposit.UnpaidTopUpRequirementVnd);
        Assert.Equal(0m, p.Deposit.UnpaidRefundCandidateVnd);
        Assert.Equal(2_133_333m, p.Rent.ProposedTargetRentVnd);
        Assert.Equal(RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
            p.Deposit.ProposedPolicyLane);
        p.VerifyFingerprint();
    }

    [Fact]
    public void TRF_PACKET03_Lower_Target_Deposit_Yields_Unpaid_Refund_Candidate()
    {
        var p = Prepare(Fixture(targetRent: 2_000_000m));
        Assert.Equal(2_000_000m, p.Deposit.ProposedCarryForwardVnd);
        Assert.Equal(1_000_000m, p.Deposit.UnpaidRefundCandidateVnd);
        Assert.Equal(0m, p.Deposit.UnpaidTopUpRequirementVnd);
        Assert.False(p.IsCompletedBankTransfer);
        p.VerifyFingerprint();
    }

    [Fact]
    public void TRF_PACKET04_Previously_Posted_Approved_Offset_Splits_Actual_Held()
    {
        var p = Prepare(Fixture(targetRent: 2_000_000m,
            approvedDeductions: 250_000m));
        Assert.Equal(2_750_000m, p.Deposit.SourceAvailableToDispositionVnd);
        Assert.Equal(750_000m, p.Deposit.UnpaidRefundCandidateVnd);
        Assert.Equal(2_000_000m, p.Deposit.ProposedCarryForwardVnd);
        p.VerifyFingerprint();
    }

    [Fact]
    public void TRF_PACKET05_Excess_Existing_Rent_Receipt_Is_Separate_Review()
    {
        var p = Prepare(Fixture(sourceReceipts: 2_000_000m));
        Assert.Equal(-1_600_000m, p.Rent.ProposedSourceRentCorrectionVnd);
        Assert.Equal(600_000m, p.Rent.PotentialSourceOvercollectionVnd);
        Assert.True(p.Rent.RequiresSeparateOvercollectionReview);
        Assert.True(p.Rent.RequiresAppendOnlyIssuedInvoiceCorrection);
        Assert.False(p.IsOfficialInvoicePosting);
    }

    [Fact]
    public void TRF_PACKET06_Existing_Approved_Rent_Delta_Is_Not_Double_Counted()
    {
        var p = Prepare(Fixture(sourceReceipts: 1_000_000m,
            previousSourceCorrection: -500_000m));
        Assert.Equal(2_500_000m, p.Rent.PreviouslyRecognizedSourceRentVnd);
        Assert.Equal(-1_100_000m, p.Rent.ProposedSourceRentCorrectionVnd);
        Assert.Equal(400_000m, p.Rent.RemainingProposedSourceRentVnd);
    }

    [Fact]
    public void TRF_PACKET07_Foreign_Rent_Contract_Or_Month_Fails()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            rent: f.SourceRent with { SourceContractId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            rent: f.SourceRent with { BillingMonth = 10 }));
    }

    [Fact]
    public void TRF_PACKET08_Requested_Transfer_Date_Must_Match_Quote()
    {
        var f = Fixture();
        var wrong = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(2026, 9, 16), 3_000_000m, 3_000_000m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            PricingReference);
        Assert.Throws<InvalidOperationException>(() =>
            Prepare(f, quote: wrong));
    }

    [Fact]
    public void TRF_PACKET09_Source_Contract_Must_Be_Active_And_Match_Quote()
    {
        var f = Fixture();
        f.Source.End(Created.AddMinutes(4), ContractEndReason.RoomChanged);
        Assert.Throws<InvalidOperationException>(() => Prepare(f));
        f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            Prepare(f, quote: f.Quote with { SourceFullMonthlyRentVnd = 1m }));
    }

    [Fact]
    public void TRF_PACKET10_Topup_Plan_Must_Not_Be_Laundered_As_NoDifference()
    {
        var f = Fixture(targetRent: 4_000_000m,
            depositLane: RoomChangeDepositTreatment.CarryAllWithNoDifference);
        Assert.Throws<InvalidOperationException>(() => Prepare(f));
    }

    [Fact]
    public void TRF_PACKET11_Independent_Review_Rejection_Does_Not_Authorize_Plan()
    {
        var f = Fixture(accepted: false);
        Assert.Throws<InvalidOperationException>(() => Prepare(f));
    }

    [Fact]
    public void TRF_PACKET12_FullMonth_Or_Subset_Choices_Are_Not_MidMonth_Lanes()
    {
        var f = Fixture(pricing:
            RoomChangePricingTreatment.FullOldMonthNewNextMonth);
        Assert.Throws<InvalidOperationException>(() => Prepare(f));
    }

    [Fact]
    public void TRF_PACKET13_Cross_Contract_Deposit_Or_Amount_Mismatch_Is_Denied()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { SourceContractId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { TargetContractId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { SourceCashBackedHeldDepositVnd = 1m }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { TargetContractRequiredDepositVnd = 1m }));
    }

    [Fact]
    public void TRF_PACKET14_No_Unverified_Receipt_Or_Open_Dispute_May_Enter_Packet()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            rent: f.SourceRent with {
                IssuedMonthlyInvoiceId = null,
                ReviewedRentReceiptsVnd = 1_000_000m
            }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { HasUnresolvedDispute = true }));
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            deposit: f.DepositFacts with { HasEarlierRefundOrCarryForward = true }));
    }

    [Fact]
    public void TRF_PACKET15_Tampered_Source_Rent_And_Carry_Detected_By_Packet_Hash()
    {
        var p = Prepare(Fixture());
        Assert.Throws<InvalidDataException>(() =>
            (p with { Rent = p.Rent with { ProposedSourceRentVnd = 1m } })
            .VerifyFingerprint());
        Assert.Throws<InvalidDataException>(() =>
            (p with { Deposit = p.Deposit with { ProposedCarryForwardVnd = 1m } })
            .VerifyFingerprint());
    }

    [Fact]
    public void TRF_PACKET16_Restamped_Packet_Cannot_Claim_Other_Source_Documents()
    {
        var f = Fixture();
        var p = Prepare(f);
        var other = Fixture();
        Assert.Throws<InvalidDataException>(() =>
            p.VerifyAgainst(other.Request, other.Source, other.Roster,
                other.Policy, other.Review));
        Assert.Throws<InvalidDataException>(() =>
            (p with { PolicySha256 = new string('a', 64) }).VerifyFingerprint());
    }

    [Fact]
    public void TRF_PACKET17_Only_UTC_After_Independent_Review_Is_Allowed()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() => Prepare(f,
            at: new DateTime(2026, 9, 1, 10, 5, 0, DateTimeKind.Local)));
        Assert.Throws<InvalidOperationException>(() =>
            Prepare(f, at: Created.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() =>
            Prepare(f, packetId: Guid.Empty));
    }

    [Fact]
    public void TRF_PACKET18_Original_Roster_Or_Reviewed_Policy_Drift_Blocks_Reverify()
    {
        var f = Fixture();
        var p = Prepare(f);
        var field = typeof(RoomChangeBusinessPolicyRecord).GetField(
            "<PricingPolicyReference>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(f.Policy, "FORGED-TRANSFER-TARIFF-REFERENCE");
        Assert.Throws<InvalidDataException>(() =>
            p.VerifyAgainst(f.Request, f.Source, f.Roster, f.Policy, f.Review));
    }
}
