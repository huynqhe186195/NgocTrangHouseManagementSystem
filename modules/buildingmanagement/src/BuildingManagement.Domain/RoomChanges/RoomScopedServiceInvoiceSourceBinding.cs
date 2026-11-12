using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Invoices;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Immutable ORIGINAL-source binding between a monthly UNISSUED invoice
/// candidate's approved auxiliary line and the precise Room/Service tariff
/// version used for that line. This is not an invoice/receipt/issuer writer.
/// A future secured issuer must verify ALL bound lines and their financial
/// totals against the original candidate before any official posting.
/// </summary>
public sealed record RoomScopedServiceInvoiceSourceBinding(
    Guid MonthlyCandidateId,
    Guid ContractId,
    Guid RoomId,
    Guid BuildingId,
    int BillingYear,
    int BillingMonth,
    Guid ServiceId,
    string ServiceCode,
    Guid OriginalTariffVersionId,
    int OriginalTariffVersion,
    string OriginalTariffSha256,
    decimal OriginalFullPeriodUnitPriceVnd,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    DateOnly ServiceFromVietnam,
    DateOnly ServiceThroughVietnam,
    int VerifiedOccupantCount,
    decimal? VerifiedMeterQuantity,
    string VerifiedChargeSourceReference,
    decimal FrozenAuxiliaryLineAmountVnd,
    string OriginalMonthlyCandidateSha256,
    string EvidenceSha256)
{
    public bool IsOfficialIssuedInvoice => false;
    public bool IsPaymentReceived => false;
    public bool CanPostMoney => false;

    /// <summary>
    /// Computes a read-only source binding using the ORIGINAL verified
    /// monthly candidate and price version, never an editable current rate.
    /// The approved fee uses the existing candidate's extended/rounded line
    /// amount; its UnitPrice field is a billed item price, which can differ
    /// from the underlying monthly tariff for prorated service periods.
    /// </summary>
    public static RoomScopedServiceInvoiceSourceBinding Freeze(
        MonthlyInvoiceCandidateLedgerRecord? candidate,
        RoomScopedServiceTariffVersion? tariff,
        RoomScopedServiceChargePreview? preview,
        string? serviceCode,
        string? independentlyVerifiedSourceReference)
    {
        if (candidate is null || tariff is null || preview is null ||
            string.IsNullOrWhiteSpace(serviceCode) ||
            serviceCode.Trim().Length is < 1 or > 64 ||
            string.IsNullOrWhiteSpace(independentlyVerifiedSourceReference) ||
            independentlyVerifiedSourceReference.Trim().Length is < 12 or > 500)
            throw new InvalidOperationException(
                "Original monthly candidate, room tariff, approved service code and verified quantity source are required.");

        var original = candidate.VerifyStoredEvidence();
        tariff.VerifyFingerprint();
        var canonical = tariff.PreviewCharge(
            preview.BilledFromVietnam, preview.BilledThroughVietnam,
            preview.IndependentlyVerifiedOccupantCount,
            preview.IndependentlyVerifiedMeterQuantity);
        if (canonical != preview ||
            tariff.RoomId != candidate.RoomId ||
            candidate.BillingYear != preview.BilledFromVietnam.Year ||
            candidate.BillingMonth != preview.BilledFromVietnam.Month ||
            candidate.BillingYear != preview.BilledThroughVietnam.Year ||
            candidate.BillingMonth != preview.BilledThroughVietnam.Month ||
            tariff.ApprovedAtUtc > candidate.ReviewedAtUtc ||
            original.AuxiliaryFeeDecision !=
                AuxiliaryFeeAssessmentDecision.ItemizedCharges)
            throw new InvalidOperationException(
                "Service evidence must come from the original effective room-specific rate and reviewed invoice month.");

        var lines = original.AuxiliaryCharges.Where(x =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.ServiceCode.Trim(), serviceCode.Trim())).ToArray();

        if (lines.Length != 1 ||
            lines[0].ContractOrRateReference != tariff.SignedPolicyReference ||
            lines[0].Amount != canonical.ProposedChargeVnd ||
            lines[0].Amount <= 0m ||
            original.AuxiliaryAmount < lines[0].Amount)
            throw new InvalidOperationException(
                "Frozen auxiliary line does not reconcile to its signed room tariff and independently verified quantity.");

        var bound = new RoomScopedServiceInvoiceSourceBinding(
            candidate.Id, candidate.ContractId, candidate.RoomId,
            candidate.BuildingId, candidate.BillingYear,
            candidate.BillingMonth, tariff.ServiceId,
            lines[0].ServiceCode.Trim(), tariff.Id, tariff.Version,
            tariff.EvidenceSha256, tariff.UnitPriceVnd,
            tariff.BillingUnit, tariff.ChargingRule,
            preview.BilledFromVietnam, preview.BilledThroughVietnam,
            preview.IndependentlyVerifiedOccupantCount,
            preview.IndependentlyVerifiedMeterQuantity,
            independentlyVerifiedSourceReference.Trim(),
            preview.ProposedChargeVnd,
            candidate.FrozenEvidenceSha256, string.Empty);
        return bound with { EvidenceSha256 = bound.Fingerprint() };
    }

    public void VerifySourceFingerprint()
    {
        if (MonthlyCandidateId == Guid.Empty || ContractId == Guid.Empty ||
            RoomId == Guid.Empty || BuildingId == Guid.Empty ||
            ServiceId == Guid.Empty || OriginalTariffVersionId == Guid.Empty ||
            EvidenceSha256 != Fingerprint())
            throw new InvalidDataException(
                "Original room/service invoice source binding was modified.");
    }

    private string Fingerprint()
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new[] {
            MonthlyCandidateId.ToString("D"), ContractId.ToString("D"),
            RoomId.ToString("D"), BuildingId.ToString("D"),
            BillingYear.ToString(c), BillingMonth.ToString(c),
            ServiceId.ToString("D"), ServiceCode,
            OriginalTariffVersionId.ToString("D"), OriginalTariffVersion.ToString(c),
            OriginalTariffSha256,
            OriginalFullPeriodUnitPriceVnd.ToString("0",c),
            ((int)BillingUnit).ToString(c),
            ((int)ChargingRule).ToString(c),
            ServiceFromVietnam.ToString("yyyy-MM-dd",c),
            ServiceThroughVietnam.ToString("yyyy-MM-dd",c),
            VerifiedOccupantCount.ToString(c),
            VerifiedMeterQuantity?.ToString("0.############################",c)
                ?? string.Empty,
            VerifiedChargeSourceReference,
            FrozenAuxiliaryLineAmountVnd.ToString("0",c),
            OriginalMonthlyCandidateSha256
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n",parts)))).ToLowerInvariant();
    }
}
