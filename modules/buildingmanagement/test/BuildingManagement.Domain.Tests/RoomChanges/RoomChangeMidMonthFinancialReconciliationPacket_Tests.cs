using System;
using System.IO;
using System.Linq;
using BuildingManagement.Utilities;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
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
    private static Payment CompletedTargetTopUp(
        RoomChangeMidMonthFinancialReconciliationPacket packet,
        decimal? amount = null, Guid? target = null,
        PaymentPurpose purpose = PaymentPurpose.ContractDeposit,
        bool complete = true)
    {
        var payment = new Payment(Guid.NewGuid(), "TRF-2H-TOPUP-001",
            Guid.NewGuid(), purpose, PaymentMethod.BankTransfer,
            PaymentChannel.Manual,
            amount ?? packet.Deposit.UnpaidTopUpRequirementVnd,
            contractId: target ?? packet.ProposedTargetContractId,
            transactionReference: "BANK-2H-TOPUP-TRANSACTION-001",
            notes: "BANK-PROOF: SIGNED-2H-TOPUP-RECEIPT-001");
        if (complete)
            payment.Complete(Created.AddMinutes(7),
                "BANK-2H-TOPUP-TRANSACTION-001");
        return payment;
    }

    private static RoomChangeTransferDepositCashReview TopUpReview(
        RoomChangeMidMonthFinancialReconciliationPacket packet,
        Payment payment, Guid? owner = null, Guid? reviewer = null,
        DateTime? reviewed = null)
        => RoomChangeTransferDepositCashReview.ReviewTopUp(
            Guid.NewGuid(), packet, payment, owner ?? Guid.NewGuid(),
            reviewer ?? Guid.NewGuid(), Created.AddMinutes(6),
            reviewed ?? Created.AddMinutes(8),
            "SIGNED-OWNER-TARGET-DEPOSIT-DIRECTION-001",
            "SIGNED-BANK-STATEMENT-TOPUP-001",
            "SIGNED-INDEPENDENT-BANK-REVIEW-TOPUP-001");

    private static RoomChangeTransferDepositCashReview RefundReview(
        RoomChangeMidMonthFinancialReconciliationPacket packet,
        Guid? owner = null, Guid? reviewer = null)
        => RoomChangeTransferDepositCashReview.ReviewManualRefund(
            Guid.NewGuid(), packet, owner ?? Guid.NewGuid(),
            reviewer ?? Guid.NewGuid(), Created.AddMinutes(6),
            Created.AddMinutes(7), Created.AddMinutes(8),
            "BANK-2H-REFUND-TRANSACTION-001",
            "SIGNED-OWNER-REFUND-RECIPIENT-ACCOUNT-001",
            "SIGNED-INDEPENDENT-BANK-STATEMENT-REFUND-001",
            "SIGNED-INDEPENDENT-REFUND-REVIEW-001");

    [Fact]
    public void TRF_CASH01_Completed_Target_Contract_Deposit_Receipt_Is_D04_Preview_Only()
    {
        var packet = Prepare(Fixture(targetRent: 4_000_000m));
        var payment = CompletedTargetTopUp(packet);
        var proof = TopUpReview(packet, payment);
        proof.Verify(packet, payment);
        Assert.Equal(RoomChangeDepositCashLeg.VerifiedTopUp, proof.Leg);
        Assert.Equal(1_000_000m, proof.ExactAmountVnd);
        Assert.Equal(payment.Id, proof.OriginalPaymentId);
        Assert.False(proof.IsBankGatewayVerified);
        Assert.False(proof.IsCashLedgerPosted);
        Assert.False(proof.CanExecuteTransfer);
        Assert.False(proof.CanExecuteBankTransfer);
    }

    [Fact]
    public void TRF_CASH02_Manual_Partial_Refund_Requires_Independent_Statement()
    {
        var packet = Prepare(Fixture(targetRent: 2_000_000m));
        var proof = RefundReview(packet);
        proof.Verify(packet);
        Assert.Equal(RoomChangeDepositCashLeg.ManuallyReviewedRefund, proof.Leg);
        Assert.Equal(1_000_000m, proof.ExactAmountVnd);
        Assert.Null(proof.OriginalPaymentId);
        Assert.False(proof.IsCashLedgerPosted);
        Assert.False(proof.CanExecuteBankTransfer);
    }

    [Fact]
    public void TRF_CASH03_No_Pending_Wrong_Purpose_Or_Wrong_Amount_Topup()
    {
        var packet = Prepare(Fixture(targetRent: 4_000_000m));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, CompletedTargetTopUp(packet, complete: false)));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, CompletedTargetTopUp(packet, amount: 999_999m)));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, CompletedTargetTopUp(packet,
                purpose: PaymentPurpose.InvoicePayment)));
    }

    [Fact]
    public void TRF_CASH04_Cross_Contract_Topup_Cannot_Fund_Target()
    {
        var packet = Prepare(Fixture(targetRent: 4_000_000m));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, CompletedTargetTopUp(packet,
                target: Guid.NewGuid())));
    }

    [Fact]
    public void TRF_CASH05_Independent_Bank_Reviewer_And_UTC_Order_Are_Mandatory()
    {
        var packet = Prepare(Fixture(targetRent: 4_000_000m));
        var payment = CompletedTargetTopUp(packet);
        var actor = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, payment, owner: actor, reviewer: actor));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, payment,
                reviewed: Created.AddMinutes(6)));
        var refund = Prepare(Fixture(targetRent: 2_000_000m));
        Assert.Throws<InvalidOperationException>(() =>
            RefundReview(refund, owner: actor, reviewer: actor));
    }

    [Fact]
    public void TRF_CASH06_Foreign_Financial_Packet_And_Unloaded_Payment_Cannot_Reverify()
    {
        var packet = Prepare(Fixture(targetRent: 4_000_000m));
        var payment = CompletedTargetTopUp(packet);
        var review = TopUpReview(packet, payment);
        Assert.Throws<InvalidDataException>(() => review.Verify(packet));
        var other = Prepare(Fixture(targetRent: 4_000_000m));
        Assert.Throws<InvalidDataException>(() => review.Verify(other, payment));
    }

    [Fact]
    public void TRF_CASH07_No_Movement_Evidence_For_Full_Carry_Or_Wrong_Refund_Lane()
    {
        var packet = Prepare(Fixture(targetRent: 3_000_000m));
        Assert.Throws<InvalidOperationException>(() =>
            RefundReview(packet));
        Assert.Throws<InvalidOperationException>(() =>
            TopUpReview(packet, CompletedTargetTopUp(packet, amount: 1m)));
        var topup = Prepare(Fixture(targetRent: 4_000_000m));
        Assert.Throws<InvalidOperationException>(() =>
            RefundReview(topup));
    }

    private static RoomChangeMidMonthDossierReadiness Dossier(
        Evidence f, bool includePricingOwner = true,
        bool includeThreeParties = true,
        bool includeCashProof = true)
    {
        var packet = Prepare(f);
        var dayRule = RoomChangeDayPricingRuleCandidate.Prepare(
            Guid.NewGuid(), f.Request, f.Source, f.Roster,
            f.Policy, f.Review, f.Quote, Guid.NewGuid(),
            Created.AddMinutes(4));
        var ownerSign = RoomChangeDayPricingOwnerSignoff.Sign(
            Guid.NewGuid(), dayRule, f.Request, f.Source,
            f.Roster, f.Policy, f.Review,
            f.Review.ReviewedByUserId, Created.AddMinutes(6),
            "OWNER-SIGNED-EXACT-DAY-AND-ROUNDING-2H-2026");
        var em = new UtilityMeter(Guid.NewGuid(), f.Source.RoomId,
            UtilityType.Electricity, "D06-PREFLIGHT-E-2026", 0m,
            new DateTime(2026, 8, 1));
        var wm = new UtilityMeter(Guid.NewGuid(), f.Source.RoomId,
            UtilityType.Water, "D06-PREFLIGHT-W-2026", 0m,
            new DateTime(2026, 8, 1));
        var er = new MeterReading(Guid.NewGuid(), em.Id,
            2026, 9, 0m, 20m, new DateTime(2026, 9, 15));
        var wr = new MeterReading(Guid.NewGuid(), wm.Id,
            2026, 9, 0m, 30m, new DateTime(2026, 9, 15));
        var physical = RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), f.Request, em, er, wm, wr,
            "SIGNED-D06-ASSET-AND-DEFECT-SNAPSHOT-2026",
            "SIGNED-D06-PHYSICAL-METER-HANDOVER-2026",
            Guid.NewGuid(), Created.AddMinutes(6));
        var tenant = f.Roster.ReadVerifiedRoster().Single(
            x => x.Role == ContractTenantRole.PrimaryTenant).TenantId;
        var signatures = RoomChangeHandoverThreePartySignoff.Sign(
            Guid.NewGuid(), f.Request, f.Roster, physical,
            f.Review.ReviewedByUserId, physical.AttestedByUserId,
            tenant,
            "SIGNED-OWNER-D06-CHECKLIST-2026",
            "SIGNED-STAFF-D06-CHECKLIST-2026",
            "SIGNED-TENANT-D06-CHECKLIST-2026",
            "SIGNED-D06-ASSET-DEFECT-REVIEW-2026",
            Created.AddMinutes(9));
        RoomChangeTransferDepositCashReview? cash = null;
        Payment? payment = null;
        if (includeCashProof && packet.Deposit.UnpaidTopUpRequirementVnd > 0m)
        {
            payment = CompletedTargetTopUp(packet);
            cash = TopUpReview(packet, payment);
        }
        else if (includeCashProof &&
                 packet.Deposit.UnpaidRefundCandidateVnd > 0m)
            cash = RefundReview(packet);
        return RoomChangeMidMonthDossierPreflight.Assess(
            f.Request, f.Source, f.Roster, f.Policy, f.Review,
            packet, includePricingOwner ? dayRule : null,
            includePricingOwner ? ownerSign : null,
            physical, em, er, wm, wr,
            includeThreeParties ? signatures : null,
            cash, payment);
    }

    [Fact]
    public void TRF_DOSSIER01_Full_Checked_Documents_Are_Not_Financial_Posting()
    {
        var result = Dossier(Fixture(targetRent: 3_000_000m));
        Assert.True(result.AllPresentedDocumentarySourcesMatch,
            string.Join(",", result.Blockers));
        Assert.Contains("D03OfficialPerLineAdjustmentPostingNotYetProven",
            result.Blockers);
        Assert.False(result.CanExecuteTransfer);
        Assert.False(result.IsOfficialInvoiceCorrectionPosted);
        Assert.False(result.IsDepositCashJournalPosted);
    }

    [Fact]
    public void TRF_DOSSIER02_Topup_Proof_Cannot_Bypass_D03_Official_Posting()
    {
        var result = Dossier(Fixture(targetRent: 4_000_000m));
        Assert.True(result.AllPresentedDocumentarySourcesMatch,
            string.Join(",", result.Blockers));
        Assert.False(result.CanExecuteTransfer);
        Assert.Contains("D03OfficialPerLineAdjustmentPostingNotYetProven",
            result.Blockers);
        Assert.False(result.IsBankGatewayVerified);
    }

    [Fact]
    public void TRF_DOSSIER03_Missing_Signatures_Or_Actual_Cash_Proof_Denied()
    {
        var f = Fixture(targetRent: 4_000_000m);
        var noOwner = Dossier(f, includePricingOwner: false);
        Assert.False(noOwner.AllPresentedDocumentarySourcesMatch);
        Assert.Contains("D01D02OwnerSignedPricingRuleMissing", noOwner.Blockers);
        var noSigners = Dossier(f, includeThreeParties: false);
        Assert.False(noSigners.AllPresentedDocumentarySourcesMatch);
        Assert.Contains("D06OriginalMeterAndThreePartySignatureMissing",
            noSigners.Blockers);
        var noCash = Dossier(f, includeCashProof: false);
        Assert.False(noCash.AllPresentedDocumentarySourcesMatch);
        Assert.Contains("D04MissingIndependentCashMovementEvidence",
            noCash.Blockers);
    }

    [Fact]
    public void TRF_ARCH06_D04_Topup_Archives_Exact_Reviewed_Target_Receipt()
    {
        var f = Fixture(targetRent: 4_000_000m);
        var packet = Prepare(f);
        var receipt = CompletedTargetTopUp(packet);
        var proof = TopUpReview(packet, receipt);
        var archived = RoomChangeEvidenceArchiveRecord.CaptureCashReview(
            Guid.NewGuid(), f.Request, f.Source, f.Roster,
            f.Policy, f.Review, packet, proof,
            receipt, Created.AddMinutes(10));
        archived.VerifyStoredSnapshot();
        Assert.Equal(RoomChangeEvidenceArchiveKind.ReviewedTransferCash, archived.Kind);
        Assert.Equal(proof.EvidenceSha256, archived.SourceEvidenceSha256);
        Assert.Equal(packet.PacketId, archived.ParentEvidenceId);
        Assert.Equal(packet.EvidenceSha256, archived.ParentEvidenceSha256);
        Assert.False(archived.CanRefundDeposit);
        Assert.False(archived.CanExecuteRoomChange);
        Assert.Throws<InvalidDataException>(() =>
            RoomChangeEvidenceArchiveRecord.CaptureCashReview(
                Guid.NewGuid(), f.Request, f.Source, f.Roster,
                f.Policy, f.Review, packet, proof,
                CompletedTargetTopUp(packet), Created.AddMinutes(10)));
    }

}
