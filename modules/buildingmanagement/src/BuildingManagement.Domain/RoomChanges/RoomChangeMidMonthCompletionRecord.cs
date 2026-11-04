using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.Rooms;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H1/H2/H3/H5/H6 separate, immutable same-deposit MID-MONTH completion.
/// A single transaction must post the adjusted source receivable, original
/// held-deposit carry and contract/tenant/room transitions. D04 refund/top-up
/// lanes deliberately require separate approved financial executors.
/// The target first-month rent is frozen to prevent a later full-month bill.
/// </summary>
public sealed class RoomChangeMidMonthCompletionRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid TargetContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid TargetRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid PolicyId { get; private set; }
    public Guid PolicyReviewId { get; private set; }
    public Guid RosterSnapshotId { get; private set; }
    public Guid HandoverEvidenceId { get; private set; }
    public Guid PricingArchiveId { get; private set; }
    public Guid ThreePartyArchiveId { get; private set; }
    public Guid PricingRuleId { get; private set; }
    public Guid SourceMonthlyInvoiceId { get; private set; }
    public Guid SourceReceivableId { get; private set; }
    public decimal SourceSignedAdjustmentVnd { get; private set; }
    public decimal SourceAdjustedReceivableVnd { get; private set; }
    public decimal SourceReviewedReceiptsVnd { get; private set; }
    public decimal TargetFirstMonthRentVnd { get; private set; }
    public Guid CarryForwardSettlementId { get; private set; }
    public decimal CarriedDepositVnd { get; private set; }
    public RoomStatus SourceRoomDisposition { get; private set; }
    public string DispositionEvidenceReference { get; private set; } = string.Empty;
    public string CompletionReference { get; private set; } = string.Empty;
    public Guid CompletedByUserId { get; private set; }
    public DateTime CompletedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    public bool IsExternalBankPayment => false;
    public bool SupportsRefundOrTopUp => false;

    private RoomChangeMidMonthCompletionRecord() { }
    private RoomChangeMidMonthCompletionRecord(Guid id) : base(id) { }

    public static RoomChangeMidMonthCompletionRecord Complete(
        Guid id, RoomChangeRequest request, Contract source, Contract target,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review,
        RoomChangeHandoverEvidenceRecord handover,
        RoomChangeDayPricingRuleCandidate rule,
        RoomChangeEvidenceArchiveRecord exactPricing,
        RoomChangeEvidenceArchiveRecord signedHandover,
        MonthlyIssuedInvoiceRecord sourceInvoice,
        OfficialInvoiceReceivablePostingRecord sourceReceivable,
        decimal originalSourceRentVnd,
        decimal sourceSignedAdjustmentVnd,
        decimal sourceReviewedReceiptsVnd,
        DepositSettlement carry,
        RoomStatus sourceDisposition,
        string? dispositionEvidence,
        string? completionReference,
        Guid actor, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(handover);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(exactPricing);
        ArgumentNullException.ThrowIfNull(signedHandover);
        ArgumentNullException.ThrowIfNull(sourceInvoice);
        ArgumentNullException.ThrowIfNull(sourceReceivable);
        ArgumentNullException.ThrowIfNull(carry);

        policy.Verify(request, roster);
        review.Verify(policy, request, roster);
        // The source is already Ended within the transaction here, whereas
        // the signed rule was verified against ACTIVE source immediately
        // before the transition. Recalculate its exact tariff independently;
        // the SQL deferred gate verifies its immutable original digest.
        var canonical = RoomChangeDailyRentQuoteCalculator.Calculate(
            rule.TransferDate, rule.SourceMonthlyRentVnd,
            rule.TargetMonthlyRentVnd, rule.TransferDayOwner,
            rule.Rounding, rule.SignedPricingRuleReference);
        // Historical signed source-day/truncation candidates remain archived,
        // but cannot authorize a NEW transfer under the binding D01/D02 rule.
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(canonical);
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableRule(
            rule, request.RequestedDate);
        exactPricing.VerifyStoredSnapshot();
        signedHandover.VerifyStoredSnapshot();

        var reference = completionReference?.Trim() ?? string.Empty;
        var disposition = dispositionEvidence?.Trim() ?? string.Empty;
        var targetFirstDay = rule.SourceChargeableDays + 1;
        var targetStart = new DateOnly(request.RequestedDate.Year,
            request.RequestedDate.Month, targetFirstDay);
        var adjustedInvoice = checked(
            sourceReceivable.OriginalAmount + sourceSignedAdjustmentVnd);
        var expectedDelta = checked(rule.ProposedSourceRentVnd -
            originalSourceRentVnd);

        if (id == Guid.Empty || actor == Guid.Empty ||
            atUtc.Kind != DateTimeKind.Utc || atUtc < request.CreatedAtUtc ||
            atUtc < review.ReviewedAtUtc || atUtc < handover.AttestedAtUtc ||
            atUtc < exactPricing.ArchivedAtUtc ||
            atUtc < signedHandover.ArchivedAtUtc ||
            reference.Length is < 12 or > 500 ||
            disposition.Length is < 12 or > 500 ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            !review.Accepted ||
            policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
            policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
            policy.DepositTreatment != RoomChangeDepositTreatment.CarryAllWithNoDifference ||
            policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
            request.RequestedDate.Day == DateTime.DaysInMonth(
                request.RequestedDate.Year, request.RequestedDate.Month) ||
            rule.SourceChargeableDays <= 0 || rule.TargetChargeableDays <= 0 ||
            rule.CalendarDays != rule.SourceChargeableDays + rule.TargetChargeableDays ||
            canonical.ProposedSourceRentVnd != rule.ProposedSourceRentVnd ||
            canonical.ProposedTargetRentVnd != rule.ProposedTargetRentVnd ||
            canonical.SourceChargeableDays != rule.SourceChargeableDays ||
            canonical.TargetChargeableDays != rule.TargetChargeableDays ||
            rule.TransferDate != request.RequestedDate ||
            rule.PolicyId != policy.Id || rule.PolicyReviewId != review.Id ||
            rule.RosterSnapshotId != roster.Id ||
            request.RequestedDate > DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(atUtc,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))) ||
            source.Id != request.SourceContractId ||
            source.RoomId != request.SourceRoomId ||
            source.Status != ContractStatus.Ended ||
            source.EndReason != ContractEndReason.RoomChanged ||
            source.ActualEndedAt is null ||
            target.Id == Guid.Empty || target.Id == source.Id ||
            target.Status != ContractStatus.Active ||
            target.RenewedFromContractId != source.Id ||
            target.RoomId != request.TargetRoomId ||
            DateOnly.FromDateTime(target.StartDate) != targetStart ||
            target.MonthlyRent != rule.TargetMonthlyRentVnd ||
            source.MonthlyRent != rule.SourceMonthlyRentVnd ||
            source.DepositAmount != target.DepositAmount ||
            source.DepositAmount <= 0 ||
            source.MonthlyRent != target.MonthlyRent ||
            exactPricing.Kind != RoomChangeEvidenceArchiveKind.SignedExactPricing ||
            exactPricing.RequestId != request.Id ||
            exactPricing.SourceContractId != source.Id ||
            exactPricing.BuildingId != request.BuildingId ||
            exactPricing.ParentEvidenceId != rule.Id ||
            exactPricing.ParentEvidenceSha256 != rule.EvidenceSha256 ||
            signedHandover.Kind != RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover ||
            signedHandover.RequestId != request.Id ||
            signedHandover.SourceContractId != source.Id ||
            signedHandover.BuildingId != request.BuildingId ||
            signedHandover.ParentEvidenceId != handover.Id ||
            signedHandover.ParentEvidenceSha256 != handover.EvidenceSha256 ||
            handover.SourceContractId != source.Id ||
            handover.RequestId != request.Id ||
            handover.HandoverDate != request.RequestedDate ||
            sourceInvoice.ContractId != source.Id ||
            sourceInvoice.RoomId != source.RoomId ||
            sourceInvoice.BuildingId != request.BuildingId ||
            sourceInvoice.BillingYear != request.RequestedDate.Year ||
            sourceInvoice.BillingMonth != request.RequestedDate.Month ||
            sourceReceivable.Kind != OfficialInvoiceKind.Monthly ||
            sourceReceivable.IssuedInvoiceId != sourceInvoice.Id ||
            sourceReceivable.ContractId != source.Id ||
            sourceReceivable.BuildingId != request.BuildingId ||
            sourceReceivable.SourceEvidenceSha256 != sourceInvoice.CandidateSha256 ||
            sourceReceivable.OriginalAmount != sourceInvoice.TotalAmount ||
            originalSourceRentVnd <= 0m ||
            decimal.Truncate(originalSourceRentVnd) != originalSourceRentVnd ||
            expectedDelta != sourceSignedAdjustmentVnd ||
            decimal.Truncate(sourceSignedAdjustmentVnd) != sourceSignedAdjustmentVnd ||
            adjustedInvoice <= 0m ||
            adjustedInvoice != sourceReviewedReceiptsVnd ||
            decimal.Truncate(sourceReviewedReceiptsVnd) != sourceReviewedReceiptsVnd ||
            carry.Type != DepositSettlementType.RoomChangeCarryForward ||
            carry.Status != DepositSettlementStatus.Completed ||
            carry.SourceContractId != source.Id ||
            carry.TargetContractId != target.Id ||
            carry.Amount != source.DepositAmount ||
            carry.CompletedAt is null ||
            sourceDisposition is not
                (RoomStatus.Available or RoomStatus.Reserved or RoomStatus.Maintenance))
            throw new InvalidOperationException(
                "Mid-month transfer requires exact signed pricing, paid corrected receivable, full cash-backed deposit and atomic linked contracts.");

        var row = new RoomChangeMidMonthCompletionRecord(id)
        {
            RequestId = request.Id,
            SourceContractId = source.Id,
            TargetContractId = target.Id,
            SourceRoomId = source.RoomId,
            TargetRoomId = target.RoomId,
            BuildingId = request.BuildingId,
            PolicyId = policy.Id,
            PolicyReviewId = review.Id,
            RosterSnapshotId = roster.Id,
            HandoverEvidenceId = handover.Id,
            PricingArchiveId = exactPricing.Id,
            ThreePartyArchiveId = signedHandover.Id,
            PricingRuleId = rule.Id,
            SourceMonthlyInvoiceId = sourceInvoice.Id,
            SourceReceivableId = sourceReceivable.Id,
            SourceSignedAdjustmentVnd = sourceSignedAdjustmentVnd,
            SourceAdjustedReceivableVnd = adjustedInvoice,
            SourceReviewedReceiptsVnd = sourceReviewedReceiptsVnd,
            TargetFirstMonthRentVnd = rule.ProposedTargetRentVnd,
            CarryForwardSettlementId = carry.Id,
            CarriedDepositVnd = carry.Amount,
            SourceRoomDisposition = sourceDisposition,
            DispositionEvidenceReference = disposition,
            CompletionReference = reference,
            CompletedByUserId = actor,
            CompletedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10L))
        };
        row.EvidenceSha256 = row.Hash();
        return row;
    }

    public void VerifyFingerprint()
    {
        if (Id == Guid.Empty || RequestId == Guid.Empty ||
            SourceContractId == Guid.Empty || TargetContractId == Guid.Empty ||
            SourceContractId == TargetContractId ||
            SourceRoomId == Guid.Empty || TargetRoomId == Guid.Empty ||
            SourceRoomId == TargetRoomId ||
            PricingArchiveId == Guid.Empty || ThreePartyArchiveId == Guid.Empty ||
            PricingRuleId == Guid.Empty || SourceReceivableId == Guid.Empty ||
            SourceMonthlyInvoiceId == Guid.Empty ||
            CarriedDepositVnd <= 0m || TargetFirstMonthRentVnd <= 0m ||
            SourceAdjustedReceivableVnd <= 0m ||
            SourceAdjustedReceivableVnd != SourceReviewedReceiptsVnd ||
            CompletedAtUtc.Kind != DateTimeKind.Utc ||
            DispositionEvidenceReference.Length is < 12 or > 500 ||
            CompletionReference.Length is < 12 or > 500 ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("Mid-month completion snapshot fingerprint is inconsistent.");
    }

    private string Hash()
    {
        var invariant = CultureInfo.InvariantCulture;
        var parts = new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), TargetContractId.ToString("D"),
            SourceRoomId.ToString("D"), TargetRoomId.ToString("D"),
            BuildingId.ToString("D"), PolicyId.ToString("D"),
            PolicyReviewId.ToString("D"), RosterSnapshotId.ToString("D"),
            HandoverEvidenceId.ToString("D"), PricingArchiveId.ToString("D"),
            ThreePartyArchiveId.ToString("D"), PricingRuleId.ToString("D"),
            SourceMonthlyInvoiceId.ToString("D"), SourceReceivableId.ToString("D"),
            SourceSignedAdjustmentVnd.ToString("0", invariant),
            SourceAdjustedReceivableVnd.ToString("0", invariant),
            SourceReviewedReceiptsVnd.ToString("0", invariant),
            TargetFirstMonthRentVnd.ToString("0", invariant),
            CarryForwardSettlementId.ToString("D"),
            CarriedDepositVnd.ToString("0", invariant),
            ((int)SourceRoomDisposition).ToString(invariant),
            DispositionEvidenceReference, CompletionReference,
            CompletedByUserId.ToString("D"),
            CompletedAtUtc.ToString("O", invariant)
        };
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))
            .ToLowerInvariant();
    }
}
