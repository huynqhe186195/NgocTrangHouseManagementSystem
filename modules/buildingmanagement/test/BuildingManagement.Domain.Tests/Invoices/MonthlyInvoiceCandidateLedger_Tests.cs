using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class MonthlyInvoiceCandidateLedger_Tests
{
    private static readonly DateTime ReviewedAt =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MONTHLYARCH01_Full_Month_Rent_Utilities_And_No_Fee_Decision_Are_Frozen()
    {
        var snapshot = Snapshot();
        var record = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), snapshot);
        var restored = record.VerifyStoredEvidence();

        Assert.Equal(snapshot.DraftInvoiceId, record.DraftInvoiceId);
        Assert.Equal(3_050_000m, record.TotalAmount);
        Assert.Equal(3_000_000m, record.RentAmount);
        Assert.Equal(35_000m, record.ElectricityAmount);
        Assert.Equal(15_000m, record.WaterAmount);
        Assert.Equal(64, record.FrozenEvidenceSha256.Length);
        Assert.Equal(2, restored.UtilityLines.Count);
        Assert.All(restored.UtilityLines, line => Assert.Single(line.Sources));
        Assert.False(record.IsIssued);
        Assert.False(record.IsOfficialReceivable);
        Assert.False(record.CanAllocatePayment);
        Assert.False(record.CanOffsetDeposit);
    }

    [Fact]
    public void MONTHLYARCH02_Modified_Header_Is_Detected()
    {
        var record = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), Snapshot());
        typeof(MonthlyInvoiceCandidateLedgerRecord)
            .GetProperty(nameof(MonthlyInvoiceCandidateLedgerRecord.TotalAmount))!
            .SetValue(record, record.TotalAmount + 1m);

        Assert.Throws<InvalidDataException>(() => record.VerifyStoredEvidence());
    }

    [Fact]
    public void MONTHLYARCH03_Valid_Hash_Alone_Cannot_Hide_Rewritten_Rent()
    {
        var record = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), Snapshot());
        var rewritten = JsonNode.Parse(record.FrozenEvidenceJson)!.AsObject();
        rewritten["RentAmount"] = 5_000_000m;
        rewritten["TotalAmount"] = 5_050_000m;
        var json = rewritten.ToJsonString();
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        typeof(MonthlyInvoiceCandidateLedgerRecord)
            .GetProperty(nameof(MonthlyInvoiceCandidateLedgerRecord.FrozenEvidenceJson))!
            .SetValue(record, json);
        typeof(MonthlyInvoiceCandidateLedgerRecord)
            .GetProperty(nameof(MonthlyInvoiceCandidateLedgerRecord.FrozenEvidenceSha256))!
            .SetValue(record, digest);

        Assert.Throws<InvalidDataException>(() => record.VerifyStoredEvidence());
    }

    [Fact]
    public void MONTHLYARCH04_Missing_Service_Review_Never_Becomes_Zero()
    {
        var invalid = Snapshot() with
        {
            AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.NotReviewed
        };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(Guid.NewGuid(), invalid));
    }

    [Fact]
    public void MONTHLYARCH05_Itemized_Fees_Require_Arithmetic_And_References()
    {
        var fee = new FinalInvoiceAuxiliaryChargeProposal(
            FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET", 2m,
            50_000m, 100_000m, "CONTRACT-INTERNET-RATE-001",
            "Internet monthly service");
        var valid = Snapshot() with
        {
            AuxiliaryFeeDecision = AuxiliaryFeeAssessmentDecision.ItemizedCharges,
            AuxiliaryCharges = new[] { fee },
            AuxiliaryAmount = 100_000m,
            TotalAmount = 3_150_000m
        };
        var archived = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), valid);
        Assert.Equal(3_150_000m, archived.TotalAmount);

        var mismatched = valid with
        {
            AuxiliaryCharges = new[] { fee with { Amount = 99_999m } }
        };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), mismatched));
        var duplicate = valid with { AuxiliaryCharges = new[] { fee, fee } };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), duplicate));
    }

    [Fact]
    public void MONTHLYARCH06_Physical_Meter_Source_Cannot_Be_Billed_Twice()
    {
        var original = Snapshot();
        var electricity = original.UtilityLines[0];
        var water = original.UtilityLines[1];
        var copied = water.Sources[0] with
        {
            SourceId = electricity.Sources[0].SourceId,
            SourceType = electricity.Sources[0].SourceType
        };
        var reused = original with
        {
            UtilityLines = new[] { electricity, water with
            {
                Sources = new[] { copied }
            } }
        };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), reused));
    }

    [Fact]
    public void MONTHLYARCH07_External_Mutable_List_Cannot_Rewrite_Archive()
    {
        var original = Snapshot();
        var mutable = original.UtilityLines.ToList();
        var snapshot = original with { UtilityLines = mutable };
        var record = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), snapshot);
        mutable.Clear();

        Assert.Equal(2, record.VerifyStoredEvidence().UtilityLines.Count);
    }

    [Fact]
    public void MONTHLYARCH08_Due_Date_Before_Review_Is_Rejected()
    {
        var invalid = Snapshot() with
        {
            DueDate = new DateOnly(2026, 10, 29)
        };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), invalid));
    }

    [Fact]
    public void MONTHLYARCH09_Manipulated_Meter_Price_Does_Not_Pass()
    {
        var original = Snapshot();
        var changed = original.UtilityLines[0] with { UnitPrice = 4_000m };
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), original with
                {
                    UtilityLines = new[] { changed, original.UtilityLines[1] }
                }));
    }

    private static MonthlyInvoiceCandidateSnapshot Snapshot()
    {
        var meterKind = Enum.GetValues<MeterUsageSourceType>().First();
        var electricity = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Electricity, Guid.NewGuid(), 10m, 3_500m,
            35_000m, new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 10, 31), 100m, 110m) });
        var water = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Water, Guid.NewGuid(), 1m, 15_000m,
            15_000m, new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 10, 31), 55m, 56m) });
        return new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), ReviewedAt,
            new DateOnly(2026, 11, 5), "APPROVED-DUE-POLICY-001",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "APPROVED-NO-SERVICE-FEES-001",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, new[] { electricity, water });
    }
}
