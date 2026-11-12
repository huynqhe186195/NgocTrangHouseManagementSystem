using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;

namespace BuildingManagement.RoomChanges;

public enum RoomChangeDepositCashLeg { VerifiedTopUp = 1, ManuallyReviewedRefund = 2 }

/// <summary>
/// H4/D04: documentary review of ONE proposed top-up or bank refund.
/// For top-up the original, completed ContractDeposit Payment is REQUIRED.
/// For a refund the manually reviewed outbound bank statement is described
/// but CANNOT be treated as a posted payout without a separate immutable,
/// scope-locked bank/payout journal. No new Payment or deposit is created.
/// </summary>
public sealed class RoomChangeTransferDepositCashReview
{
    public Guid Id { get; private init; }
    public Guid RequestId { get; private init; }
    public Guid SourceContractId { get; private init; }
    public Guid TargetContractId { get; private init; }
    public Guid FinancialPacketId { get; private init; }
    public string FinancialPacketSha256 { get; private init; } = string.Empty;
    public RoomChangeDepositCashLeg Leg { get; private init; }
    public decimal ExactAmountVnd { get; private init; }
    public Guid? OriginalPaymentId { get; private init; }
    public Guid ApprovedByOwnerId { get; private init; }
    public Guid IndependentBankReviewerId { get; private init; }
    public DateTime ApprovedAtUtc { get; private init; }
    public DateTime PaidAtUtc { get; private init; }
    public DateTime ReviewedAtUtc { get; private init; }
    public string TransactionReference { get; private init; } = string.Empty;
    public string OwnerApprovedDestinationReference { get; private init; } = string.Empty;
    public string BankStatementReference { get; private init; } = string.Empty;
    public string SignedBankReviewReference { get; private init; } = string.Empty;
    public string EvidenceSha256 { get; private init; } = string.Empty;

    private RoomChangeTransferDepositCashReview() { }

    public static RoomChangeTransferDepositCashReview ReviewTopUp(
        Guid id, RoomChangeMidMonthFinancialReconciliationPacket packet,
        Payment payment, Guid ownerId, Guid reviewerId,
        DateTime approvedAtUtc, DateTime reviewedAtUtc,
        string? destinationApprovalReference,
        string? bankStatementReference, string? signedReviewReference)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var tx = payment.TransactionReference?.Trim() ?? string.Empty;
        if (packet is null)
            throw new InvalidOperationException("A verified transfer packet is required.");
        packet.VerifyFingerprint();
        if (packet.Deposit.UnpaidTopUpRequirementVnd <= 0m ||
            packet.Deposit.UnpaidRefundCandidateVnd != 0m ||
            packet.Deposit.ProposedPolicyLane !=
                RoomChangeDepositTreatment.CarryWithVerifiedTopUp ||
            payment.Id == Guid.Empty ||
            payment.Purpose != PaymentPurpose.ContractDeposit ||
            payment.Method != PaymentMethod.BankTransfer ||
            payment.Channel != PaymentChannel.Manual ||
            payment.Status != PaymentStatus.Completed ||
            payment.DepositDisposition != DepositDisposition.Unapplied ||
            payment.ContractId != packet.ProposedTargetContractId ||
            payment.InvoiceId.HasValue ||
            payment.RoomReservationId.HasValue ||
            payment.Amount != packet.Deposit.UnpaidTopUpRequirementVnd ||
            !payment.PaidAt.HasValue ||
            payment.PaidAt.Value.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(payment.Notes) ||
            !payment.Notes.StartsWith("BANK-PROOF: ", StringComparison.Ordinal) ||
            payment.Notes.Length < 20)
            throw new InvalidOperationException(
                "Top-up must be an original completed, unallocated, exact target deposit receipt.");

        return Create(id, packet, RoomChangeDepositCashLeg.VerifiedTopUp,
            payment.Amount, payment.Id, ownerId, reviewerId, approvedAtUtc,
            payment.PaidAt.Value, reviewedAtUtc, tx,
            destinationApprovalReference, bankStatementReference,
            signedReviewReference);
    }

    public static RoomChangeTransferDepositCashReview ReviewManualRefund(
        Guid id, RoomChangeMidMonthFinancialReconciliationPacket packet,
        Guid ownerId, Guid reviewerId,
        DateTime approvedAtUtc, DateTime paidAtUtc, DateTime reviewedAtUtc,
        string? actualBankTransactionReference,
        string? ownerApprovedRecipientAndAccountReference,
        string? bankStatementReference, string? signedReviewReference)
    {
        ArgumentNullException.ThrowIfNull(packet);
        packet.VerifyFingerprint();
        if (packet.Deposit.UnpaidRefundCandidateVnd <= 0m ||
            packet.Deposit.UnpaidTopUpRequirementVnd != 0m ||
            packet.Deposit.ProposedPolicyLane !=
                RoomChangeDepositTreatment.PartialCarryWithRefund)
            throw new InvalidOperationException(
                "A partial refund requires a documented source-cash surplus.");
        return Create(id, packet, RoomChangeDepositCashLeg.ManuallyReviewedRefund,
            packet.Deposit.UnpaidRefundCandidateVnd, null,
            ownerId, reviewerId, approvedAtUtc, paidAtUtc, reviewedAtUtc,
            actualBankTransactionReference, ownerApprovedRecipientAndAccountReference,
            bankStatementReference, signedReviewReference);
    }

    private static RoomChangeTransferDepositCashReview Create(
        Guid id, RoomChangeMidMonthFinancialReconciliationPacket packet,
        RoomChangeDepositCashLeg leg, decimal amount, Guid? paymentId,
        Guid owner, Guid reviewer, DateTime approvedAt, DateTime paidAt,
        DateTime reviewedAt, string? transaction, string? destination,
        string? statement, string? signed)
    {
        var tx = transaction?.Trim() ?? string.Empty;
        var ownerRef = destination?.Trim() ?? string.Empty;
        var bank = statement?.Trim() ?? string.Empty;
        var sign = signed?.Trim() ?? string.Empty;
        if (id == Guid.Empty || !Enum.IsDefined(leg) ||
            owner == Guid.Empty || reviewer == Guid.Empty ||
            owner == reviewer ||
            amount <= 0m || decimal.Truncate(amount) != amount ||
            approvedAt.Kind != DateTimeKind.Utc ||
            paidAt.Kind != DateTimeKind.Utc ||
            reviewedAt.Kind != DateTimeKind.Utc ||
            approvedAt < packet.PreparedAtUtc ||
            paidAt < approvedAt || reviewedAt < paidAt ||
            tx.Length is < 8 or > 128 ||
            ownerRef.Length is < 12 or > 500 ||
            bank.Length is < 12 or > 500 ||
            sign.Length is < 12 or > 500 ||
            tx == ownerRef || tx == bank || tx == sign ||
            ownerRef == bank || ownerRef == sign || bank == sign)
            throw new InvalidOperationException(
                "D04 requires exact VND, distinct Owner and bank reviewer, chronological signed proof.");

        var candidate = new RoomChangeTransferDepositCashReview
        {
            Id = id, RequestId = packet.RequestId,
            SourceContractId = packet.SourceContractId,
            TargetContractId = packet.ProposedTargetContractId,
            FinancialPacketId = packet.PacketId,
            FinancialPacketSha256 = packet.EvidenceSha256,
            Leg = leg, ExactAmountVnd = amount, OriginalPaymentId = paymentId,
            ApprovedByOwnerId = owner, IndependentBankReviewerId = reviewer,
            ApprovedAtUtc = Normalize(approvedAt),
            PaidAtUtc = Normalize(paidAt), ReviewedAtUtc = Normalize(reviewedAt),
            TransactionReference = tx,
            OwnerApprovedDestinationReference = ownerRef,
            BankStatementReference = bank, SignedBankReviewReference = sign
        };
        return new RoomChangeTransferDepositCashReview
        {
            Id = candidate.Id, RequestId = candidate.RequestId,
            SourceContractId = candidate.SourceContractId,
            TargetContractId = candidate.TargetContractId,
            FinancialPacketId = candidate.FinancialPacketId,
            FinancialPacketSha256 = candidate.FinancialPacketSha256,
            Leg = candidate.Leg, ExactAmountVnd = candidate.ExactAmountVnd,
            OriginalPaymentId = candidate.OriginalPaymentId,
            ApprovedByOwnerId = candidate.ApprovedByOwnerId,
            IndependentBankReviewerId = candidate.IndependentBankReviewerId,
            ApprovedAtUtc = candidate.ApprovedAtUtc,
            PaidAtUtc = candidate.PaidAtUtc, ReviewedAtUtc = candidate.ReviewedAtUtc,
            TransactionReference = candidate.TransactionReference,
            OwnerApprovedDestinationReference = candidate.OwnerApprovedDestinationReference,
            BankStatementReference = candidate.BankStatementReference,
            SignedBankReviewReference = candidate.SignedBankReviewReference,
            EvidenceSha256 = candidate.Hash()
        };
    }

    public void Verify(
        RoomChangeMidMonthFinancialReconciliationPacket packet,
        Payment? originalTopUpReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        packet.VerifyFingerprint();
        if (Id == Guid.Empty || FinancialPacketId != packet.PacketId ||
            FinancialPacketSha256 != packet.EvidenceSha256 ||
            RequestId != packet.RequestId ||
            SourceContractId != packet.SourceContractId ||
            TargetContractId != packet.ProposedTargetContractId ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("Deposit cash review is detached from original 2H packet.");
        try
        {
            RoomChangeTransferDepositCashReview rechecked;
            if (Leg == RoomChangeDepositCashLeg.VerifiedTopUp)
            {
                if (originalTopUpReceipt is null ||
                    OriginalPaymentId != originalTopUpReceipt.Id)
                    throw new InvalidDataException(
                        "Top-up cannot be reverified without its original completed Payment.");
                rechecked = ReviewTopUp(Id, packet, originalTopUpReceipt,
                    ApprovedByOwnerId, IndependentBankReviewerId,
                    ApprovedAtUtc, ReviewedAtUtc,
                    OwnerApprovedDestinationReference, BankStatementReference,
                    SignedBankReviewReference);
            }
            else if (Leg == RoomChangeDepositCashLeg.ManuallyReviewedRefund &&
                     OriginalPaymentId is null)
                rechecked = ReviewManualRefund(Id, packet,
                    ApprovedByOwnerId, IndependentBankReviewerId,
                    ApprovedAtUtc, PaidAtUtc, ReviewedAtUtc,
                    TransactionReference, OwnerApprovedDestinationReference,
                    BankStatementReference, SignedBankReviewReference);
            else
                throw new InvalidDataException("Invalid refund/top-up leg.");

            if (EvidenceSha256 != rechecked.EvidenceSha256)
                throw new InvalidDataException("D04 signed amount or source has drifted.");
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("Original D04 deposit evidence is inconsistent.", e);
        }
    }

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), TargetContractId.ToString("D"),
            FinancialPacketId.ToString("D"), FinancialPacketSha256,
            ((int)Leg).ToString(CultureInfo.InvariantCulture),
            ExactAmountVnd.ToString("0", CultureInfo.InvariantCulture),
            OriginalPaymentId?.ToString("D") ?? "",
            ApprovedByOwnerId.ToString("D"),
            IndependentBankReviewerId.ToString("D"),
            ApprovedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            PaidAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ReviewedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            TransactionReference, OwnerApprovedDestinationReference,
            BankStatementReference, SignedBankReviewReference
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static DateTime Normalize(DateTime time)
        => time.AddTicks(-(time.Ticks % 10));

    // A manual bank statement review is not a live bank/gateway confirmation.
    // Separate unique transaction journals and DB-level locking are still
    // required to actually carry, collect or refund funds exactly once.
    public bool IsBankGatewayVerified => false;
    public bool IsCashLedgerPosted => false;
    public bool CanExecuteTransfer => false;
    public bool CanExecuteBankTransfer => false;
}
