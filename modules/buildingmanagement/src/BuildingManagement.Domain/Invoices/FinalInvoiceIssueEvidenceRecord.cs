using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// STAGED database evidence for a future final-invoice issuance transaction.
/// Deliberately NOT an issued invoice, payable debt, authorized posting or
/// payment allocation. No production writer or PostgreSQL migration exists.
/// Only immutable preparation metadata is retained; a future issuer must
/// re-read/re-authorize all relationships under locks and approved policy.
/// </summary>
public sealed class FinalInvoiceIssueEvidenceRecord : Entity<Guid>
{
    public Guid DraftInvoiceId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public Guid PreparedByUserId { get; private set; }
    public DateTime PreparedAtUtc { get; private set; }
    public decimal KnownBaseChargeTotal { get; private set; }
    public string EvidenceJson { get; private set; } = string.Empty;
    public string EvidenceSha256 { get; private set; } = string.Empty;

    public bool IsInvoiceIssued => false;
    public bool IsReceivablePosted => false;

    private FinalInvoiceIssueEvidenceRecord() { }

    private FinalInvoiceIssueEvidenceRecord(
        Guid id, Guid preparedByUserId, FinalInvoiceEvidencePackage evidence,
        DateTime preparedAtUtc) : base(id)
    {
        DraftInvoiceId = evidence.DraftInvoiceId;
        MoveOutRequestId = evidence.MoveOutRequestId;
        ContractId = evidence.ContractId;
        RoomId = evidence.RoomId;
        BillingYear = evidence.FinalBillingDate.Year;
        BillingMonth = evidence.FinalBillingDate.Month;
        PreparedByUserId = preparedByUserId;
        PreparedAtUtc = preparedAtUtc;
        KnownBaseChargeTotal = evidence.KnownRentAndUtilities;
        EvidenceJson = evidence.EvidenceJson;
        EvidenceSha256 = evidence.EvidenceSha256;
    }

    public static FinalInvoiceIssueEvidenceRecord Prepare(
        Guid id, Guid preparedByUserId, DateTime preparedAtUtc,
        FinalInvoiceEvidencePackage evidence,
        FinalInvoiceIssuePreflight preflight)
    {
        if (id == Guid.Empty || preparedByUserId == Guid.Empty)
            throw new ArgumentException("Preparation record and actor IDs are required.");
        if (preparedAtUtc == default ||
            preparedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC preparation timestamp is required.",
                nameof(preparedAtUtc));
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(preflight);

        // This is a snapshot-copy guard, not a usable approval. Monetary
        // policy blockers deliberately DO NOT become cleared by staging.
        if (!evidence.VerifyChecksum() ||
            evidence.DraftInvoiceId == Guid.Empty ||
            evidence.MoveOutRequestId == Guid.Empty ||
            evidence.ContractId == Guid.Empty ||
            evidence.RoomId == Guid.Empty ||
            evidence.KnownRentAndUtilities <= 0m ||
            preflight.MoveOutRequestId != evidence.MoveOutRequestId ||
            preflight.ContractId != evidence.ContractId ||
            preflight.RoomId != evidence.RoomId ||
            preflight.UtilityDraftId != evidence.DraftInvoiceId ||
            preflight.EvidenceSha256 != evidence.EvidenceSha256 ||
            preflight.KnownBaseChargeTotal != evidence.KnownRentAndUtilities ||
            preflight.BaseCharges.Count != 3 ||
            preflight.BaseCharges.Sum(x => x.Amount) !=
                evidence.KnownRentAndUtilities)
            throw new InvalidOperationException(
                "A verified, consistent final invoice evidence preflight is required.");

        var staged = new FinalInvoiceIssueEvidenceRecord(
            id, preparedByUserId, evidence, preparedAtUtc);
        // Validation must be identical before insertion and after EF
        // rehydration. A matching hash alone cannot prove that source
        // readings or rate identifiers are internally consistent.
        staged.VerifyStoredEvidence();
        return staged;
    }

    /// <summary>
    /// Verifies the archived JSON digest AND persisted index metadata.
    /// This does not establish that source prices, identity or fee policy
    /// were authorized. A digest is integrity evidence, not a signature.
    /// </summary>
    public void VerifyStoredEvidence()
    {
        if (string.IsNullOrWhiteSpace(EvidenceJson) ||
            string.IsNullOrWhiteSpace(EvidenceSha256) ||
            !StringComparer.Ordinal.Equals(
                EvidenceSha256,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(EvidenceJson))).ToLowerInvariant()))
            throw new InvalidDataException("Stored final invoice evidence checksum mismatch.");

        try
        {
            using var document = JsonDocument.Parse(EvidenceJson);
            var root = document.RootElement;
            var billingDate = DateOnly.ParseExact(
                root.GetProperty("FinalBillingDate").GetString()!, "yyyy-MM-dd");
            var capturedAtUtc =
                root.GetProperty("CapturedAtUtc").GetDateTime();
            var utilities = root.GetProperty("Utilities").EnumerateArray()
                .Select(x => new
                {
                    Type = (UtilityType)x.GetProperty("UtilityType").GetInt32(),
                    RateId = x.GetProperty("UtilityRateId").GetGuid(),
                    Amount = x.GetProperty("Amount").GetDecimal(),
                    Quantity = x.GetProperty("Quantity").GetDecimal(),
                    UnitPrice = x.GetProperty("UnitPrice").GetDecimal(),
                    Sources = x.GetProperty("Sources").EnumerateArray()
                        .Select(source => new
                        {
                            SourceId = source.GetProperty("SourceId").GetGuid(),
                            MeterId = source.GetProperty("UtilityMeterId").GetGuid(),
                            Type = (MeterUsageSourceType)source
                                .GetProperty("SourceType").GetInt32(),
                            RecordedDate = source.GetProperty("RecordedDate").GetDateTime(),
                            Previous = source.GetProperty("PreviousReading").GetDecimal(),
                            Current = source.GetProperty("CurrentReading").GetDecimal()
                        }).ToArray()
                }).ToArray();
            var rent = root.GetProperty("FullMonthRent").GetDecimal();
            var total = root.GetProperty("KnownRentAndUtilities").GetDecimal();

            if (root.GetProperty("DraftInvoiceId").GetGuid() != DraftInvoiceId ||
                root.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                root.GetProperty("ContractId").GetGuid() != ContractId ||
                root.GetProperty("RoomId").GetGuid() != RoomId ||
                billingDate.Year != BillingYear ||
                billingDate.Month != BillingMonth ||
                billingDate.Day != DateTime.DaysInMonth(BillingYear, BillingMonth) ||
                total != KnownBaseChargeTotal ||
                rent <= 0m ||
                capturedAtUtc == default ||
                capturedAtUtc.Kind != DateTimeKind.Utc ||
                utilities.Length != 2 ||
                utilities.Select(x => x.Type).Distinct().Count() != 2 ||
                !utilities.Any(x => x.Type == UtilityType.Electricity) ||
                !utilities.Any(x => x.Type == UtilityType.Water) ||
                utilities.Any(x =>
                    x.RateId == Guid.Empty ||
                    x.Quantity < 0m || x.UnitPrice <= 0m || x.Amount < 0m ||
                    x.Amount != x.Quantity * x.UnitPrice ||
                    x.Sources.Length == 0 ||
                    x.Sources.Sum(source => source.Current - source.Previous)
                        != x.Quantity ||
                    x.Sources.Any(source =>
                        source.SourceId == Guid.Empty ||
                        source.MeterId == Guid.Empty ||
                        !Enum.IsDefined(source.Type) ||
                        source.RecordedDate.Year != BillingYear ||
                        source.RecordedDate.Month != BillingMonth ||
                        source.Previous < 0m ||
                        source.Current < source.Previous)) ||
                utilities.SelectMany(x => x.Sources)
                    .Select(source => (source.Type, source.SourceId))
                    .Distinct().Count() != utilities.Sum(x => x.Sources.Length) ||
                total != rent + utilities.Sum(x => x.Amount) ||
                PreparedByUserId == Guid.Empty ||
                PreparedAtUtc == default)
                throw new InvalidDataException(
                    "Stored final invoice evidence disagrees with indexed metadata.");
        }
        catch (Exception error) when (error is FormatException or
            JsonException or ArgumentException or InvalidOperationException or
            OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException(
                "Stored final invoice evidence cannot be verified.", error);
        }
    }
}
