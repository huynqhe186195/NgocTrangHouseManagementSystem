using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// D01/D02: immutable, auditable per-request pricing choice, anchored to
/// one approved OI-07 policy, the active source and the entire tenant roster.
/// A pricing reference is a document pointer, NOT a verified signature.
/// This candidate cannot post debt, invoice, money or complete a room move.
/// </summary>
public sealed class RoomChangeDayPricingRuleCandidate : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid TargetRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid RosterSnapshotId { get; private set; }
    public Guid PolicyId { get; private set; }
    public Guid PolicyReviewId { get; private set; }
    public DateOnly TransferDate { get; private set; }
    public RoomChangeTransferDayChargeOwner TransferDayOwner { get; private set; }
    public RoomChangeRentRoundingRule Rounding { get; private set; }
    public int CalendarDays { get; private set; }
    public int SourceChargeableDays { get; private set; }
    public int TargetChargeableDays { get; private set; }
    public decimal SourceMonthlyRentVnd { get; private set; }
    public decimal TargetMonthlyRentVnd { get; private set; }
    public decimal ProposedSourceRentVnd { get; private set; }
    public decimal ProposedTargetRentVnd { get; private set; }
    public string SignedPricingRuleReference { get; private set; } = string.Empty;
    public Guid PreparedByUserId { get; private set; }
    public DateTime PreparedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeDayPricingRuleCandidate() { }
    private RoomChangeDayPricingRuleCandidate(Guid id) : base(id) { }

    public static RoomChangeDayPricingRuleCandidate Prepare(
        Guid id,
        RoomChangeRequest request,
        Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review,
        RoomChangeDailyRentQuote quote,
        Guid preparer,
        DateTime preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(quote);

        try
        {
            policy.Verify(request, roster);
            review.Verify(policy, request, roster);
        }
        catch (Exception e) when (e is InvalidDataException or
            InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Pricing proposal source evidence is invalid.", e);
        }

        if (id == Guid.Empty || preparer == Guid.Empty ||
            preparedAtUtc.Kind != DateTimeKind.Utc ||
            preparedAtUtc < request.CreatedAtUtc ||
            preparedAtUtc < review.ReviewedAtUtc ||
            !review.Accepted ||
            preparer == review.ReviewedByUserId ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            source.Id != request.SourceContractId ||
            source.RoomId != request.SourceRoomId ||
            source.Status != ContractStatus.Active ||
            request.SourceRoomId == request.TargetRoomId ||
            request.RequestedDate != quote.TransferDate ||
            DateOnly.FromDateTime(source.StartDate) > quote.TransferDate ||
            (source.EndDate.HasValue &&
             DateOnly.FromDateTime(source.EndDate.Value) < quote.TransferDate) ||
            policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
            policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
            policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
            policy.PricingPolicyReference != quote.SignedRuleReference ||
            source.MonthlyRent != quote.SourceFullMonthlyRentVnd)
            throw new InvalidOperationException(
                "An accepted, independently reviewed, source-scoped day-proration choice is required.");

        RoomChangeDailyRentQuote expected;
        try
        {
            expected = RoomChangeDailyRentQuoteCalculator.Calculate(
                quote.TransferDate, quote.SourceFullMonthlyRentVnd,
                quote.TargetFullMonthlyRentVnd, quote.TransferDayOwner,
                quote.Rounding, quote.SignedRuleReference);
        }
        catch (Exception e) when (e is InvalidOperationException or OverflowException)
        {
            throw new InvalidOperationException("Pricing choice failed arithmetic validation.", e);
        }

        if (quote != expected || quote.CalendarDaysInMonth !=
                quote.SourceChargeableDays + quote.TargetChargeableDays)
            throw new InvalidOperationException(
                "Proposed transfer quote is not canonical or does not own each calendar day once.");

        var normalized = preparedAtUtc.AddTicks(-(preparedAtUtc.Ticks % 10L));
        var row = new RoomChangeDayPricingRuleCandidate(id)
        {
            RequestId = request.Id,
            SourceContractId = source.Id,
            SourceRoomId = request.SourceRoomId,
            TargetRoomId = request.TargetRoomId,
            BuildingId = request.BuildingId,
            RosterSnapshotId = roster.Id,
            PolicyId = policy.Id,
            PolicyReviewId = review.Id,
            TransferDate = quote.TransferDate,
            TransferDayOwner = quote.TransferDayOwner,
            Rounding = quote.Rounding,
            CalendarDays = quote.CalendarDaysInMonth,
            SourceChargeableDays = quote.SourceChargeableDays,
            TargetChargeableDays = quote.TargetChargeableDays,
            SourceMonthlyRentVnd = quote.SourceFullMonthlyRentVnd,
            TargetMonthlyRentVnd = quote.TargetFullMonthlyRentVnd,
            ProposedSourceRentVnd = quote.ProposedSourceRentVnd,
            ProposedTargetRentVnd = quote.ProposedTargetRentVnd,
            SignedPricingRuleReference = quote.SignedRuleReference,
            PreparedByUserId = preparer,
            PreparedAtUtc = normalized
        };
        row.EvidenceSha256 = row.CalculateHash();
        return row;
    }

    public void Verify(
        RoomChangeRequest request,
        Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review)
    {
        if (EvidenceSha256 != CalculateHash())
            throw new InvalidDataException("Saved day-proration decision fingerprint is invalid.");

        try
        {
            var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
                TransferDate, SourceMonthlyRentVnd, TargetMonthlyRentVnd,
                TransferDayOwner, Rounding, SignedPricingRuleReference);
            var actual = Prepare(Id, request, source, roster, policy, review,
                quote, PreparedByUserId, PreparedAtUtc);
            if (EvidenceSha256 != actual.EvidenceSha256 ||
                RequestId != actual.RequestId ||
                SourceContractId != actual.SourceContractId ||
                SourceRoomId != actual.SourceRoomId ||
                TargetRoomId != actual.TargetRoomId ||
                BuildingId != actual.BuildingId ||
                RosterSnapshotId != actual.RosterSnapshotId ||
                PolicyId != actual.PolicyId ||
                PolicyReviewId != actual.PolicyReviewId ||
                CalendarDays != actual.CalendarDays ||
                SourceChargeableDays != actual.SourceChargeableDays ||
                TargetChargeableDays != actual.TargetChargeableDays ||
                ProposedSourceRentVnd != actual.ProposedSourceRentVnd ||
                ProposedTargetRentVnd != actual.ProposedTargetRentVnd)
                throw new InvalidDataException(
                    "Stored pricing decision is detached from approved source and roster.");
        }
        catch (Exception e) when (e is InvalidOperationException or
            OverflowException or ArgumentException)
        {
            throw new InvalidDataException(
                "Stored pricing candidate is no longer valid for its approved request.", e);
        }
    }

    private string CalculateHash()
    {
        var invariant = CultureInfo.InvariantCulture;
        var serialized = string.Join("\n", new[]
        {
            Id.ToString("D"),
            RequestId.ToString("D"),
            SourceContractId.ToString("D"),
            SourceRoomId.ToString("D"),
            TargetRoomId.ToString("D"),
            BuildingId.ToString("D"),
            RosterSnapshotId.ToString("D"),
            PolicyId.ToString("D"),
            PolicyReviewId.ToString("D"),
            TransferDate.ToString("yyyy-MM-dd", invariant),
            ((int)TransferDayOwner).ToString(invariant),
            ((int)Rounding).ToString(invariant),
            CalendarDays.ToString(invariant),
            SourceChargeableDays.ToString(invariant),
            TargetChargeableDays.ToString(invariant),
            SourceMonthlyRentVnd.ToString("0", invariant),
            TargetMonthlyRentVnd.ToString("0", invariant),
            ProposedSourceRentVnd.ToString("0", invariant),
            ProposedTargetRentVnd.ToString("0", invariant),
            SignedPricingRuleReference,
            PreparedByUserId.ToString("D"),
            PreparedAtUtc.ToString("O", invariant)
        });
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
    }

    public bool CanPostIssuedInvoiceAdjustment => false;
    public bool CanExecuteRoomChange => false;
    public bool IsOwnerGlobalPricingApproval => false;
}
