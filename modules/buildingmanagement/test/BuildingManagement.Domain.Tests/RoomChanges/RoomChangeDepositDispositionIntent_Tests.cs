using System;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeDepositDispositionIntent_Tests
{
    private static readonly DateTime CreatedAt =
        new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static (
        RoomChangeRequest Request,
        RoomChangeDepositDifferenceFacts Facts,
        Guid Owner) Source(decimal held, decimal required,
            decimal deductions = 0m)
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var request = RoomChangeRequest.Submit(
            Guid.NewGuid(), source, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 11, 15),
            "Move to a different room on signed transfer day",
            owner, CreatedAt, ContractStatus.Active,
            RoomStatus.Occupied, RoomStatus.Available);
        var facts = new RoomChangeDepositDifferenceFacts(
            source, target, held, deductions, required,
            "ACTUAL-CASH-LEDGER-SOURCE-REQUIRES-REVIEW-001",
            "OWNER-SIGNED-TARGET-DEPOSIT-POLICY-001",
            deductions > 0m ? "SIGNED-POSTED-DEDUCTION-LEDGER-001" : null,
            HasUnresolvedDispute:false,
            HasEarlierRefundOrCarryForward:false);
        return(request,facts,owner);
    }

    private static RoomChangeDeferredDepositDispositionPlan Plan(
        RoomChangeRequest request, RoomChangeDepositDifferenceFacts facts,
        RoomChangeDepositSurplusChoice choice =
            RoomChangeDepositSurplusChoice.None)
        => RoomChangeDeferredDepositDispositionPlanner.Plan(
            request.Id, facts, request.RequestedDate, choice);

    private static RoomChangeDepositDispositionIntentRecord Capture(
        RoomChangeRequest request, RoomChangeDepositDifferenceFacts facts,
        RoomChangeDeferredDepositDispositionPlan plan, Guid owner)
        => RoomChangeDepositDispositionIntentRecord.Capture(
            Guid.NewGuid(), request, facts, plan, owner,
            CreatedAt.AddDays(2).AddTicks(5),
            "OWNER-SIGNED-EXCLUSIVE-H4-SETTLEMENT-INTENT-001");

    [Fact]
    public void TRF_H4INT01_Deposit_TopUp_Stays_Uncollected_And_One_Month_Due()
    {
        var (request,facts,owner)=Source(3_000_000m,4_000_000m);
        var row=Capture(request,facts,Plan(request,facts),owner);
        Assert.Equal(1_000_000m,row.ProposedUnpaidTopUpVnd);
        Assert.Equal(3_000_000m,row.ProposedCarryTargetVnd);
        Assert.Equal(new DateOnly(2026,12,15),row.ProposedTopUpDueDateVietnam);
        Assert.False(row.CanCompleteTransfer);
        Assert.False(row.IsCashBackedPosting);
        Assert.False(row.IsTopUpCollected);
        row.VerifyFingerprint();
    }

    [Fact]
    public void TRF_H4INT02_Excess_Deposit_Only_Credits_Future_Rent()
    {
        var (request,facts,owner)=Source(3_000_000m,2_000_000m);
        var row=Capture(request,facts,Plan(request,facts,
            RoomChangeDepositSurplusChoice.CreditNextMonthRent),owner);
        Assert.Equal(1_000_000m,row.ProposedFutureRentCreditVnd);
        Assert.Equal(0m,row.ProposedManualRefundVnd);
        Assert.False(row.IsRentCreditApplied);
        row.VerifyFingerprint();
    }

    [Fact]
    public void TRF_H4INT03_Excess_Deposit_Only_Proposes_Manual_Refund()
    {
        var (request,facts,owner)=Source(3_000_000m,2_000_000m);
        var row=Capture(request,facts,Plan(request,facts,
            RoomChangeDepositSurplusChoice.ReviewedManualRefund),owner);
        Assert.Equal(0m,row.ProposedFutureRentCreditVnd);
        Assert.Equal(1_000_000m,row.ProposedManualRefundVnd);
        Assert.False(row.IsTenantRefundPaid);
        row.VerifyFingerprint();
    }

    [Fact]
    public void TRF_H4INT04_Tamper_With_Money_Or_Signature_Detected()
    {
        var (request,facts,owner)=Source(3_000_000m,4_000_000m);
        var row=Capture(request,facts,Plan(request,facts),owner);
        Assert.Throws<System.IO.InvalidDataException>(() =>
            (row with { ProposedUnpaidTopUpVnd=0m }).VerifyFingerprint());
        Assert.Throws<System.IO.InvalidDataException>(() =>
            (row with { OwnerSignedDispositionReference="FAKE-SIGNATURE-001" })
                .VerifyFingerprint());
    }

    [Fact]
    public void TRF_H4INT05_Rewritten_Preview_Cannot_Be_Frozen()
    {
        var (request,facts,owner)=Source(3_000_000m,4_000_000m);
        var plan=Plan(request,facts);
        Assert.Throws<InvalidOperationException>(() =>
            Capture(request,facts,plan with {
                OutstandingTopUpReceivableVnd=999_999m
            },owner));
    }

    [Fact]
    public void TRF_H4INT06_Foreign_Request_Or_Source_Contract_Denied()
    {
        var (request,facts,owner)=Source(3_000_000m,4_000_000m);
        var plan=Plan(request,facts);
        var other=Source(3_000_000m,4_000_000m);
        Assert.Throws<InvalidOperationException>(() =>
            Capture(other.Request,facts,plan,owner));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(request,facts with {
                SourceContractId=Guid.NewGuid()
            },plan,owner));
    }

    [Fact]
    public void TRF_H4INT07_Author_And_Original_Document_Required()
    {
        var (request,facts,owner)=Source(3_000_000m,4_000_000m);
        var plan=Plan(request,facts);
        Assert.Throws<InvalidOperationException>(() =>
            Capture(request,facts,plan,Guid.Empty));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(request,facts with {
                SourceHeldCashLedgerReference=" "
            },plan,owner));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDispositionIntentRecord.Capture(
                Guid.NewGuid(),request,facts,plan,owner,
                CreatedAt.AddDays(-1),"OWNER-SIGNED-H4-RECONCILIATION-001"));
    }

    [Fact]
    public void TRF_H4INT08_Original_Deduction_Preserved_In_Conservation()
    {
        var (request,facts,owner)=Source(
            3_000_000m,2_000_000m,500_000m);
        var row=Capture(request,facts,Plan(request,facts,
            RoomChangeDepositSurplusChoice.CreditNextMonthRent),owner);
        Assert.Equal(row.ClaimedOriginalSourceHeldVnd,
            row.AlreadyPostedApprovedDeductionsVnd +
            row.ProposedCarryTargetVnd +
            row.ProposedFutureRentCreditVnd +
            row.ProposedManualRefundVnd);
        row.VerifyFingerprint();
    }

    // For adversarial tests only: rehash a forged record to prove that a
    // checksum by itself cannot authorize an economically invalid H4 split.
    private static RoomChangeDepositDispositionIntentRecord Rehash(
        RoomChangeDepositDispositionIntentRecord row)
    {
        var method = typeof(RoomChangeDepositDispositionIntentRecord)
            .GetMethod("Fingerprint",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        return row with { EvidenceSha256 = (string)method!.Invoke(row, null)! };
    }

    [Fact]
    public void TRF_H4INT09_Rehashed_Contradictory_TopUp_And_Credit_Fail_Closed()
    {
        var (request, facts, owner) = Source(3_000_000m, 4_000_000m);
        var original = Capture(request, facts, Plan(request, facts), owner);
        original.VerifyFingerprint();

        var forged = Rehash(original with {
            ProposedUnpaidTopUpVnd = 0m,
            ProposedFutureRentCreditVnd = 1_000_000m
        });
        Assert.Throws<System.IO.InvalidDataException>(
            () => forged.VerifyFingerprint());
    }

    [Fact]
    public void TRF_H4INT10_Rehashed_Invalid_Due_And_Phantom_Deduction_Fail()
    {
        var (request, facts, owner) = Source(3_000_000m, 4_000_000m);
        var original = Capture(request, facts, Plan(request, facts), owner);

        var late = Rehash(original with {
            ProposedTopUpDueDateVietnam = new DateOnly(2026, 12, 16)
        });
        Assert.Throws<System.IO.InvalidDataException>(
            () => late.VerifyFingerprint());

        var phantom = Rehash(original with {
            SignedDeductionReference = "PHANTOM-SIGNED-DEDUCTION-001"
        });
        Assert.Throws<System.IO.InvalidDataException>(
            () => phantom.VerifyFingerprint());
    }
}
