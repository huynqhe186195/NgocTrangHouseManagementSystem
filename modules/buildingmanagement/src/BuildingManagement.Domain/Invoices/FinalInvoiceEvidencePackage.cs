using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Immutable copy of the validated rent/electricity/water evidence for an
/// APPROVED final month. It is NOT a posted invoice or an issuance approval.
/// Service fees, invoice due date, debt allocation and deposit disposition
/// still require separately reviewed policy and authorization.
/// </summary>
public sealed class FinalInvoiceEvidencePackage
{
    public Guid MoveOutRequestId { get; }
    public Guid DraftInvoiceId { get; }
    public Guid ContractId { get; }
    public Guid RoomId { get; }
    public DateOnly FinalBillingDate { get; }
    public decimal FullMonthRent { get; }
    public decimal KnownRentAndUtilities { get; }
    public DateTime CapturedAtUtc { get; }
    public IReadOnlyList<FinalInvoiceUtilityEvidence> Utilities { get; }
    public string EvidenceJson { get; }
    public string EvidenceSha256 { get; }

    // Never mistake an immutable evidence package for a posted receivable.
    public bool CanIssue => false;
    public bool IsReceivablePosted => false;

    private FinalInvoiceEvidencePackage(
        Guid requestId, Guid invoiceId, Guid contractId, Guid roomId,
        DateOnly finalDate, decimal rent, decimal knownTotal,
        DateTime capturedAtUtc,
        IReadOnlyList<FinalInvoiceUtilityEvidence> utilities)
    {
        MoveOutRequestId = requestId;
        DraftInvoiceId = invoiceId;
        ContractId = contractId;
        RoomId = roomId;
        FinalBillingDate = finalDate;
        FullMonthRent = rent;
        KnownRentAndUtilities = knownTotal;
        CapturedAtUtc = capturedAtUtc;
        Utilities = utilities;

        // Deliberately fixed field order, utility order and source order.
        // The serialized immutable values form the historical audit proof.
        EvidenceJson = JsonSerializer.Serialize(new
        {
            MoveOutRequestId,
            DraftInvoiceId,
            ContractId,
            RoomId,
            FinalBillingDate,
            FullMonthRent,
            KnownRentAndUtilities,
            CapturedAtUtc,
            Utilities
        });
        EvidenceSha256 = Digest(EvidenceJson);
    }

    public bool VerifyChecksum()
        => string.Equals(EvidenceSha256, Digest(EvidenceJson),
            StringComparison.Ordinal);

    public static FinalInvoiceEvidencePackage Capture(
        MoveOutRequest request, Contract? contract,
        Invoice invoice,
        IReadOnlyCollection<InvoiceItem> items,
        IReadOnlyCollection<InvoiceItemMeterSource> sources,
        DateTime capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sources);
        if (capturedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "Evidence capture timestamp must be UTC.", nameof(capturedAtUtc));

        var review = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { invoice }, items, sources);
        if (review.ProvisionalRentAndUtilities is not decimal total ||
            review.FullMonthRent is not decimal rent ||
            review.UtilityDraftId != invoice.Id ||
            request.ApprovedMoveOutDate is not DateOnly date ||
            date.Day != DateTime.DaysInMonth(date.Year, date.Month))
            throw new InvalidOperationException(
                "Final invoice evidence requires approved month-end rent and complete, scoped utility draft sources.");

        var firstDay = new DateTime(date.Year, date.Month, 1);
        var lastDay = date.ToDateTime(TimeOnly.MaxValue);
        if (sources.Any(source =>
            source.RecordedDate < firstDay ||
            source.RecordedDate > lastDay))
            throw new InvalidOperationException(
                "Meter evidence outside the approved final billing month cannot be included.");

        // The stored source trail is copied by VALUE, not referenced as a
        // mutable EF collection or evaluated using current tariff prices.
        var lines = items
            .OrderBy(x => x.UtilityType)
            .Select(line => new FinalInvoiceUtilityEvidence(
                line.UtilityType,
                line.UtilityRateId!.Value,
                line.RecordedQuantity,
                line.UnitPrice!.Value,
                line.ProvisionalAmount!.Value,
                Array.AsReadOnly(sources
                    .Where(x => x.InvoiceItemId == line.Id)
                    .OrderBy(x => x.RecordedDate)
                    .ThenBy(x => x.SourceType)
                    .ThenBy(x => x.SourceId)
                    .Select(x => new FinalInvoiceMeterEvidence(
                        x.SourceId, x.UtilityMeterId, x.SourceType,
                        x.RecordedDate, x.PreviousReading,
                        x.CurrentReading))
                    .ToArray())))
            .ToArray();

        return new FinalInvoiceEvidencePackage(
            request.Id, invoice.Id, request.ContractId, request.RoomId,
            date, rent, total, capturedAtUtc, Array.AsReadOnly(lines));
    }

    private static string Digest(string json)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}

public sealed record FinalInvoiceUtilityEvidence(
    UtilityType UtilityType,
    Guid UtilityRateId,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    IReadOnlyList<FinalInvoiceMeterEvidence> Sources);

public sealed record FinalInvoiceMeterEvidence(
    Guid SourceId,
    Guid UtilityMeterId,
    MeterUsageSourceType SourceType,
    DateTime RecordedDate,
    decimal PreviousReading,
    decimal CurrentReading);
