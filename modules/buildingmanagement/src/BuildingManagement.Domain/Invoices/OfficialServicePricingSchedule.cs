using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// D03 — the per-service pricing schedule carried by an ISSUED monthly
/// invoice. Every line is anchored to the original room-specific tariff
/// version that was effective for the billed month, and to the
/// independently verified occupancy / meter quantity of that month. The
/// schedule is frozen at issuance: a later room or pricing edit cannot
/// change what an already issued invoice says.
///
/// This is not a receivable. Posting an official debt remains a separate,
/// independently reviewed action.
/// </summary>
public sealed record OfficialServicePricingLine(
    Guid ServiceId,
    string ServiceCode,
    Guid TariffVersionId,
    int TariffVersion,
    string TariffSha256,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    decimal UnitPriceVnd,
    DateOnly ServiceFromVietnam,
    DateOnly ServiceThroughVietnam,
    int VerifiedOccupantCount,
    decimal? VerifiedMeterQuantity,
    decimal AmountVnd,
    string SourceBindingSha256)
{
    public bool IsOfficialReceivable => false;
    public bool CanPostMoney => false;
}

/// <summary>
/// Immutable service schedule for one issued monthly invoice. Constructed
/// only from source bindings that were themselves verified against the
/// original candidate and the original tariff version.
/// </summary>
public sealed record OfficialServicePricingSchedule
{
    private OfficialServicePricingSchedule(
        Guid issuedInvoiceId, Guid candidateId, Guid contractId,
        Guid roomId, Guid buildingId, int billingYear, int billingMonth,
        IReadOnlyList<OfficialServicePricingLine> lines,
        int verifiedResidencyCount, decimal totalVnd)
    {
        IssuedInvoiceId = issuedInvoiceId;
        CandidateId = candidateId;
        ContractId = contractId;
        RoomId = roomId;
        BuildingId = buildingId;
        BillingYear = billingYear;
        BillingMonth = billingMonth;
        Lines = lines;
        VerifiedResidencyCount = verifiedResidencyCount;
        TotalVnd = totalVnd;
        EvidenceSha256 = Fingerprint(this);
    }

    public Guid IssuedInvoiceId { get; }
    public Guid CandidateId { get; }
    public Guid ContractId { get; }
    public Guid RoomId { get; }
    public Guid BuildingId { get; }
    public int BillingYear { get; }
    public int BillingMonth { get; }
    public IReadOnlyList<OfficialServicePricingLine> Lines { get; }
    public int VerifiedResidencyCount { get; }
    public decimal TotalVnd { get; }
    public string EvidenceSha256 { get; }

    public int LineCount => Lines.Count;
    public bool IsOfficialReceivable => false;
    public bool CanPostMoney => false;

    /// <summary>
    /// Binds the service lines of an ISSUED invoice to their original
    /// tariff versions.
    ///
    /// <paramref name="verifiedResidency"/> MUST be loaded by the caller
    /// from the server-side contract tenant links; it is never supplied by
    /// the client. Per-person lines must agree with it exactly.
    ///
    /// <paramref name="roomTariffHistory"/> MUST be the complete scoped
    /// tariff history for the rooms involved, so a rate that was superseded
    /// after the candidate was frozen is refused rather than billed.
    /// </summary>
    public static OfficialServicePricingSchedule Bind(
        MonthlyIssuedInvoiceRecord? issued,
        MonthlyInvoiceCandidateLedgerRecord? candidate,
        IReadOnlyCollection<RoomScopedServiceInvoiceSourceBinding>? sourceBindings,
        IReadOnlyCollection<RoomScopedServiceTariffVersion>? roomTariffHistory,
        IReadOnlyCollection<RoomChangeRosterMember>? verifiedResidency)
    {
        if (issued is null || candidate is null)
            throw new InvalidOperationException(
                "An issued invoice and its original reviewed candidate are required.");

        var original = candidate.VerifyStoredEvidence();
        if (issued.Id == Guid.Empty || issued.CandidateId != candidate.Id ||
            issued.ContractId != candidate.ContractId ||
            issued.RoomId != candidate.RoomId ||
            issued.BuildingId != candidate.BuildingId ||
            issued.BillingYear != candidate.BillingYear ||
            issued.BillingMonth != candidate.BillingMonth ||
            !StringComparer.Ordinal.Equals(
                issued.CandidateSha256, candidate.FrozenEvidenceSha256))
            throw new InvalidOperationException(
                "Service pricing must be bound to the invoice's own issued terms.");

        var residency = VerifyResidency(verifiedResidency);
        var lines = original.AuxiliaryCharges;

        if (original.AuxiliaryFeeDecision ==
            AuxiliaryFeeAssessmentDecision.NotReviewed)
            throw new InvalidOperationException(
                "An issued invoice cannot carry unreviewed service fees.");

        if (lines.Count == 0)
        {
            if ((sourceBindings?.Count ?? 0) != 0 || original.AuxiliaryAmount != 0m)
                throw new InvalidOperationException(
                    "A no-fee invoice cannot carry service source bindings.");
            return new OfficialServicePricingSchedule(
                issued.Id, candidate.Id, candidate.ContractId, candidate.RoomId,
                candidate.BuildingId, candidate.BillingYear, candidate.BillingMonth,
                Array.Empty<OfficialServicePricingLine>(), residency.Count, 0m);
        }

        if (original.AuxiliaryFeeDecision !=
            AuxiliaryFeeAssessmentDecision.ItemizedCharges ||
            sourceBindings is null || roomTariffHistory is null)
            throw new InvalidOperationException(
                "Itemized service fees require original tariff history and source bindings.");

        if (sourceBindings.Count != lines.Count)
            throw new InvalidOperationException(
                "Every issued service line requires exactly one original source binding.");

        var byCode = new Dictionary<string, RoomScopedServiceInvoiceSourceBinding>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var binding in sourceBindings)
        {
            binding.VerifySourceFingerprint();
            if (!byCode.TryAdd(binding.ServiceCode.Trim(), binding))
                throw new InvalidOperationException(
                    "One issued service line cannot be bound twice.");
        }

        var priced = new List<OfficialServicePricingLine>(lines.Count);
        foreach (var fee in lines)
        {
            if (!byCode.Remove(fee.ServiceCode.Trim(), out var binding))
                throw new InvalidOperationException(
                    "An issued service line has no original source binding.");

            if (binding.ContractId != candidate.ContractId ||
                binding.RoomId != candidate.RoomId ||
                binding.BuildingId != candidate.BuildingId ||
                binding.BillingYear != candidate.BillingYear ||
                binding.BillingMonth != candidate.BillingMonth ||
                !StringComparer.Ordinal.Equals(
                    binding.OriginalMonthlyCandidateSha256,
                    candidate.FrozenEvidenceSha256))
                throw new InvalidOperationException(
                    "Service source binding does not belong to this issued invoice.");

            // A rate superseded before the billed period ended must not be
            // billed: the room's effective version for that date is the one
            // the invoice has to name.
            //
            // Selection is anchored to the candidate's OWN review instant,
            // not to "latest version today". A tariff the Owner appends after
            // the invoice was issued must never rewrite an issued schedule,
            // so eligibility stops at ReviewedAtUtc — an immutable boundary.
            var effective = SelectFrozenVersion(
                roomTariffHistory, binding, candidate.ReviewedAtUtc);
            if (effective.Id != binding.OriginalTariffVersionId)
                throw new InvalidOperationException(
                    "Service price was superseded; the issued line must use the effective original version.");

            var tariff = effective;
            var recalculated = tariff.PreviewCharge(
                binding.ServiceFromVietnam, binding.ServiceThroughVietnam,
                binding.VerifiedOccupantCount, binding.VerifiedMeterQuantity);
            if (recalculated.ProposedChargeVnd !=
                    binding.FrozenAuxiliaryLineAmountVnd ||
                binding.FrozenAuxiliaryLineAmountVnd != fee.Amount ||
                !StringComparer.Ordinal.Equals(
                    binding.VerifiedChargeSourceReference,
                    binding.VerifiedChargeSourceReference.Trim()) ||
                binding.VerifiedChargeSourceReference.Trim().Length is < 12 or > 500)
                throw new InvalidOperationException(
                    "Issued service line does not reconcile to its original room tariff arithmetic.");

            if (binding.BillingUnit == RoomScopedServiceBillingUnit.PerPerson &&
                binding.VerifiedOccupantCount != residency.Count)
                throw new InvalidOperationException(
                    "Per-person service must use the verified residency count of the billed month.");

            priced.Add(new OfficialServicePricingLine(
                binding.ServiceId, binding.ServiceCode, binding.OriginalTariffVersionId,
                binding.OriginalTariffVersion, binding.OriginalTariffSha256,
                binding.BillingUnit, binding.ChargingRule,
                binding.OriginalFullPeriodUnitPriceVnd,
                binding.ServiceFromVietnam, binding.ServiceThroughVietnam,
                binding.VerifiedOccupantCount, binding.VerifiedMeterQuantity,
                binding.FrozenAuxiliaryLineAmountVnd, binding.EvidenceSha256));
        }

        if (byCode.Count != 0)
            throw new InvalidOperationException(
                "A source binding has no matching issued service line.");

        var total = 0m;
        try { foreach (var line in priced) total = checked(total + line.AmountVnd); }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                "Issued service schedule cannot overflow VND accounting.", ex);
        }
        if (total != original.AuxiliaryAmount || total <= 0m)
            throw new InvalidOperationException(
                "Issued service schedule total does not reconcile to the original candidate.");

        var schedule = new OfficialServicePricingSchedule(
            issued.Id, candidate.Id, candidate.ContractId, candidate.RoomId,
            candidate.BuildingId, candidate.BillingYear, candidate.BillingMonth,
            priced.AsReadOnly(), residency.Count, total);
        return schedule;
    }

    public void VerifySchedule(MonthlyInvoiceCandidateLedgerRecord candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.VerifyStoredEvidence();
        if (CandidateId != candidate.Id ||
            ContractId != candidate.ContractId || RoomId != candidate.RoomId ||
            BuildingId != candidate.BuildingId ||
            BillingYear != candidate.BillingYear ||
            BillingMonth != candidate.BillingMonth ||
            Lines.Any(x => x.AmountVnd <= 0m || x.TariffVersion <= 0 ||
                string.IsNullOrWhiteSpace(x.ServiceCode)) ||
            Lines.Sum(x => x.AmountVnd) != TotalVnd ||
            EvidenceSha256 != Fingerprint(this))
            throw new InvalidDataException(
                "Issued service pricing schedule has drifted from the issued terms.");
    }

    /// <summary>
    /// The room/service tariff version that was effective for the billed
    /// period as of the candidate's review instant. Later appended versions
    /// are deliberately outside the window, so an issued schedule cannot be
    /// rewritten by a subsequent pricing edit.
    /// </summary>
    private static RoomScopedServiceTariffVersion SelectFrozenVersion(
        IReadOnlyCollection<RoomScopedServiceTariffVersion> history,
        RoomScopedServiceInvoiceSourceBinding binding, DateTime reviewedAtUtc)
    {
        var eligible = history
            .Where(x => x.RoomId == binding.RoomId &&
                        x.ServiceId == binding.ServiceId &&
                        x.ApprovedAtUtc <= reviewedAtUtc)
            .ToArray();
        foreach (var item in eligible) item.VerifyFingerprint();

        var covering = eligible
            .Where(x => x.EffectiveFrom <= binding.ServiceThroughVietnam &&
                        (!x.EffectiveThrough.HasValue ||
                         binding.ServiceFromVietnam <= x.EffectiveThrough.Value))
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.Version)
            .ToArray();

        if (covering.Length == 0)
            throw new InvalidOperationException(
                "No approved room/service price covers the issued billing period.");
        return covering[0];
    }

    private static IReadOnlyList<RoomChangeRosterMember> VerifyResidency(
        IReadOnlyCollection<RoomChangeRosterMember>? members)
    {
        if (members is null || members.Count is < 1 or > 100)
            throw new InvalidOperationException(
                "A bounded, server-loaded residency list is required to price an issued invoice.");
        var sorted = members.OrderBy(x => x.TenantId).ToArray();
        if (sorted.Any(x => x.TenantId == Guid.Empty ||
                x.Role is not (ContractTenantRole.PrimaryTenant or
                    ContractTenantRole.Member)) ||
            sorted.Select(x => x.TenantId).Distinct().Count() != sorted.Length ||
            sorted.Count(x => x.Role == ContractTenantRole.PrimaryTenant) != 1)
            throw new InvalidOperationException(
                "Residency list must hold unique tenants and exactly one primary tenant.");
        return sorted;
    }

    private static string Fingerprint(OfficialServicePricingSchedule schedule)
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new List<string>
        {
            schedule.IssuedInvoiceId.ToString("D"),
            schedule.CandidateId.ToString("D"),
            schedule.ContractId.ToString("D"),
            schedule.RoomId.ToString("D"),
            schedule.BuildingId.ToString("D"),
            schedule.BillingYear.ToString(c),
            schedule.BillingMonth.ToString(c),
            schedule.VerifiedResidencyCount.ToString(c),
            schedule.TotalVnd.ToString("0", c)
        };
        foreach (var line in schedule.Lines)
        {
            parts.Add(line.ServiceId.ToString("D"));
            parts.Add(line.ServiceCode);
            parts.Add(line.TariffVersionId.ToString("D"));
            parts.Add(line.TariffVersion.ToString(c));
            parts.Add(line.TariffSha256);
            parts.Add(((int)line.BillingUnit).ToString(c));
            parts.Add(((int)line.ChargingRule).ToString(c));
            parts.Add(line.UnitPriceVnd.ToString("0", c));
            parts.Add(line.ServiceFromVietnam.ToString("yyyy-MM-dd", c));
            parts.Add(line.ServiceThroughVietnam.ToString("yyyy-MM-dd", c));
            parts.Add(line.VerifiedOccupantCount.ToString(c));
            parts.Add(line.VerifiedMeterQuantity?
                .ToString("0.############################", c) ?? string.Empty);
            parts.Add(line.AmountVnd.ToString("0", c));
            parts.Add(line.SourceBindingSha256);
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();
    }
}
