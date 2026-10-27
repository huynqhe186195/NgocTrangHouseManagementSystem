using System;
using System.Collections.Generic;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.DepositSettlements;

public sealed class ContractDepositLedgerReview_Tests
{
    [Fact]
    public void DEPLEDGER01_Direct_Completed_Contract_Deposit_Allocation_Is_Visible()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ContractDeposit, contract.Id);
        var allocation = Allocation(contract.Id, payment.Id, payment.Amount);

        var audit = Assess(request, contract, new[] { allocation }, new[] { payment });

        Assert.Equal(payment.Amount, audit.VerifiedIncomingAllocationTotal);
        Assert.Equal(1, audit.CompletedIncomingCount);
        Assert.Equal(0m, audit.RecordedCompletedOutgoingTotal);
        Assert.Null(audit.RefundableBalance);
        Assert.False(audit.CanRefund);
        Assert.False(audit.CanOffsetDebt);
        Assert.False(audit.IsSettlementApproved);
        Assert.Contains("FinalInvoiceDebtNotReconciled", audit.Blockers);
    }

    [Fact]
    public void DEPLEDGER02_Pending_Payment_Cannot_Count_As_Deposit()
    {
        var (request, contract) = Fixture();
        var pending = Receipt(PaymentPurpose.ContractDeposit, contract.Id,
            completed: false);
        var settlement = Allocation(contract.Id, pending.Id, pending.Amount);

        var audit = Assess(request, contract, new[] { settlement }, new[] { pending });

        Assert.Null(audit.VerifiedIncomingAllocationTotal);
        Assert.Contains("DepositAllocationPaymentMismatch", audit.Blockers);
    }

    [Fact]
    public void DEPLEDGER03_Overallocated_Payment_Cannot_Be_Counted_Twice()
    {
        var (request, contract) = Fixture();
        var receipt = Receipt(PaymentPurpose.ContractDeposit, contract.Id);
        var first = Allocation(contract.Id, receipt.Id, receipt.Amount);
        var second = Allocation(contract.Id, receipt.Id, 1m);

        var audit = Assess(request, contract,
            new[] { first, second }, new[] { receipt });

        Assert.Null(audit.VerifiedIncomingAllocationTotal);
        Assert.Contains("DepositPaymentOverAllocatedOrCrossContract", audit.Blockers);
    }

    [Fact]
    public void DEPLEDGER04_One_Receipt_Allocated_To_Two_Contracts_Is_Rejected()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ReservationDeposit,
            contract.Id, reservationId: Guid.NewGuid());
        var first = Allocation(contract.Id, payment.Id, 250000m,
            payment.RoomReservationId);
        var second = Allocation(Guid.NewGuid(), payment.Id, 250000m,
            payment.RoomReservationId);

        var audit = Assess(request, contract,
            new[] { first, second }, new[] { payment });

        Assert.Contains("DepositPaymentOverAllocatedOrCrossContract", audit.Blockers);
        Assert.Null(audit.VerifiedIncomingAllocationTotal);
    }

    [Fact]
    public void DEPLEDGER05_Valid_Partial_Reservation_Allocation_Counts_Only_Applied_Amount()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ReservationDeposit,
            contract.Id, reservationId: Guid.NewGuid());
        var allocated = Allocation(contract.Id, payment.Id, 200000m,
            payment.RoomReservationId);

        var audit = Assess(request, contract, new[] { allocated }, new[] { payment });

        Assert.Equal(200000m, audit.VerifiedIncomingAllocationTotal);
        Assert.Null(audit.RefundableBalance);
    }

    [Fact]
    public void DEPLEDGER06_Refund_From_Contract_Requires_Full_Reconciliation()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ContractDeposit, contract.Id);
        var incoming = Allocation(contract.Id, payment.Id, payment.Amount);
        var outgoing = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.Refund, 100000m,
            sourceContractId: contract.Id);
        outgoing.Complete(DateTime.UtcNow);

        var audit = Assess(request, contract,
            new[] { incoming, outgoing }, new[] { payment });

        Assert.Null(audit.VerifiedIncomingAllocationTotal);
        Assert.Contains("RecordedOutgoingDepositNeedsReconciliation", audit.Blockers);
        Assert.False(audit.CanRefund);
    }

    [Fact]
    public void DEPLEDGER07_Transfer_From_Another_Contract_Needs_Provenance()
    {
        var (request, contract) = Fixture();
        var transfer = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.RoomChangeCarryForward, 300000m,
            sourceContractId: Guid.NewGuid(), targetContractId: contract.Id);
        transfer.Complete(DateTime.UtcNow);

        var audit = Assess(request, contract, new[] { transfer },
            Array.Empty<Payment>());

        Assert.Null(audit.VerifiedIncomingAllocationTotal);
        Assert.Contains("IncomingTransferProvenanceNotVerified", audit.Blockers);
    }

    [Fact]
    public void DEPLEDGER08_Pending_Request_And_No_Allocations_Are_Blocked()
    {
        var (request, contract) = Fixture(approve: false);

        var audit = Assess(request, contract,
            Array.Empty<DepositSettlement>(), Array.Empty<Payment>());

        Assert.Contains("ApprovedMoveOutAndActiveContractRequired", audit.Blockers);
        Assert.Contains("NoCompletedDepositAllocation", audit.Blockers);
        Assert.Null(audit.RefundableBalance);
    }

    [Fact]
    public void DEPLEDGER09_Allocated_Amount_Greater_Than_Deposit_Is_An_Anomaly()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ContractDeposit, contract.Id, amount: 3500000m);
        var allocation = Allocation(contract.Id, payment.Id, payment.Amount);

        var audit = Assess(request, contract,
            new[] { allocation }, new[] { payment });

        Assert.Contains("AllocatedDepositExceedsContractRequirement", audit.Blockers);
        Assert.Null(audit.VerifiedIncomingAllocationTotal);
    }

    [Fact]
    public void DEPLEDGER10_Incomplete_Settlement_Cannot_Be_Treated_As_Held()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ContractDeposit, contract.Id);
        var pending = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.AllocateToContractDeposit,
            payment.Amount, paymentId: payment.Id,
            targetContractId: contract.Id);

        var audit = Assess(request, contract, new[] { pending }, new[] { payment });

        Assert.Contains("PendingDepositSettlementRequiresReview", audit.Blockers);
        Assert.Null(audit.VerifiedIncomingAllocationTotal);
    }

    [Fact]
    public void DEPLEDGER11_Legacy_Contract_Forfeit_Without_Reason_Is_Flagged()
    {
        var (request, contract) = Fixture();
        var payment = Receipt(PaymentPurpose.ContractDeposit, contract.Id);
        var incoming = Allocation(contract.Id, payment.Id, payment.Amount);
        var outgoing = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.Forfeit, 100000m,
            sourceContractId: contract.Id,
            notes: "Broken item observed");

        // A historical record can be corrupted independently from today's
        // validated constructor. The audit must expose missing reasons.
        typeof(DepositSettlement)
            .GetProperty(nameof(DepositSettlement.Notes))!
            .SetValue(outgoing, null);

        var audit = Assess(request, contract,
            new[] { incoming, outgoing }, new[] { payment });

        Assert.Contains("ContractDepositForfeitReasonMissing", audit.Blockers);
        Assert.Null(audit.RefundableBalance);
        Assert.False(audit.CanRefund);
    }

    private static ContractDepositLedgerReview Assess(
        MoveOutRequest request, Contract contract,
        DepositSettlement[] settlements, Payment[] payments)
        => ContractDepositLedgerReviewEvaluator.Assess(
            request, contract, settlements, payments);

    private static (MoveOutRequest, Contract) Fixture(bool approve = true)
    {
        var roomId = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "DEP-REVIEW",
            roomId, new DateTime(2026, 1, 1), null, 3000000m);
        contract.Activate();
        var request = new MoveOutRequest(Guid.NewGuid(), contract.Id, roomId,
            new DateOnly(2026, 11, 30), "Deposit reconciliation");
        if (approve) request.Approve(DateTime.UtcNow, null);
        return (request, contract);
    }

    private static Payment Receipt(
        PaymentPurpose purpose, Guid contractId,
        Guid? reservationId = null, bool completed = true,
        decimal amount = 500000m)
    {
        var payment = new Payment(Guid.NewGuid(),
            "DEPREVIEW-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), purpose,
            PaymentMethod.Cash, PaymentChannel.Manual, amount,
            roomReservationId: reservationId,
            contractId: purpose == PaymentPurpose.ContractDeposit
                ? contractId : null);
        if (completed) payment.Complete(DateTime.UtcNow);
        return payment;
    }

    private static DepositSettlement Allocation(
        Guid targetContractId, Guid paymentId, decimal amount,
        Guid? reservationId = null)
    {
        var settlement = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.AllocateToContractDeposit,
            amount, paymentId: paymentId,
            sourceRoomReservationId: reservationId,
            targetContractId: targetContractId);
        settlement.Complete(DateTime.UtcNow);
        return settlement;
    }
}
