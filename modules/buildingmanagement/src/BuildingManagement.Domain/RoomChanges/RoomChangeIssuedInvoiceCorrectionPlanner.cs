using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Invoices;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// One rent-line correction against an immutable issued monthly invoice.
/// The original invoice, its meter evidence and its receipts never change:
/// the correction exists only as an append-only adjustment proposal.
/// </summary>
public sealed record RoomChangeIssuedInvoiceCorrectionLine(
    Guid IssuedInvoiceId,
    Guid ContractId,
    Guid BuildingId,
    int BillingYear,
    int BillingMonth,
    decimal OriginalRentLineVnd,
    decimal CapturedReceiptsVnd,
    decimal ProposedRentLineVnd,
    Guid AdjustmentId,
    decimal SignedDeltaVnd)
{
    public bool RequiresCorrection => SignedDeltaVnd != 0m;
    public bool IsCredit => SignedDeltaVnd < 0m;
    public bool IsDebit => SignedDeltaVnd > 0m;
}

/// <summary>
/// READ-ONLY plan: the exact append-only corrections an approved D01/D02
/// transfer implies for the source and target issued invoices of one calendar
/// month. Nothing here issues an invoice, posts a receivable, moves money or
/// authorizes the transfer. Each correction still needs independent approval
/// via <see cref="MonthlyInvoiceAdjustmentDecisionRecord"/>, and any official
/// posting remains a separate, explicitly gated step.
/// </summary>
public sealed record RoomChangeIssuedInvoiceCorrectionPlan(
    DateOnly TransferDate,
    int CalendarDaysInMonth,
    int SourceChargeableDays,
    int TargetChargeableDays,
    decimal ProposedCombinedRentVnd,
    RoomChangeIssuedInvoiceCorrectionLine Source,
    RoomChangeIssuedInvoiceCorrectionLine? Target,
    string SignedRuleReference)
{
    public bool HasAnyCorrection =>
        Source.RequiresCorrection || (Target?.RequiresCorrection ?? false);

    public decimal NetDeltaVnd =>
        checked(Source.SignedDeltaVnd + (Target?.SignedDeltaVnd ?? 0m));

    public bool CanIssueInvoice => false;
    public bool CanPostOfficialReceivable => false;
    public bool CanExecuteRoomChange => false;
}

/// <summary>
/// Binds the approved D01/D02 daily-rent quote to the REAL issued monthly
/// invoices of the transfer month and derives the ordered, append-only
/// adjustment proposals. This closes the gap between the domain calculator
/// (which cannot issue anything) and the issued-invoice correction primitive.
///
/// The quote is always recalculated from its own inputs before being trusted,
/// so a hand-constructed record cannot smuggle in unapproved arithmetic.
/// </summary>
public static class RoomChangeIssuedInvoiceCorrectionPlanner
{
    public static RoomChangeIssuedInvoiceCorrectionPlan Plan(
        RoomChangeDailyRentQuote? candidate,
        MonthlyIssuedInvoiceRecord sourceInvoice,
        decimal sourceIssuedRentLineVnd,
        decimal sourceCapturedReceiptsVnd,
        MonthlyIssuedInvoiceRecord? targetInvoice,
        decimal targetIssuedRentLineVnd,
        decimal targetCapturedReceiptsVnd,
        string? reason,
        string? sourceEvidenceReference,
        Guid proposerId,
        DateTime proposedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceInvoice);
        if (candidate is null)
            throw new InvalidOperationException(
                "A signed D01/D02 rent quote is required before correcting an issued invoice.");

        // Never trust the public quote record: recompute every output first.
        RoomChangeDailyRentQuote quote;
        try
        {
            quote = RoomChangeDailyRentQuoteCalculator.Calculate(
                candidate.TransferDate, candidate.SourceFullMonthlyRentVnd,
                candidate.TargetFullMonthlyRentVnd, candidate.TransferDayOwner,
                candidate.Rounding, candidate.SignedRuleReference);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("The D01/D02 transfer quote is invalid.", ex);
        }

        if (candidate != quote)
            throw new InvalidOperationException(
                "The transfer quote has been altered from its signed calculation.");

        var why = reason?.Trim() ?? string.Empty;
        var evidence = sourceEvidenceReference?.Trim() ?? string.Empty;
        if (proposerId == Guid.Empty ||
            proposedAtUtc.Kind != DateTimeKind.Utc ||
            why.Length is < 12 or > 500 ||
            evidence.Length is < 8 or > 200)
            throw new InvalidOperationException(
                "A correction plan needs a proposer, an explicit reason and source evidence.");

        RequireSameBillingPeriod(quote, sourceInvoice, "source");
        RequireWholeVnd(sourceIssuedRentLineVnd, "source issued rent line");
        RequireWholeVnd(sourceCapturedReceiptsVnd, "source captured receipts");
        if (sourceCapturedReceiptsVnd > sourceIssuedRentLineVnd)
            throw new InvalidOperationException(
                "Source captured receipts cannot exceed the issued source rent line.");

        var sourceProposed = quote.ProposedSourceRentVnd;
        if (sourceProposed < sourceCapturedReceiptsVnd)
            throw new InvalidOperationException(
                "The D01/D02 source rent would fall below money already received; " +
                "an overcollection refund is a separate reviewed process.");

        var sourceLine = Line(
            quote, sourceInvoice, sourceIssuedRentLineVnd,
            sourceCapturedReceiptsVnd, sourceProposed,
            why, evidence, proposerId, proposedAtUtc);

        RoomChangeIssuedInvoiceCorrectionLine? targetLine = null;
        if (quote.TargetChargeableDays > 0)
        {
            if (targetInvoice is null)
                throw new InvalidOperationException(
                    "The target room has chargeable days but no issued target invoice was supplied.");

            if (targetInvoice.Id == sourceInvoice.Id)
                throw new InvalidOperationException(
                    "Source and target corrections must reference different issued invoices.");

            RequireSameBillingPeriod(quote, targetInvoice, "target");
            RequireWholeVnd(targetIssuedRentLineVnd, "target issued rent line");
            RequireWholeVnd(targetCapturedReceiptsVnd, "target captured receipts");
            if (targetCapturedReceiptsVnd > targetIssuedRentLineVnd)
                throw new InvalidOperationException(
                    "Target captured receipts cannot exceed the issued target rent line.");

            var targetProposed = quote.ProposedTargetRentVnd;
            if (targetProposed < targetCapturedReceiptsVnd)
                throw new InvalidOperationException(
                    "The D01/D02 target rent would fall below money already received; " +
                    "an overcollection refund is a separate reviewed process.");

            targetLine = Line(
                quote, targetInvoice, targetIssuedRentLineVnd,
                targetCapturedReceiptsVnd, targetProposed,
                why, evidence, proposerId, proposedAtUtc);
        }
        else if (targetInvoice is not null || targetIssuedRentLineVnd != 0m ||
                 targetCapturedReceiptsVnd != 0m)
        {
            throw new InvalidOperationException(
                "A transfer with no chargeable target days must not carry target invoice evidence.");
        }

        return new RoomChangeIssuedInvoiceCorrectionPlan(
            quote.TransferDate, quote.CalendarDaysInMonth,
            quote.SourceChargeableDays, quote.TargetChargeableDays,
            quote.ProposedCombinedRentVnd, sourceLine, targetLine,
            quote.SignedRuleReference);
    }

    /// <summary>
    /// Materializes the append-only proposals of a plan. Deterministic ids make
    /// a replayed transfer yield byte-identical proposals instead of a second
    /// financial correction.
    /// </summary>
    public static MonthlyInvoiceAdjustmentRecord[] Materialize(
        RoomChangeIssuedInvoiceCorrectionPlan plan,
        MonthlyIssuedInvoiceRecord sourceInvoice,
        MonthlyIssuedInvoiceRecord? targetInvoice,
        string? reason,
        string? sourceEvidenceReference,
        Guid proposerId,
        DateTime proposedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceInvoice);
        var why = reason?.Trim() ?? string.Empty;
        var evidence = sourceEvidenceReference?.Trim() ?? string.Empty;

        var proposals = new List<MonthlyInvoiceAdjustmentRecord>(2);
        if (plan.Source.RequiresCorrection)
            proposals.Add(Proposal(plan.Source, sourceInvoice,
                why, evidence, proposerId, proposedAtUtc));
        if (plan.Target is not null)
        {
            if (targetInvoice is null)
                throw new InvalidOperationException(
                    "Plan carries a target correction but no target invoice was supplied.");
            if (plan.Target.RequiresCorrection)
                proposals.Add(Proposal(plan.Target, targetInvoice,
                    why, evidence, proposerId, proposedAtUtc));
        }
        return proposals.ToArray();
    }

    /// <summary>
    /// Rejects a plan that no longer matches its issued invoices and receipts,
    /// e.g. after an invoice was reissued or a receipt was captured.
    /// </summary>
    public static void VerifyAgainst(
        RoomChangeIssuedInvoiceCorrectionPlan plan,
        MonthlyIssuedInvoiceRecord sourceInvoice,
        decimal sourceIssuedRentLineVnd,
        decimal sourceCapturedReceiptsVnd,
        MonthlyIssuedInvoiceRecord? targetInvoice,
        decimal targetIssuedRentLineVnd,
        decimal targetCapturedReceiptsVnd)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceInvoice);
        VerifyLine(plan.Source, sourceInvoice, sourceIssuedRentLineVnd,
            sourceCapturedReceiptsVnd, plan.TransferDate);
        if (plan.Target is null)
        {
            if (targetInvoice is not null || targetIssuedRentLineVnd != 0m ||
                targetCapturedReceiptsVnd != 0m)
                throw new InvalidOperationException(
                    "Plan has no target correction but target invoice evidence was supplied.");
            return;
        }
        if (targetInvoice is null)
            throw new InvalidOperationException(
                "Plan has a target correction but no target invoice was supplied.");
        VerifyLine(plan.Target, targetInvoice, targetIssuedRentLineVnd,
            targetCapturedReceiptsVnd, plan.TransferDate);
    }

    private static void VerifyLine(
        RoomChangeIssuedInvoiceCorrectionLine line,
        MonthlyIssuedInvoiceRecord invoice,
        decimal issuedRentLineVnd,
        decimal capturedReceiptsVnd,
        DateOnly transferDate)
    {
        RequireSameBillingPeriod(transferDate, invoice, "issued");
        RequireWholeVnd(issuedRentLineVnd, "issued rent line");
        RequireWholeVnd(capturedReceiptsVnd, "captured receipts");
        if (line.IssuedInvoiceId != invoice.Id ||
            line.ContractId != invoice.ContractId ||
            line.BuildingId != invoice.BuildingId ||
            line.BillingYear != invoice.BillingYear ||
            line.BillingMonth != invoice.BillingMonth ||
            line.OriginalRentLineVnd != issuedRentLineVnd ||
            line.CapturedReceiptsVnd != capturedReceiptsVnd ||
            line.SignedDeltaVnd != line.ProposedRentLineVnd - issuedRentLineVnd ||
            line.ProposedRentLineVnd < capturedReceiptsVnd)
            throw new InvalidOperationException(
                "The invoice correction plan has drifted from its issued invoice or receipts.");
    }

    private static RoomChangeIssuedInvoiceCorrectionLine Line(
        RoomChangeDailyRentQuote quote,
        MonthlyIssuedInvoiceRecord invoice,
        decimal issuedRentLineVnd,
        decimal capturedReceiptsVnd,
        decimal proposedRentLineVnd,
        string reason,
        string evidence,
        Guid proposerId,
        DateTime proposedAtUtc)
    {
        var delta = checked(proposedRentLineVnd - issuedRentLineVnd);
        var adjustmentId = delta == 0m
            ? Guid.Empty
            : DeterministicAdjustmentId(
                invoice.Id, quote.TransferDate, quote.SignedRuleReference, delta);

        if (delta == 0m)
        {
            return new RoomChangeIssuedInvoiceCorrectionLine(
                invoice.Id, invoice.ContractId, invoice.BuildingId,
                invoice.BillingYear, invoice.BillingMonth,
                issuedRentLineVnd, capturedReceiptsVnd, proposedRentLineVnd,
                Guid.Empty, 0m);
        }

        // Build the real proposal so every guard of the append-only primitive
        // (whole VND, reason/evidence lengths, issued-before-proposed) fires here.
        _ = MonthlyInvoiceAdjustmentRecord.Propose(
            adjustmentId, invoice, proposerId, proposedAtUtc, delta,
            reason, evidence);

        return new RoomChangeIssuedInvoiceCorrectionLine(
            invoice.Id, invoice.ContractId, invoice.BuildingId,
            invoice.BillingYear, invoice.BillingMonth,
            issuedRentLineVnd, capturedReceiptsVnd, proposedRentLineVnd,
            adjustmentId, delta);
    }

    private static MonthlyInvoiceAdjustmentRecord Proposal(
        RoomChangeIssuedInvoiceCorrectionLine line,
        MonthlyIssuedInvoiceRecord invoice,
        string reason,
        string evidence,
        Guid proposerId,
        DateTime proposedAtUtc)
    {
        if (line.AdjustmentId == Guid.Empty || line.SignedDeltaVnd == 0m)
            throw new InvalidOperationException(
                "A zero-value line has no append-only proposal to materialize.");
        return MonthlyInvoiceAdjustmentRecord.Propose(
            line.AdjustmentId, invoice, proposerId, proposedAtUtc,
            line.SignedDeltaVnd, reason, evidence);
    }

    private static void RequireSameBillingPeriod(
        RoomChangeDailyRentQuote quote,
        MonthlyIssuedInvoiceRecord invoice,
        string role)
    {
        if (invoice.Id == Guid.Empty || invoice.ContractId == Guid.Empty ||
            invoice.BuildingId == Guid.Empty)
            throw new InvalidOperationException(
                $"The {role} invoice identity is incomplete.");
        if (invoice.BillingYear != quote.TransferDate.Year ||
            invoice.BillingMonth != quote.TransferDate.Month)
            throw new InvalidOperationException(
                $"The {role} invoice was not issued for the transfer's billing month.");
    }

    private static void RequireSameBillingPeriod(
        DateOnly transferDate,
        MonthlyIssuedInvoiceRecord invoice,
        string role)
    {
        if (invoice.BillingYear != transferDate.Year ||
            invoice.BillingMonth != transferDate.Month)
            throw new InvalidOperationException(
                $"The {role} invoice was not issued for the transfer's billing month.");
    }

    private static void RequireWholeVnd(decimal value, string what)
    {
        if (value < 0m || decimal.Truncate(value) != value)
            throw new InvalidOperationException(
                $"The {what} must be a nonnegative whole-VND amount.");
    }

    private static Guid DeterministicAdjustmentId(
        Guid issuedInvoiceId, DateOnly transferDate,
        string signedRuleReference, decimal deltaVnd)
    {
        var canonical = string.Join('|',
            issuedInvoiceId.ToString("N"),
            transferDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            signedRuleReference,
            deltaVnd.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}
