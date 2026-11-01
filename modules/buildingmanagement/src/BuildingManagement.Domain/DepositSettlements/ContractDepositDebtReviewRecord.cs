using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Independent staff signoff on all CURRENT verified, undisputed official
/// debt lines and actual held funds. This review is distinct from OWNER
/// authorization, invoice issuance approval and bank receipt approval.
/// The signed reference is an auditable human attestation, not an external
/// electronic-signature validation.
/// </summary>
public sealed class ContractDepositDebtReviewRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ReviewedByUserId { get; private set; }
    public DateOnly ReviewedAsOfDate { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public decimal VerifiedHeldAmount { get; private set; }
    public decimal VerifiedOutstandingAmount { get; private set; }
    public string SignedReviewReference { get; private set; } = string.Empty;
    public string ReviewJson { get; private set; } = string.Empty;
    public string ReviewSha256 { get; private set; } = string.Empty;

    private ContractDepositDebtReviewRecord() { }
    private ContractDepositDebtReviewRecord(Guid id) : base(id) { }

    public bool CanPostMoney => false;

    public static ContractDepositDebtReviewRecord Capture(
        Guid id, Guid contractId, Guid moveOutRequestId, Guid buildingId,
        Guid reviewerId, DateOnly asOfDate, DateTime reviewedAtUtc,
        decimal verifiedHeldAmount,
        IReadOnlyCollection<ContractDepositOffsetClaim>? claims,
        string? signedReviewReference)
    {
        if (id == Guid.Empty || contractId == Guid.Empty ||
            moveOutRequestId == Guid.Empty || buildingId == Guid.Empty ||
            reviewerId == Guid.Empty || asOfDate == default ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            verifiedHeldAmount <= 0m ||
            decimal.Truncate(verifiedHeldAmount) != verifiedHeldAmount ||
            string.IsNullOrWhiteSpace(signedReviewReference) ||
            signedReviewReference.Trim().Length is < 12 or > 500 ||
            claims is null || claims.Count is < 1 or > 250 ||
            claims.Any(x => x is null || x.ClaimId == Guid.Empty ||
                !x.IndependentlyVerified || x.IsDisputed ||
                x.OutstandingVnd <= 0m ||
                decimal.Truncate(x.OutstandingVnd) != x.OutstandingVnd ||
                string.IsNullOrWhiteSpace(x.SignedEvidenceReference) ||
                x.SignedEvidenceReference.Trim().Length < 8) ||
            claims.Select(x => x.ClaimId).Distinct().Count() != claims.Count)
            throw new InvalidOperationException(
                "Independent review requires complete undisputed source-backed debt.");
        var total = claims.Sum(x => x.OutstandingVnd);
        var copy = claims.OrderBy(x => x.ClaimId).ToArray();
        var at = reviewedAtUtc.AddTicks(-(reviewedAtUtc.Ticks % 10));
        var json = JsonSerializer.Serialize(new {
            ContractId = contractId, MoveOutRequestId = moveOutRequestId,
            BuildingId = buildingId, ReviewedByUserId = reviewerId,
            ReviewedAsOfDate = asOfDate, ReviewedAtUtc = at,
            VerifiedHeldAmount = verifiedHeldAmount,
            VerifiedOutstandingAmount = total,
            SignedReviewReference = signedReviewReference.Trim(),
            Claims = copy
        });
        var row = new ContractDepositDebtReviewRecord(id) {
            ContractId = contractId, MoveOutRequestId = moveOutRequestId,
            BuildingId = buildingId, ReviewedByUserId = reviewerId,
            ReviewedAsOfDate = asOfDate, ReviewedAtUtc = at,
            VerifiedHeldAmount = verifiedHeldAmount,
            VerifiedOutstandingAmount = total,
            SignedReviewReference = signedReviewReference.Trim(),
            ReviewJson = json, ReviewSha256 = Sha(json)
        };
        row.VerifyStoredReview();
        return row;
    }

    public void VerifyStoredReview()
    {
        if (Id == Guid.Empty || ContractId == Guid.Empty ||
            MoveOutRequestId == Guid.Empty || BuildingId == Guid.Empty ||
            ReviewedByUserId == Guid.Empty ||
            ReviewedAsOfDate == default ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            VerifiedHeldAmount <= 0m ||
            VerifiedOutstandingAmount <= 0m ||
            string.IsNullOrWhiteSpace(SignedReviewReference) ||
            SignedReviewReference.Length is < 12 or > 500 ||
            ReviewJson.Length is < 100 or > 1000000 ||
            ReviewSha256 != Sha(ReviewJson))
            throw new InvalidDataException("Signed debt review index or hash changed.");
        try
        {
            using var json = JsonDocument.Parse(ReviewJson);
            var d = json.RootElement;
            if (d.GetProperty("ContractId").GetGuid() != ContractId ||
                d.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                d.GetProperty("BuildingId").GetGuid() != BuildingId ||
                d.GetProperty("ReviewedByUserId").GetGuid() != ReviewedByUserId ||
                DateOnly.Parse(d.GetProperty("ReviewedAsOfDate").GetString()!) !=
                    ReviewedAsOfDate ||
                d.GetProperty("ReviewedAtUtc").GetDateTime() != ReviewedAtUtc ||
                d.GetProperty("VerifiedHeldAmount").GetDecimal() != VerifiedHeldAmount ||
                d.GetProperty("VerifiedOutstandingAmount").GetDecimal() !=
                    VerifiedOutstandingAmount ||
                d.GetProperty("SignedReviewReference").GetString() !=
                    SignedReviewReference)
                throw new InvalidDataException("Signed debt review proof differs from index.");
            var claims = JsonSerializer.Deserialize<ContractDepositOffsetClaim[]>(
                d.GetProperty("Claims").GetRawText()) ??
                throw new InvalidDataException("Missing reviewed claims.");
            if (claims.Length is < 1 or > 250 ||
                claims.Select(x => x.ClaimId).Distinct().Count() != claims.Length ||
                claims.Any(x => !x.IndependentlyVerified || x.IsDisputed ||
                    x.OutstandingVnd <= 0m ||
                    string.IsNullOrWhiteSpace(x.SignedEvidenceReference)) ||
                claims.Sum(x => x.OutstandingVnd) != VerifiedOutstandingAmount)
                throw new InvalidDataException("Signed reviewed claim set is invalid.");
        }
        catch (Exception ex) when (ex is JsonException or
            KeyNotFoundException or FormatException or InvalidOperationException)
        { throw new InvalidDataException("Signed review JSON cannot be verified.", ex); }
    }

    public void RequireCurrentEvidence(
        Guid contractId, Guid requestId, Guid buildingId,
        decimal actualHeldAmount,
        IReadOnlyCollection<ContractDepositOffsetClaim> currentClaims)
    {
        VerifyStoredReview();
        if (ContractId != contractId || MoveOutRequestId != requestId ||
            BuildingId != buildingId ||
            VerifiedHeldAmount != actualHeldAmount)
            throw new InvalidOperationException("Independent review is stale.");
        using var json = JsonDocument.Parse(ReviewJson);
        var expected = JsonSerializer.Deserialize<ContractDepositOffsetClaim[]>(
            json.RootElement.GetProperty("Claims").GetRawText())!;
        if (expected.Length != currentClaims.Count ||
            !expected.OrderBy(x => x.ClaimId)
                .SequenceEqual(currentClaims.OrderBy(x => x.ClaimId)))
            throw new InvalidOperationException(
                "Official claim or dispute evidence changed after review.");
    }

    private static string Sha(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
