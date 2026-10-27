using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.DepositSettlements;

public class DepositSettlement
    : AuditedAggregateRoot<Guid>
{
    public DepositSettlementType Type
    {
        get;
        private set;
    }

    public DepositSettlementStatus Status
    {
        get;
        private set;
    }

    public decimal Amount
    {
        get;
        private set;
    }

    public Guid? PaymentId
    {
        get;
        private set;
    }

    public Guid? SourceRoomReservationId
    {
        get;
        private set;
    }

    public Guid? SourceContractId
    {
        get;
        private set;
    }

    public Guid? TargetContractId
    {
        get;
        private set;
    }

    public DateTime? CompletedAt
    {
        get;
        private set;
    }

    public string? Notes
    {
        get;
        private set;
    }

    protected DepositSettlement()
    {
    }

    public DepositSettlement(
        Guid id,
        DepositSettlementType type,
        decimal amount,
        Guid? paymentId = null,
        Guid? sourceRoomReservationId = null,
        Guid? sourceContractId = null,
        Guid? targetContractId = null,
        string? notes = null
    ) : base(id)
    {
        if (amount <= 0)
        {
            throw new BusinessException(
                BuildingManagementErrorCodes
                    .InvalidDepositSettlementAmount
            );
        }

        ValidateReferences(
            type,
            paymentId,
            sourceRoomReservationId,
            sourceContractId,
            targetContractId
        );

        Type = type;
        Amount = amount;

        PaymentId = paymentId;

        SourceRoomReservationId =
            sourceRoomReservationId;

        SourceContractId =
            sourceContractId;

        TargetContractId =
            targetContractId;

        Notes =
            string.IsNullOrWhiteSpace(notes)
                ? null
                : notes.Trim();

        // A forfeiture from a held CONTRACT deposit is a financial
        // deduction and requires a reason. Reservation no-show forfeiture
        // is a separate pre-move-in policy, so preserve that workflow.
        EnsureContractForfeitReason(type, sourceContractId, Notes);

        Status =
            DepositSettlementStatus.Pending;
    }

    public void Complete(
        DateTime completedAt)
    {
        if (Status !=
            DepositSettlementStatus.Pending)
        {
            throw new BusinessException(
                BuildingManagementErrorCodes
                    .DepositSettlementCannotBeCompleted
            );
        }

        // Also guard legacy/deserialized Pending rows that predate
        // constructor validation and may lack a contract-forfeit reason.
        EnsureContractForfeitReason(Type, SourceContractId, Notes);

        Status =
            DepositSettlementStatus.Completed;

        CompletedAt = completedAt;
    }

    private static void EnsureContractForfeitReason(
        DepositSettlementType type,
        Guid? sourceContractId,
        string? notes)
    {
        if (type == DepositSettlementType.Forfeit &&
            sourceContractId.HasValue &&
            string.IsNullOrWhiteSpace(notes))
            throw new BusinessException(
                BuildingManagementErrorCodes.ContractDepositForfeitReasonRequired);
    }

    private static void ValidateReferences(
        DepositSettlementType type,
        Guid? paymentId,
        Guid? sourceRoomReservationId,
        Guid? sourceContractId,
        Guid? targetContractId)
    {
        switch (type)
        {
            case DepositSettlementType
                .Refund:

            case DepositSettlementType
                .Forfeit:

                ValidateRefundOrForfeit(
                    sourceRoomReservationId,
                    sourceContractId,
                    targetContractId
                );

                break;

            case DepositSettlementType
                .AllocateToContractDeposit:

                if (!paymentId.HasValue
                    ||
                    !targetContractId.HasValue
                    ||
                    sourceContractId.HasValue)
                {
                    ThrowInvalidReference();
                }

                break;

            case DepositSettlementType
                .ContractTransfer:

            case DepositSettlementType
                .RoomChangeCarryForward:

            case DepositSettlementType
                .RentCredit:

                if (!sourceContractId.HasValue
                    ||
                    !targetContractId.HasValue
                    ||
                    sourceContractId.Value ==
                    targetContractId.Value
                    ||
                    paymentId.HasValue
                    ||
                    sourceRoomReservationId.HasValue)
                {
                    ThrowInvalidReference();
                }

                break;

            default:
                ThrowInvalidReference();
                break;
        }
    }

    private static void
        ValidateRefundOrForfeit(
            Guid? sourceRoomReservationId,
            Guid? sourceContractId,
            Guid? targetContractId)
    {
        var sourceCount = 0;

        if (sourceRoomReservationId.HasValue)
        {
            sourceCount++;
        }

        if (sourceContractId.HasValue)
        {
            sourceCount++;
        }

        if (sourceCount != 1
            ||
            targetContractId.HasValue)
        {
            ThrowInvalidReference();
        }
    }

    private static void
        ThrowInvalidReference()
    {
        throw new BusinessException(
            BuildingManagementErrorCodes
                .InvalidDepositSettlementReference
        );
    }
}