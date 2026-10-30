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
/// Frozen, UNISSUED monthly billing candidate. A separate authenticated
/// issuer and recognized-debt/payment ledger are mandatory before billing.
/// This record never changes Invoice.Status or allocates customer funds.
/// </summary>
public sealed class MonthlyInvoiceCandidateLedgerRecord : Entity<Guid>
{
    public Guid DraftInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal RentAmount { get; private set; }
    public decimal ElectricityAmount { get; private set; }
    public decimal WaterAmount { get; private set; }
    public decimal AuxiliaryAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string FrozenEvidenceJson { get; private set; } = string.Empty;
    public string FrozenEvidenceSha256 { get; private set; } = string.Empty;

    public bool IsIssued => false;
    public bool IsOfficialReceivable => false;
    public bool CanAllocatePayment => false;
    public bool CanOffsetDeposit => false;

    private MonthlyInvoiceCandidateLedgerRecord() { }
    private MonthlyInvoiceCandidateLedgerRecord(Guid id) : base(id) { }

    /// <summary>
    /// Called only with source rows already re-read from the locked original
    /// contract/draft/meter/rate transaction, NOT with trusted client totals.
    /// The candidate is archived by value including every physical meter
    /// source, service fee and documented decision.
    /// </summary>
    public static MonthlyInvoiceCandidateLedgerRecord Capture(
        Guid id, MonthlyInvoiceCandidateSnapshot evidence)
    {
        if (id == Guid.Empty || evidence is null)
            throw new ArgumentException("Candidate id and evidence required.");
        // PostgreSQL timestamptz stores microseconds; .NET ticks measure
        // 100-nanosecond intervals. Canonicalize BEFORE serializing AND
        // hashing the audit evidence, not merely before the SQL insert.
        // Otherwise a perfectly valid archived row disagrees with its own
        // ReviewedAtUtc after EF materializes it from PostgreSQL.
        if (evidence.ReviewedAtUtc.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("Monthly review time must be UTC.");
        evidence = evidence with
        {
            ReviewedAtUtc = evidence.ReviewedAtUtc.AddTicks(
                -(evidence.ReviewedAtUtc.Ticks % 10L))
        };
        Validate(evidence);
        var json = JsonSerializer.Serialize(evidence);
        if (json.Length > 1_000_000)
            throw new InvalidOperationException("Monthly invoice evidence too large.");
        var candidate = new MonthlyInvoiceCandidateLedgerRecord(id)
        {
            DraftInvoiceId = evidence.DraftInvoiceId,
            ContractId = evidence.ContractId,
            RoomId = evidence.RoomId,
            BuildingId = evidence.BuildingId,
            BillingYear = evidence.BillingYear,
            BillingMonth = evidence.BillingMonth,
            ReviewedByUserId = evidence.ReviewedByUserId,
            ReviewedAtUtc = evidence.ReviewedAtUtc,
            DueDate = evidence.DueDate,
            RentAmount = evidence.RentAmount,
            ElectricityAmount = evidence.ElectricityAmount,
            WaterAmount = evidence.WaterAmount,
            AuxiliaryAmount = evidence.AuxiliaryAmount,
            TotalAmount = evidence.TotalAmount,
            FrozenEvidenceJson = json,
            FrozenEvidenceSha256 = Hash(json)
        };
        candidate.VerifyStoredEvidence();
        return candidate;
    }

    public MonthlyInvoiceCandidateSnapshot VerifyStoredEvidence()
    {
        if (Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(FrozenEvidenceJson) ||
            FrozenEvidenceJson.Length > 1_000_000 ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                FrozenEvidenceSha256, Hash(FrozenEvidenceJson)))
            throw new InvalidDataException("Monthly invoice archive hash mismatch.");

        MonthlyInvoiceCandidateSnapshot? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<MonthlyInvoiceCandidateSnapshot>(
                FrozenEvidenceJson);
            if (evidence is null)
                throw new InvalidDataException("Archived monthly evidence missing.");
            Validate(evidence);
        }
        catch (Exception ex) when (ex is JsonException or
            ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Archived monthly invoice evidence is invalid.", ex);
        }

        if (evidence.DraftInvoiceId != DraftInvoiceId ||
            evidence.ContractId != ContractId ||
            evidence.RoomId != RoomId ||
            evidence.BuildingId != BuildingId ||
            evidence.BillingYear != BillingYear ||
            evidence.BillingMonth != BillingMonth ||
            evidence.ReviewedByUserId != ReviewedByUserId ||
            evidence.ReviewedAtUtc != ReviewedAtUtc ||
            evidence.DueDate != DueDate ||
            evidence.RentAmount != RentAmount ||
            evidence.ElectricityAmount != ElectricityAmount ||
            evidence.WaterAmount != WaterAmount ||
            evidence.AuxiliaryAmount != AuxiliaryAmount ||
            evidence.TotalAmount != TotalAmount)
            throw new InvalidDataException("Monthly candidate header differs from frozen evidence.");
        return evidence;
    }

    private static void Validate(MonthlyInvoiceCandidateSnapshot evidence)
    {
        if (evidence.DraftInvoiceId == Guid.Empty ||
            evidence.ContractId == Guid.Empty ||
            evidence.RoomId == Guid.Empty ||
            evidence.BuildingId == Guid.Empty ||
            evidence.ReviewedByUserId == Guid.Empty ||
            evidence.BillingYear is < 2000 or > 2200 ||
            evidence.BillingMonth is < 1 or > 12 ||
            evidence.ReviewedAtUtc == default ||
            evidence.ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            evidence.DueDate < DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(evidence.ReviewedAtUtc,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))) ||
            !Reference(evidence.DueDatePolicyReference, 8, 200) ||
            !Reference(evidence.AuxiliaryFeePolicyReference, 8, 200) ||
            evidence.UtilityLines is null || evidence.UtilityLines.Count != 2 ||
            evidence.UtilityLines.Any(x => x is null) ||
            evidence.RentAmount <= 0m ||
            !Whole(evidence.RentAmount) ||
            !Whole(evidence.ElectricityAmount) ||
            !Whole(evidence.WaterAmount) ||
            !Whole(evidence.AuxiliaryAmount) ||
            !Whole(evidence.TotalAmount))
            throw new InvalidOperationException("Monthly candidate identity, decision or VND amount invalid.");

        var electricity = evidence.UtilityLines
            .SingleOrDefault(x => x is not null && x.UtilityType == UtilityType.Electricity);
        var water = evidence.UtilityLines
            .SingleOrDefault(x => x is not null && x.UtilityType == UtilityType.Water);
        if (electricity is null || water is null ||
            evidence.UtilityLines.Select(x => x.UtilityType).Distinct().Count() != 2 ||
            !ValidLine(electricity, evidence.ElectricityAmount) ||
            !ValidLine(water, evidence.WaterAmount))
            throw new InvalidOperationException("Both original utility lines required.");

        var physicalKeys = evidence.UtilityLines.SelectMany(x => x.Sources)
            .Select(x => (x.SourceType, x.SourceId)).ToArray();
        if (physicalKeys.Distinct().Count() != physicalKeys.Length)
            throw new InvalidOperationException("A meter source was billed more than once.");

        if (evidence.AuxiliaryCharges is null ||
            evidence.AuxiliaryFeeDecision is not
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges and
                not AuxiliaryFeeAssessmentDecision.ItemizedCharges)
            throw new InvalidOperationException("Explicit monthly fee decision required.");
        if (evidence.AuxiliaryFeeDecision ==
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges)
        {
            if (evidence.AuxiliaryCharges.Count != 0 ||
                evidence.AuxiliaryAmount != 0m)
                throw new InvalidOperationException("No-fee decision contradicts charges.");
        }
        else
        {
            if (evidence.AuxiliaryCharges.Count == 0 ||
                evidence.AuxiliaryCharges.Any(x => !ValidFee(x)) ||
                evidence.AuxiliaryCharges
                    .GroupBy(x => (x.Kind, x.ServiceCode.Trim().ToUpperInvariant()))
                    .Any(x => x.Count() > 1) ||
                evidence.AuxiliaryCharges.Sum(x => x.Amount) !=
                    evidence.AuxiliaryAmount)
                throw new InvalidOperationException("Approved service fees are inconsistent.");
        }

        if (checked(evidence.RentAmount +
                    evidence.ElectricityAmount +
                    evidence.WaterAmount +
                    evidence.AuxiliaryAmount) != evidence.TotalAmount ||
            evidence.TotalAmount <= 0m)
            throw new InvalidOperationException("Frozen monthly total does not reconcile.");
    }

    private static bool ValidLine(
        MonthlyInvoiceCandidateUtilityLine? line, decimal expected)
    {
        if (line is null || line.UtilityRateId == Guid.Empty ||
            line.UnitPrice <= 0m || line.Quantity < 0m ||
            line.Amount != expected || !Whole(line.Amount) ||
            line.Sources is null || line.Sources.Count == 0)
            return false;
        try
        {
            if (line.Quantity * line.UnitPrice != line.Amount ||
                line.Sources.Any(x => x is null ||
                    x.SourceId == Guid.Empty ||
                    x.UtilityMeterId == Guid.Empty ||
                    !Enum.IsDefined(x.SourceType) ||
                    x.PreviousReading < 0m ||
                    x.CurrentReading < x.PreviousReading ||
                    x.CurrentReading - x.PreviousReading != x.Quantity) ||
                line.Sources.Sum(x => x.Quantity) != line.Quantity)
                return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        return true;
    }

    private static bool ValidFee(FinalInvoiceAuxiliaryChargeProposal? line)
    {
        if (line is null || !Enum.IsDefined(line.Kind) ||
            !Reference(line.ServiceCode, 1, 64) ||
            !Reference(line.ContractOrRateReference, 8, 200) ||
            !Reference(line.Description, 1, 500) ||
            line.Quantity <= 0m || line.UnitPrice <= 0m ||
            line.Amount <= 0m || !Whole(line.Amount))
            return false;
        try { return line.Quantity * line.UnitPrice == line.Amount; }
        catch (OverflowException) { return false; }
    }

    private static bool Whole(decimal amount)
        => amount >= 0m && decimal.Truncate(amount) == amount;

    private static bool Reference(string? text, int min, int max)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Trim().Length >= min && text.Trim().Length <= max;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

public sealed record MonthlyInvoiceCandidateMeterSource(
    Guid SourceId, Guid UtilityMeterId, MeterUsageSourceType SourceType,
    DateTime RecordedDate, decimal PreviousReading, decimal CurrentReading)
{
    public decimal Quantity => CurrentReading - PreviousReading;
}

public sealed record MonthlyInvoiceCandidateUtilityLine(
    UtilityType UtilityType, Guid UtilityRateId, decimal Quantity,
    decimal UnitPrice, decimal Amount,
    IReadOnlyList<MonthlyInvoiceCandidateMeterSource> Sources);

public sealed record MonthlyInvoiceCandidateSnapshot(
    Guid DraftInvoiceId, Guid ContractId, Guid RoomId, Guid BuildingId,
    int BillingYear, int BillingMonth, Guid ReviewedByUserId,
    DateTime ReviewedAtUtc, DateOnly DueDate, string DueDatePolicyReference,
    decimal RentAmount, decimal ElectricityAmount, decimal WaterAmount,
    AuxiliaryFeeAssessmentDecision AuxiliaryFeeDecision,
    string AuxiliaryFeePolicyReference,
    IReadOnlyList<FinalInvoiceAuxiliaryChargeProposal> AuxiliaryCharges,
    decimal AuxiliaryAmount, decimal TotalAmount,
    IReadOnlyList<MonthlyInvoiceCandidateUtilityLine> UtilityLines);
