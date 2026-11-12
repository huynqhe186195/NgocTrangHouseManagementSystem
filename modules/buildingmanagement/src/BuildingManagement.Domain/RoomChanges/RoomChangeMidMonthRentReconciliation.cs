using System;
using System.Linq;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H2/H3 transfer-only, READ-ONLY reconciliation of the already existing
/// daily quote against the SOURCE rent line of an issued monthly invoice.
/// It must not use the full invoice total, which includes utilities/services.
/// A reference string or a claimed reviewed amount is NOT authentication:
/// the caller must first verify original invoices, approved adjustments and
/// independently reviewed rent allocations from persisted source records.
/// This object never issues a credit, invoice, debt or transfer.
/// </summary>
public static class RoomChangeMidMonthRentReconciliation
{
    public static RoomChangeMidMonthRentPreview Assess(
        RoomChangeDailyRentQuote? quote,
        RoomChangeSourceRentSnapshot? source)
    {
        if (quote is null || source is null)
            throw new InvalidOperationException("Signed rent quote and source ledger evidence are required.");

        // The public quote record can be constructed without the calculator.
        // Recalculate every output before trusting a quote in any projection.
        RoomChangeDailyRentQuote calculated;
        try
        {
            calculated = RoomChangeDailyRentQuoteCalculator.Calculate(
                quote.TransferDate, quote.SourceFullMonthlyRentVnd,
                quote.TargetFullMonthlyRentVnd, quote.TransferDayOwner,
                quote.Rounding, quote.SignedRuleReference);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("The transfer rent quote is invalid.", ex);
        }

        if (quote != calculated ||
            source.SourceContractId == Guid.Empty ||
            source.BillingYear != quote.TransferDate.Year ||
            source.BillingMonth != quote.TransferDate.Month ||
            !WholeNonnegative(source.OriginalIssuedRentLineVnd) ||
            !WholeNonnegative(source.ApprovedRentCorrectionTotalVnd, true) ||
            !WholeNonnegative(source.ReviewedRentReceiptsVnd))
            throw new InvalidOperationException(
                "Monthly rent evidence is outside the signed transfer period or has invalid VND amounts.");

        if (source.IssuedMonthlyInvoiceId.HasValue)
        {
            if (source.IssuedMonthlyInvoiceId.Value == Guid.Empty ||
                !Sha256Reference(source.FrozenInvoiceEvidenceSha256))
                throw new InvalidOperationException(
                    "An issued source rent line requires an original immutable invoice fingerprint.");
        }
        else if (source.OriginalIssuedRentLineVnd != 0m ||
                 source.ApprovedRentCorrectionTotalVnd != 0m ||
                 source.ReviewedRentReceiptsVnd != 0m ||
                 source.FrozenInvoiceEvidenceSha256 is not null ||
                 source.ReceiptAllocationEvidenceReference is not null)
        {
            throw new InvalidOperationException(
                "An unissued rent line cannot claim a receipt, prior charge or correction.");
        }

        if (source.ReviewedRentReceiptsVnd > 0m &&
            !Reference(source.ReceiptAllocationEvidenceReference))
            throw new InvalidOperationException(
                "Receipt allocations must have their independent evidence reference.");

        if (source.ReviewedRentReceiptsVnd == 0m &&
            source.ReceiptAllocationEvidenceReference is not null)
            throw new InvalidOperationException(
                "A zero-receipt source must not invent a receipt allocation reference.");

        decimal recognized;
        decimal delta;
        try
        {
            recognized = checked(source.OriginalIssuedRentLineVnd +
                                 source.ApprovedRentCorrectionTotalVnd);
            delta = checked(quote.ProposedSourceRentVnd - recognized);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Source rent arithmetic exceeds decimal bounds.", ex);
        }

        if (recognized < 0m || source.ReviewedRentReceiptsVnd > recognized)
            throw new InvalidOperationException(
                "Source approved balance is negative or below money already received.");

        var overCollected = Math.Max(0m,
            source.ReviewedRentReceiptsVnd - quote.ProposedSourceRentVnd);
        var remaining = Math.Max(0m,
            quote.ProposedSourceRentVnd - source.ReviewedRentReceiptsVnd);

        return new RoomChangeMidMonthRentPreview(
            quote.TransferDate,
            source.SourceContractId,
            source.IssuedMonthlyInvoiceId,
            recognized,
            source.ReviewedRentReceiptsVnd,
            quote.ProposedSourceRentVnd,
            quote.ProposedTargetRentVnd,
            delta,
            remaining,
            overCollected,
            source.IssuedMonthlyInvoiceId.HasValue && delta != 0m,
            overCollected > 0m,
            quote.SignedRuleReference);
    }

    private static bool WholeNonnegative(decimal value, bool signed = false)
        => (signed || value >= 0m) && decimal.Truncate(value) == value;

    private static bool Reference(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Trim().Length is >= 12 and <= 500;

    private static bool Sha256Reference(string? hash)
        => hash is { Length: 64 } &&
            hash.All(c => (c >= '0' && c <= '9') ||
                          (c >= 'a' && c <= 'f'));
}

public sealed record RoomChangeSourceRentSnapshot(
    Guid SourceContractId,
    int BillingYear,
    int BillingMonth,
    Guid? IssuedMonthlyInvoiceId,
    decimal OriginalIssuedRentLineVnd,
    decimal ApprovedRentCorrectionTotalVnd,
    decimal ReviewedRentReceiptsVnd,
    string? FrozenInvoiceEvidenceSha256,
    string? ReceiptAllocationEvidenceReference);

/// <summary>
/// PREVIEW ONLY. A negative correction requires an append-only, independently
/// reviewed credit proposal, not mutation of an issued invoice. Overcollection
/// requires separate evidence and refund handling, not an automatic payout.
/// Even a zero delta does not approve transfer or invoice posting.
/// </summary>
public sealed record RoomChangeMidMonthRentPreview(
    DateOnly TransferDate,
    Guid SourceContractId,
    Guid? OriginalIssuedInvoiceId,
    decimal PreviouslyRecognizedSourceRentVnd,
    decimal AlreadyReviewedSourceRentReceiptsVnd,
    decimal ProposedSourceRentVnd,
    decimal ProposedTargetRentVnd,
    decimal ProposedSourceRentCorrectionVnd,
    decimal RemainingProposedSourceRentVnd,
    decimal PotentialSourceOvercollectionVnd,
    bool RequiresAppendOnlyIssuedInvoiceCorrection,
    bool RequiresSeparateOvercollectionReview,
    string SignedPricingRuleReference)
{
    public bool CanPostInvoice => false;
    public bool CanExecuteRoomChange => false;
    public bool IsVerifiedBankPayment => false;
}
