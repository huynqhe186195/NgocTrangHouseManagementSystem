using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// D04 evidence boundary for settling an already committed immutable H4 obligation.
/// Not a bank adapter, receipt, official rent posting or DB writer: an authenticated,
/// SERIALIZABLE executor must re-read every independent source and append exactly one
/// posting with a unique obligation/source reference at COMMIT.
/// </summary>
public enum RoomChangeH4SettlementKind
{
    DepositTopUp = 1,
    NextMonthIssuedRentCredit = 2,
    IndependentBankRefund = 3
}

public sealed record RoomChangeH4VerifiedPayment(
    Guid PaymentId, decimal AmountVnd, string BankOrReceiptReference);

public sealed record RoomChangeH4SettlementEvidence(
    Guid ObligationId,
    Guid SourceContractId,
    Guid TargetContractId,
    RoomChangeH4SettlementKind Kind,
    decimal OriginalAmountVnd,
    DateOnly TransferDateVietnam,
    DateOnly? TopUpDueDateVietnam,
    IReadOnlyCollection<RoomChangeH4VerifiedPayment> PreviouslyPostedPayments,
    IReadOnlyCollection<RoomChangeH4VerifiedPayment> NewlyVerifiedPayments,
    Guid? OfficialIssuedInvoiceId,
    Guid? InvoiceTargetContractId,
    int? InvoiceBillingYear,
    int? InvoiceBillingMonth,
    decimal? IndependentlyVerifiedIssuedRentOutstandingVnd,
    Guid? BankRefundReviewId,
    Guid? IndependentBankReviewerId,
    Guid? PaymentApproverId,
    string? CompletedBankTransferReference);

public sealed record RoomChangeH4SettlementPreflight(
    Guid ObligationId, RoomChangeH4SettlementKind Kind,
    decimal NewlyEligibleAmountVnd, decimal OutstandingAfterVnd,
    bool IsPartialTopUp)
{
    // A validated candidate is never evidence that an actual bank/ledger
    // settlement happened. Posting requires a separate audited writer.
    public bool IsFinancialPosting => false;
    public bool IsExternallySettled => false;
    public bool CanMarkOriginalObligationPaid => false;
}

public static class RoomChangeH4SettlementEvidenceGate
{
    public static RoomChangeH4SettlementPreflight RequireEligible(
        RoomChangeH4SettlementEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ObligationId == Guid.Empty ||
            evidence.SourceContractId == Guid.Empty ||
            evidence.TargetContractId == Guid.Empty ||
            evidence.SourceContractId == evidence.TargetContractId ||
            !Enum.IsDefined(evidence.Kind) ||
            evidence.OriginalAmountVnd <= 0m ||
            decimal.Truncate(evidence.OriginalAmountVnd) != evidence.OriginalAmountVnd ||
            evidence.TransferDateVietnam == default ||
            evidence.PreviouslyPostedPayments is null ||
            evidence.NewlyVerifiedPayments is null)
            throw new InvalidOperationException("Original H4 obligation is invalid.");

        var all = evidence.PreviouslyPostedPayments
            .Concat(evidence.NewlyVerifiedPayments).ToArray();
        if (all.Any(x => x is null || x.PaymentId == Guid.Empty ||
                x.AmountVnd <= 0m || decimal.Truncate(x.AmountVnd) != x.AmountVnd ||
                !Reference(x.BankOrReceiptReference)) ||
            all.Select(x => x.PaymentId).Distinct().Count() != all.Length ||
            all.Select(x => x.BankOrReceiptReference.Trim())
                .Distinct(StringComparer.Ordinal).Count() != all.Length)
            throw new InvalidOperationException(
                "H4 payment sources must be verified, positive and non-replayed.");

        decimal previous;
        decimal newly;
        try
        {
            previous = checked(evidence.PreviouslyPostedPayments.Sum(x => x.AmountVnd));
            newly = checked(evidence.NewlyVerifiedPayments.Sum(x => x.AmountVnd));
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("H4 payment total overflow.", ex);
        }

        if (previous >= evidence.OriginalAmountVnd ||
            newly <= 0m || newly > evidence.OriginalAmountVnd - previous)
            throw new InvalidOperationException(
                "H4 posting must not overpay or replay an already settled obligation.");

        if (evidence.Kind == RoomChangeH4SettlementKind.DepositTopUp)
        {
            if (evidence.TopUpDueDateVietnam is null ||
                evidence.TopUpDueDateVietnam < evidence.TransferDateVietnam ||
                evidence.TopUpDueDateVietnam > evidence.TransferDateVietnam.AddMonths(1) ||
                evidence.OfficialIssuedInvoiceId.HasValue ||
                evidence.BankRefundReviewId.HasValue ||
                evidence.CompletedBankTransferReference is not null)
                throw new InvalidOperationException(
                    "Top-up requires original due date and distinct verified receipts.");
        }
        else if (evidence.Kind == RoomChangeH4SettlementKind.NextMonthIssuedRentCredit)
        {
            var next = evidence.TransferDateVietnam.AddMonths(1);
            if (previous != 0m || newly != evidence.OriginalAmountVnd ||
                evidence.TopUpDueDateVietnam.HasValue ||
                evidence.OfficialIssuedInvoiceId is null or { } id && id == Guid.Empty ||
                evidence.InvoiceTargetContractId != evidence.TargetContractId ||
                evidence.InvoiceBillingYear != next.Year ||
                evidence.InvoiceBillingMonth != next.Month ||
                evidence.IndependentlyVerifiedIssuedRentOutstandingVnd is null ||
                evidence.IndependentlyVerifiedIssuedRentOutstandingVnd < newly ||
                evidence.BankRefundReviewId.HasValue ||
                evidence.CompletedBankTransferReference is not null)
                throw new InvalidOperationException(
                    "Rent credit requires its original next-month issued invoice, sufficient open rent and one full exclusive posting.");
        }
        else
        {
            if (previous != 0m || newly != evidence.OriginalAmountVnd ||
                evidence.TopUpDueDateVietnam.HasValue ||
                evidence.OfficialIssuedInvoiceId.HasValue ||
                evidence.BankRefundReviewId is null || evidence.BankRefundReviewId == Guid.Empty ||
                evidence.IndependentBankReviewerId is null ||
                evidence.IndependentBankReviewerId == Guid.Empty ||
                evidence.PaymentApproverId is null ||
                evidence.PaymentApproverId == Guid.Empty ||
                evidence.IndependentBankReviewerId == evidence.PaymentApproverId ||
                !Reference(evidence.CompletedBankTransferReference))
                throw new InvalidOperationException(
                    "Refund needs an independent reviewer and completed bank transfer evidence.");
        }

        return new RoomChangeH4SettlementPreflight(
            evidence.ObligationId, evidence.Kind, newly,
            evidence.OriginalAmountVnd - previous - newly,
            evidence.Kind == RoomChangeH4SettlementKind.DepositTopUp &&
            previous + newly < evidence.OriginalAmountVnd);
    }

    private static bool Reference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 12 and <= 500;
}
