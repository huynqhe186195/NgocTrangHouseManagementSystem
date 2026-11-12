using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace BuildingManagement.DepositSettlements;

public class DepositSettlement_Tests
{
    [Fact]
    public void Should_Create_Pending_Settlement()
    {
        var paymentId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        var settlement =
            new DepositSettlement(
                Guid.NewGuid(),
                DepositSettlementType
                    .AllocateToContractDeposit,
                500_000,
                paymentId: paymentId,
                sourceRoomReservationId:
                    reservationId,
                targetContractId:
                    contractId,
                notes: "  Initial deposit  "
            );

        settlement.Type.ShouldBe(
            DepositSettlementType
                .AllocateToContractDeposit
        );

        settlement.Status.ShouldBe(
            DepositSettlementStatus.Pending
        );

        settlement.Amount.ShouldBe(500_000);

        settlement.PaymentId.ShouldBe(
            paymentId
        );

        settlement
            .SourceRoomReservationId
            .ShouldBe(reservationId);

        settlement.TargetContractId.ShouldBe(
            contractId
        );

        settlement.CompletedAt.ShouldBeNull();

        settlement.Notes.ShouldBe(
            "Initial deposit"
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void
        Should_Reject_Non_Positive_Amount(
            decimal amount)
    {
        var exception =
            Should.Throw<BusinessException>(
                () =>
                    new DepositSettlement(
                        Guid.NewGuid(),
                        DepositSettlementType
                            .Refund,
                        amount,
                        sourceRoomReservationId:
                            Guid.NewGuid()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementAmount
        );
    }

    [Fact]
    public void
        Refund_Should_Require_Exactly_One_Source()
    {
        var exception =
            Should.Throw<BusinessException>(
                () =>
                    new DepositSettlement(
                        Guid.NewGuid(),
                        DepositSettlementType
                            .Refund,
                        500_000
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementReference
        );
    }

    [Fact]
    public void
        Refund_Should_Not_Allow_Two_Sources()
    {
        var exception =
            Should.Throw<BusinessException>(
                () =>
                    new DepositSettlement(
                        Guid.NewGuid(),
                        DepositSettlementType
                            .Refund,
                        500_000,
                        sourceRoomReservationId:
                            Guid.NewGuid(),
                        sourceContractId:
                            Guid.NewGuid()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementReference
        );
    }

    [Fact]
    public void
        AllocateToContractDeposit_Should_Require_Payment_And_TargetContract()
    {
        var exception =
            Should.Throw<BusinessException>(
                () =>
                    new DepositSettlement(
                        Guid.NewGuid(),
                        DepositSettlementType
                            .AllocateToContractDeposit,
                        500_000,
                        paymentId:
                            Guid.NewGuid()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementReference
        );
    }

    [Fact]
    public void
        ContractTransfer_Should_Require_Different_Source_And_Target_Contracts()
    {
        var contractId = Guid.NewGuid();

        var exception =
            Should.Throw<BusinessException>(
                () =>
                    new DepositSettlement(
                        Guid.NewGuid(),
                        DepositSettlementType
                            .ContractTransfer,
                        3_000_000,
                        sourceContractId:
                            contractId,
                        targetContractId:
                            contractId
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementReference
        );
    }

    [Fact]
    public void
        Should_Complete_Pending_Settlement()
    {
        var settlement =
            new DepositSettlement(
                Guid.NewGuid(),
                DepositSettlementType.Forfeit,
                500_000,
                sourceRoomReservationId:
                    Guid.NewGuid()
            );

        var completedAt =
            new DateTime(
                2026,
                10,
                20,
                17,
                30,
                0
            );

        settlement.Complete(completedAt);

        settlement.Status.ShouldBe(
            DepositSettlementStatus.Completed
        );

        settlement.CompletedAt.ShouldBe(
            completedAt
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Contract_Deposit_Forfeit_Requires_Explanation(string? reason)
    {
        var ex = Should.Throw<BusinessException>(() =>
            new DepositSettlement(
                Guid.NewGuid(), DepositSettlementType.Forfeit,
                250_000m, sourceContractId: Guid.NewGuid(),
                notes: reason));

        ex.Code.ShouldBe(
            BuildingManagementErrorCodes.ContractDepositForfeitReasonRequired);
    }

    [Fact]
    public void Contract_Forfeit_Reason_Is_Trimmed_And_Retained_After_Completion()
    {
        var settlement = new DepositSettlement(
            Guid.NewGuid(), DepositSettlementType.Forfeit,
            250_000m, sourceContractId: Guid.NewGuid(),
            notes: "  Broken window in room 203  ");

        settlement.Complete(new DateTime(2026, 10, 27, 8, 0, 0,
            DateTimeKind.Utc));

        settlement.Notes.ShouldBe("Broken window in room 203");
        settlement.Status.ShouldBe(DepositSettlementStatus.Completed);
    }

    [Fact]
    public void Reservation_Forfeit_No_Show_Still_Allows_Missing_Notes()
    {
        var settlement = new DepositSettlement(
            Guid.NewGuid(), DepositSettlementType.Forfeit,
            500_000m, sourceRoomReservationId: Guid.NewGuid());

        settlement.Complete(DateTime.UtcNow);

        settlement.Status.ShouldBe(DepositSettlementStatus.Completed);
        settlement.Notes.ShouldBeNull();
    }

    [Fact]
    public void Legacy_Contract_Forfeit_Without_Reason_Cannot_Be_Completed()
    {
        var settlement = new DepositSettlement(
            Guid.NewGuid(), DepositSettlementType.Forfeit,
            300_000m, sourceContractId: Guid.NewGuid(),
            notes: "Originally documented damage");

        // Simulate a legacy materialized/altered Pending row. A normal
        // constructor would have rejected this record.
        var notesProperty = typeof(DepositSettlement)
            .GetProperty(nameof(DepositSettlement.Notes))!;
        notesProperty.SetValue(settlement, null);

        var ex = Should.Throw<BusinessException>(() =>
            settlement.Complete(DateTime.UtcNow));
        ex.Code.ShouldBe(
            BuildingManagementErrorCodes.ContractDepositForfeitReasonRequired);
        settlement.Status.ShouldBe(DepositSettlementStatus.Pending);
        settlement.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void
        Should_Not_Complete_Settlement_Twice()
    {
        var settlement =
            new DepositSettlement(
                Guid.NewGuid(),
                DepositSettlementType.Forfeit,
                500_000,
                sourceRoomReservationId:
                    Guid.NewGuid()
            );

        settlement.Complete(
            new DateTime(
                2026,
                10,
                20,
                17,
                30,
                0
            )
        );

        var exception =
            Should.Throw<BusinessException>(
                () =>
                    settlement.Complete(
                        new DateTime(
                            2026,
                            10,
                            20,
                            18,
                            0,
                            0
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .DepositSettlementCannotBeCompleted
        );
    }
}