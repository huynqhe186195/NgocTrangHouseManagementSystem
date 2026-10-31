using System;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeTransferPreflight_Tests
{
    private static RoomChangeTransferFacts Ready() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.Empty, new DateOnly(2026, 12, 31),
        "Move closer to the stairway",
        ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available,
        Array.Empty<RoomChangeTargetReservationFact>(),
        Array.Empty<RoomChangeTargetContractFact>(),
        true, true, true, true, true, true, true, true);

    private static RoomChangeTransferFacts Valid()
    {
        var facts = Ready();
        return facts with { TargetBuildingId = facts.SourceBuildingId };
    }

    [Fact]
    public void TRF01_All_Evidence_All_Policies_All_Clear_Is_Preflight_Eligible()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid());
        Assert.True(result.CanProceedToTransactionalReview);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void TRF02_Missing_Policy_Decisions_Fail_Closed()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            ContractTreatmentPolicyApproved = false,
            MidMonthPricingPolicyApproved = false,
            DepositCarryForwardPolicyApproved = false
        });
        Assert.False(result.CanProceedToTransactionalReview);
        Assert.Contains("ContractTreatmentPolicyUnapproved", result.Blockers);
        Assert.Contains("TransferPricingPolicyUnapproved", result.Blockers);
        Assert.Contains("DepositCarryForwardNotApprovedOrReconciled", result.Blockers);
    }

    [Theory]
    [InlineData(RoomStatus.Occupied)]
    [InlineData(RoomStatus.Reserved)]
    [InlineData(RoomStatus.Maintenance)]
    [InlineData(RoomStatus.Inactive)]
    public void TRF03_Only_Available_Target_Is_Eligible(RoomStatus status)
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            TargetRoomStatus = status
        });
        Assert.Contains("TargetRoomNotAvailable", result.Blockers);
    }

    [Fact]
    public void TRF04_Cannot_Transfer_To_Same_Room_Or_Foreign_Building()
    {
        var facts = Valid();
        Assert.Contains("InvalidTransferIdentityOrDate",
            RoomChangeTransferPreflight.Assess(facts with
            { TargetRoomId = facts.SourceRoomId }).Blockers);
        Assert.Contains("CrossBuildingTransferNotAuthorized",
            RoomChangeTransferPreflight.Assess(facts with
            { TargetBuildingId = Guid.NewGuid() }).Blockers);
    }

    [Theory]
    [InlineData(RoomReservationStatus.PendingPayment)]
    [InlineData(RoomReservationStatus.Reserved)]
    public void TRF05_Unresolved_Target_Reservation_Blocks_Transfer(
        RoomReservationStatus status)
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            TargetReservations = new[]
            {
                new RoomChangeTargetReservationFact(Guid.NewGuid(), status)
            }
        });
        Assert.Contains("TargetReservationUnresolved", result.Blockers);
    }

    [Theory]
    [InlineData(ContractStatus.Active)]
    [InlineData(ContractStatus.Signed)]
    public void TRF06_Existing_Target_Contract_Blocks_Transfer(
        ContractStatus status)
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            TargetContracts = new[]
            {
                new RoomChangeTargetContractFact(Guid.NewGuid(), status)
            }
        });
        Assert.Contains("TargetContractUnresolved", result.Blockers);
    }

    [Fact]
    public void TRF07_Source_Contract_Must_Remain_Active_And_Occupied()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            SourceContractStatus = ContractStatus.Ended,
            SourceRoomStatus = RoomStatus.Available
        });
        Assert.Contains("SourceTenancyNotActive", result.Blockers);
    }

    [Fact]
    public void TRF08_Cannot_Transfer_With_Unpaid_Debt_Or_Unverified_Meters()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            FinalMeterEvidenceVerified = false,
            SourceInvoiceDebtSettled = false,
            SourceInspectionSigned = false
        });
        Assert.Contains("SourceHandoverEvidenceNotVerified", result.Blockers);
        Assert.Contains("SourceDebtNotReconciled", result.Blockers);
    }

    [Fact]
    public void TRF09_Carry_Forward_Never_Bypasses_Double_Disposition_Audit()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            DepositExclusivityReconciled = false
        });
        Assert.Contains("DepositCarryForwardNotApprovedOrReconciled", result.Blockers);
    }

    [Fact]
    public void TRF10_Unknown_Or_Duplicate_Evidence_Is_Not_Treated_As_Empty()
    {
        var id = Guid.NewGuid();
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            TargetReservations = new[]
            {
                new RoomChangeTargetReservationFact(id, RoomReservationStatus.Cancelled),
                new RoomChangeTargetReservationFact(id, RoomReservationStatus.Cancelled)
            },
            TargetContracts = new[]
            {
                new RoomChangeTargetContractFact(Guid.NewGuid(), (ContractStatus)99)
            }
        });
        Assert.Contains("DuplicateTargetCommitmentEvidence", result.Blockers);
        Assert.Contains("TargetContractUnresolved", result.Blockers);
    }

    [Fact]
    public void TRF11_Null_Evidence_And_Empty_Reason_Cannot_Pass()
    {
        var result = RoomChangeTransferPreflight.Assess(Valid() with
        {
            Reason = " ",
            TargetContracts = null,
            TenantRosterVerified = false
        });
        Assert.Contains("TransferReasonMissing", result.Blockers);
        Assert.Contains("TargetCommitmentFactsMissing", result.Blockers);
        Assert.Contains("TransferTenantRosterNotVerified", result.Blockers);
        Assert.False(result.CanProceedToTransactionalReview);
    }

    [Fact]
    public void TRF12_Missing_Facts_Fail_Closed()
    {
        var result = RoomChangeTransferPreflight.Assess(null);
        Assert.False(result.CanProceedToTransactionalReview);
        Assert.Contains("TransferFactsMissing", result.Blockers);
    }
}
