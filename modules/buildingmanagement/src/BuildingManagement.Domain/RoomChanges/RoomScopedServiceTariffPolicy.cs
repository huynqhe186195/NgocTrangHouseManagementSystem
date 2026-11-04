using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// The same service can have a different price for every room. The billing
/// unit and monthly charge rule are explicit instead of inferred globally.
/// </summary>
public enum RoomScopedServiceBillingUnit
{
    PerPerson = 1,
    PerRoom = 2,
    MeteredUnit = 3
}

public enum RoomScopedServiceChargingRule
{
    WholeMonth = 1,
    CalendarDayProration = 2,
    VerifiedMeterUsage = 3
}

/// <summary>
/// A versioned D03/H3 room-specific service tariff source snapshot.
/// Owners can create a future effective version, but existing invoice
/// snapshots must continue to point to their ORIGINAL tariff version.
/// The server must persist these in an append-only tariff history and
/// authorize/verify actual occupancy and meter evidence independently.
/// A SHA-256 digest detects drift; it is not a human signature.
/// </summary>
public sealed record RoomScopedServiceTariffVersion(
    Guid Id,
    Guid RoomId,
    Guid ServiceId,
    int Version,
    decimal UnitPriceVnd,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveThrough,
    Guid ApprovedByUserId,
    DateTime ApprovedAtUtc,
    string SignedPolicyReference,
    string EvidenceSha256)
{
    public static RoomScopedServiceTariffVersion Create(
        Guid id, Guid roomId, Guid serviceId, int version,
        decimal unitPriceVnd, RoomScopedServiceBillingUnit billingUnit,
        RoomScopedServiceChargingRule chargingRule,
        DateOnly effectiveFrom, DateOnly? effectiveThrough,
        Guid approvedByUserId, DateTime approvedAtUtc,
        string? signedPolicyReference)
    {
        if (id == Guid.Empty || roomId == Guid.Empty ||
            serviceId == Guid.Empty || approvedByUserId == Guid.Empty ||
            version <= 0 || unitPriceVnd < 0m ||
            decimal.Truncate(unitPriceVnd) != unitPriceVnd ||
            !Enum.IsDefined(billingUnit) || !Enum.IsDefined(chargingRule) ||
            effectiveFrom == default ||
            effectiveThrough.HasValue && effectiveThrough.Value < effectiveFrom ||
            approvedAtUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(signedPolicyReference) ||
            signedPolicyReference.Trim().Length is < 12 or > 500 ||
            (billingUnit == RoomScopedServiceBillingUnit.MeteredUnit) !=
            (chargingRule == RoomScopedServiceChargingRule.VerifiedMeterUsage))
            throw new InvalidOperationException(
                "Room service tariff requires an identified room, explicit billing basis, effective dates and approved rate.");

        var row = new RoomScopedServiceTariffVersion(
            id, roomId, serviceId, version, unitPriceVnd,
            billingUnit, chargingRule, effectiveFrom, effectiveThrough,
            approvedByUserId, approvedAtUtc, signedPolicyReference.Trim(),
            EvidenceSha256: string.Empty);
        return row with { EvidenceSha256 = row.CalculateFingerprint() };
    }

    public void VerifyFingerprint()
    {
        if (EvidenceSha256 != CalculateFingerprint() ||
            Id == Guid.Empty || RoomId == Guid.Empty || ServiceId == Guid.Empty)
            throw new InvalidOperationException(
                "Room-specific signed service-tariff snapshot has drifted.");
    }

    public static void RequireNonOverlapping(
        RoomScopedServiceTariffVersion existing,
        RoomScopedServiceTariffVersion candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        existing.VerifyFingerprint();
        candidate.VerifyFingerprint();
        if (existing.RoomId != candidate.RoomId ||
            existing.ServiceId != candidate.ServiceId)
            return;

        var oldEnd = existing.EffectiveThrough ?? DateOnly.MaxValue;
        var newEnd = candidate.EffectiveThrough ?? DateOnly.MaxValue;
        if (existing.Version == candidate.Version ||
            existing.EffectiveFrom <= newEnd &&
            candidate.EffectiveFrom <= oldEnd)
            throw new InvalidOperationException(
                "One room/service cannot have two overlapping active tariff versions.");
    }

    public RoomScopedServiceChargePreview PreviewCharge(
        DateOnly billedFrom, DateOnly billedThrough,
        int independentlyVerifiedOccupantCount,
        decimal? independentlyVerifiedMeterQuantity = null)
    {
        VerifyFingerprint();
        if (billedFrom == default || billedThrough < billedFrom ||
            billedFrom.Year != billedThrough.Year ||
            billedFrom.Month != billedThrough.Month ||
            billedFrom < EffectiveFrom ||
            EffectiveThrough.HasValue && billedThrough > EffectiveThrough.Value ||
            independentlyVerifiedOccupantCount < 0)
            throw new InvalidOperationException(
                "Service billing must use one Vietnam calendar month and one effective room tariff version.");

        decimal multiplier;
        if (BillingUnit == RoomScopedServiceBillingUnit.PerPerson)
        {
            if (independentlyVerifiedOccupantCount == 0 ||
                independentlyVerifiedMeterQuantity.HasValue)
                throw new InvalidOperationException(
                    "Per-person services require verified actual tenant headcount.");
            multiplier = independentlyVerifiedOccupantCount;
        }
        else if (BillingUnit == RoomScopedServiceBillingUnit.PerRoom)
        {
            if (independentlyVerifiedMeterQuantity.HasValue)
                throw new InvalidOperationException(
                    "Per-room services cannot disguise meter values as new fees.");
            multiplier = 1m;
        }
        else
        {
            if (!independentlyVerifiedMeterQuantity.HasValue ||
                independentlyVerifiedMeterQuantity.Value < 0m)
                throw new InvalidOperationException(
                    "Metered room services require independently verified usage.");
            multiplier = independentlyVerifiedMeterQuantity.Value;
        }

        decimal amount;
        try
        {
            var gross = checked(UnitPriceVnd * multiplier);
            var charge = ChargingRule == RoomScopedServiceChargingRule.CalendarDayProration
                ? checked(gross *
                    (billedThrough.DayNumber - billedFrom.DayNumber + 1) /
                    DateTime.DaysInMonth(billedFrom.Year, billedFrom.Month))
                : gross;
            amount = decimal.Round(charge, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                "Room service charge cannot overflow VND accounting.", ex);
        }

        return new RoomScopedServiceChargePreview(
            RoomId, ServiceId, Id, Version, billedFrom, billedThrough,
            independentlyVerifiedOccupantCount,
            independentlyVerifiedMeterQuantity, BillingUnit, ChargingRule,
            UnitPriceVnd, amount, EvidenceSha256);
    }

    private string CalculateFingerprint()
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new[] {
            Id.ToString("D"), RoomId.ToString("D"), ServiceId.ToString("D"),
            Version.ToString(c), UnitPriceVnd.ToString("0", c),
            ((int)BillingUnit).ToString(c),
            ((int)ChargingRule).ToString(c),
            EffectiveFrom.ToString("yyyy-MM-dd", c),
            EffectiveThrough?.ToString("yyyy-MM-dd", c) ?? string.Empty,
            ApprovedByUserId.ToString("D"), ApprovedAtUtc.ToString("O", c),
            SignedPolicyReference
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();
    }
}

/// <summary>
/// Read-only calculation for one room/service/tariff version. Not a posted
/// invoice; the authenticated adapter verifies occupant and meter sources.
/// </summary>
public sealed record RoomScopedServiceChargePreview(
    Guid RoomId,
    Guid ServiceId,
    Guid TariffVersionId,
    int TariffVersion,
    DateOnly BilledFromVietnam,
    DateOnly BilledThroughVietnam,
    int IndependentlyVerifiedOccupantCount,
    decimal? IndependentlyVerifiedMeterQuantity,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    decimal UnitPriceVnd,
    decimal ProposedChargeVnd,
    string OriginalTariffSha256)
{
    public bool IsIssuedInvoice => false;
    public bool IsBankReceipt => false;
    public bool CanPost => false;
}
