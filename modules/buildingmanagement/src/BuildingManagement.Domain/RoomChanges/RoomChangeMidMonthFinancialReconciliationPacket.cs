using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H2/H3/H4 PREPARATION ONLY. Binds one independently reviewed OI-07
/// policy, actual source contract, transfer month rent quote/ledger and
/// cash-backed deposit split to the SAME request and proposed new contract.
/// Does not authenticate the caller's ledger data: the adapter must read
/// and verify immutable invoices, receipt allocations and deposit payments
/// under the existing building-scoped locks before invoking this calculator.
/// No invoice, debt, deposit, bank or contract state is mutated here.
/// </summary>
public static class RoomChangeMidMonthFinancialReconciliationPacketFactory
{
    public static RoomChangeMidMonthFinancialReconciliationPacket Prepare(
        Guid packetId,
        RoomChangeRequest? request,
        Contract? source,
        Guid proposedTargetContractId,
        RoomChangeTenantRosterSnapshotRecord? roster,
        RoomChangeBusinessPolicyRecord? policy,
        RoomChangeBusinessPolicyReviewRecord? review,
        RoomChangeDailyRentQuote? quote,
        RoomChangeSourceRentSnapshot? sourceRent,
        RoomChangeDepositDifferenceFacts? depositFacts,
        Guid preparedBy,
        DateTime preparedAtUtc)
    {
        if (request is null || source is null || roster is null ||
            policy is null || review is null || quote is null ||
            sourceRent is null || depositFacts is null)
            throw new InvalidOperationException(
                "The transfer proposal requires source, approved policy, rent and deposit inputs.");

        // A review verifies the ORIGINAL policy/roster; it is not a
        // replacement for a later independent cash/receipt review.
        try
        {
            policy.Verify(request, roster);
            review.Verify(policy, request, roster);
        }
        catch (Exception ex) when (ex is InvalidDataException or
            InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Source, policy, review or roster evidence does not match.", ex);
        }

        if (packetId == Guid.Empty ||
            proposedTargetContractId == Guid.Empty ||
            proposedTargetContractId == source.Id ||
            preparedBy == Guid.Empty ||
            preparedAtUtc.Kind != DateTimeKind.Utc ||
            preparedAtUtc < review.ReviewedAtUtc ||
            preparedAtUtc < request.CreatedAtUtc ||
            !review.Accepted ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            source.Id != request.SourceContractId ||
            source.RoomId != request.SourceRoomId ||
            source.Status != ContractStatus.Active ||
            request.TargetRoomId == source.RoomId ||
            request.RequestedDate != quote.TransferDate ||
            DateOnly.FromDateTime(source.StartDate) > quote.TransferDate ||
            (source.EndDate.HasValue &&
             DateOnly.FromDateTime(source.EndDate.Value) < quote.TransferDate) ||
            policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
            policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
            policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
            !string.Equals(policy.PricingPolicyReference,
                quote.SignedRuleReference, StringComparison.Ordinal) ||
            !string.Equals(policy.DepositPolicyReference,
                depositFacts.TargetRequiredDepositPolicyReference, StringComparison.Ordinal) ||
            quote.SourceFullMonthlyRentVnd != source.MonthlyRent ||
            sourceRent.SourceContractId != source.Id ||
            depositFacts.SourceContractId != source.Id ||
            depositFacts.TargetContractId != proposedTargetContractId ||
            depositFacts.SourceCashBackedHeldDepositVnd != source.DepositAmount ||
            // The current Contract aggregate equates required deposit with
            // monthly rent. Do not assume a decoupled tariff is supported.
            depositFacts.TargetContractRequiredDepositVnd !=
                quote.TargetFullMonthlyRentVnd)
            throw new InvalidOperationException(
                "Cross-contract, date, approved pricing or cash-backed deposit identity mismatch.");

        RoomChangeMidMonthRentPreview rent;
        RoomChangeDepositDifferencePreview deposit;
        try
        {
            rent = RoomChangeMidMonthRentReconciliation.Assess(quote, sourceRent);
            deposit = RoomChangeDepositDifferencePreviewCalculator.Calculate(depositFacts);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Transfer rent/deposit preview evidence is inconsistent.", ex);
        }

        if (policy.DepositTreatment != deposit.ProposedPolicyLane ||
            rent.TransferDate != request.RequestedDate ||
            rent.SourceContractId != source.Id ||
            deposit.SourceContractId != source.Id ||
            deposit.TargetContractId != proposedTargetContractId)
            throw new InvalidOperationException(
                "Independently reviewed OI-07 lane conflicts with the money split.");

        var prepared = new RoomChangeMidMonthFinancialReconciliationPacket(
            packetId, request.Id, request.BuildingId, source.Id,
            request.SourceRoomId, request.TargetRoomId, proposedTargetContractId,
            roster.Id, roster.RosterSha256, policy.Id, policy.EvidenceSha256,
            review.Id, review.ProposalSha256, review.ReviewedAtUtc,
            quote, sourceRent, rent, depositFacts, deposit,
            preparedBy, preparedAtUtc.AddTicks(-(preparedAtUtc.Ticks % 10L)),
            EvidenceSha256: string.Empty);

        return prepared with { EvidenceSha256 = prepared.CalculateFingerprint() };
    }
}

/// <summary>
/// Non-authoritative versioned audit *candidate*; no EF mapping, public
/// writer endpoint or cash posting. The SHA detects accidental alterations
/// to this complete snapshot but does NOT cryptographically authenticate a
/// person's signature, original bank execution or document provenance.
/// VerifyAgainst must reconstruct from original server-read evidence.
/// </summary>
public sealed record RoomChangeMidMonthFinancialReconciliationPacket(
    Guid PacketId,
    Guid RequestId,
    Guid BuildingId,
    Guid SourceContractId,
    Guid SourceRoomId,
    Guid TargetRoomId,
    Guid ProposedTargetContractId,
    Guid RosterSnapshotId,
    string RosterSha256,
    Guid PolicyId,
    string PolicySha256,
    Guid ReviewId,
    string ReviewPolicySha256,
    DateTime ReviewedAtUtc,
    RoomChangeDailyRentQuote Quote,
    RoomChangeSourceRentSnapshot SourceRentLedger,
    RoomChangeMidMonthRentPreview Rent,
    RoomChangeDepositDifferenceFacts DepositLedger,
    RoomChangeDepositDifferencePreview Deposit,
    Guid PreparedByUserId,
    DateTime PreparedAtUtc,
    string EvidenceSha256)
{
    public bool IsIndependentlyCashVerified => false;
    public bool IsOfficialInvoicePosting => false;
    public bool IsCompletedBankTransfer => false;
    public bool CanExecuteRoomChange => false;

    public void VerifyFingerprint()
    {
        if (PacketId == Guid.Empty || RequestId == Guid.Empty ||
            BuildingId == Guid.Empty || SourceContractId == Guid.Empty ||
            SourceRoomId == Guid.Empty || TargetRoomId == Guid.Empty ||
            SourceRoomId == TargetRoomId ||
            ProposedTargetContractId == Guid.Empty ||
            ProposedTargetContractId == SourceContractId ||
            RosterSnapshotId == Guid.Empty || PolicyId == Guid.Empty ||
            ReviewId == Guid.Empty || PreparedByUserId == Guid.Empty ||
            PreparedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            PreparedAtUtc < ReviewedAtUtc ||
            Quote is null || SourceRentLedger is null ||
            Rent is null || DepositLedger is null || Deposit is null ||
            EvidenceSha256 != CalculateFingerprint())
            throw new InvalidDataException(
                "Mid-month transfer packet fingerprint or identity has drifted.");

        try
        {
            var recomputedRent = RoomChangeMidMonthRentReconciliation.Assess(
                Quote, SourceRentLedger);
            var recomputedDeposit =
                RoomChangeDepositDifferencePreviewCalculator.Calculate(DepositLedger);
            if (Rent != recomputedRent || Deposit != recomputedDeposit ||
                Quote.TransferDate != Rent.TransferDate ||
                SourceContractId != Rent.SourceContractId ||
                SourceContractId != Deposit.SourceContractId ||
                ProposedTargetContractId != Deposit.TargetContractId ||
                SourceRentLedger.SourceContractId != SourceContractId ||
                DepositLedger.SourceContractId != SourceContractId ||
                DepositLedger.TargetContractId != ProposedTargetContractId)
                throw new InvalidDataException(
                    "Rent and deposit source figures disagree with the stored candidate.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException(
                "Saved financial evidence fails recalculation.", ex);
        }
    }

    /// <summary>
    /// Re-check exact business/contract scope AND the original policy, money
    /// and roster sources; fingerprint alone is not enough for authorization.
    /// Does not check real cash provenance, human signature or current room
    /// availability, which must be verified by separate scoped readers.
    /// </summary>
    public void VerifyAgainst(
        RoomChangeRequest request,
        Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review)
    {
        VerifyFingerprint();
        try
        {
            var rebuilt = RoomChangeMidMonthFinancialReconciliationPacketFactory.Prepare(
                PacketId, request, source, ProposedTargetContractId,
                roster, policy, review, Quote, SourceRentLedger, DepositLedger,
                PreparedByUserId, PreparedAtUtc);
            if (this != rebuilt)
                throw new InvalidDataException(
                    "Financial proposal is detached from its signed source documents.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            ArgumentException)
        {
            throw new InvalidDataException(
                "Financial proposal no longer matches current approved source evidence.", ex);
        }
    }

    internal string CalculateFingerprint()
    {
        var unsigned = this with { EvidenceSha256 = string.Empty };
        // Property order and decimal/date encoding are stable for the
        // versioned .NET audit candidate. No external JSON is trusted here.
        var canonical = JsonSerializer.Serialize(new
        {
            Version = 1,
            unsigned.PacketId,
            unsigned.RequestId,
            unsigned.BuildingId,
            unsigned.SourceContractId,
            unsigned.SourceRoomId,
            unsigned.TargetRoomId,
            unsigned.ProposedTargetContractId,
            unsigned.RosterSnapshotId,
            unsigned.RosterSha256,
            unsigned.PolicyId,
            unsigned.PolicySha256,
            unsigned.ReviewId,
            unsigned.ReviewPolicySha256,
            unsigned.ReviewedAtUtc,
            unsigned.Quote,
            unsigned.SourceRentLedger,
            unsigned.Rent,
            unsigned.DepositLedger,
            unsigned.Deposit,
            unsigned.PreparedByUserId,
            unsigned.PreparedAtUtc
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
