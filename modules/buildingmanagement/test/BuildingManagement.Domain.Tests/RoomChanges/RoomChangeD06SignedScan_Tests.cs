using System;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

/// <summary>
/// D06 scanned signed handover sheet + Owner document download trail.
/// The scan proves a document was archived against a real three-party
/// signoff; it never proves who signed and never executes anything.
/// </summary>
public sealed class RoomChangeD06SignedScan_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string Doc =
        new('a', 64);
    private static readonly string Vault =
        Guid.NewGuid().ToString("N");

    private sealed record Facts(
        RoomChangeRequest Request, RoomChangeEvidenceArchiveRecord Handover);

    private static Facts Create()
    {
        var source = new Contract(Guid.NewGuid(), "D06-SIGNED-SOURCE-001",
            Guid.NewGuid(), new DateTime(2026, 10, 1), null, 3_000_000m);
        source.Activate();
        var request = RoomChangeRequest.Submit(Guid.NewGuid(), source.Id,
            source.RoomId, Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 11, 30),
            "Reviewed contract change with exact signed day-proration",
            Guid.NewGuid(), Now, ContractStatus.Active,
            RoomStatus.Occupied, RoomStatus.Available);
        var primary = Guid.NewGuid();
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, new[] {
                new RoomChangeRosterMember(primary, ContractTenantRole.PrimaryTenant)
            }, Guid.NewGuid(), Now.AddSeconds(1));
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.AgreedDayProration,
            RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-NEW-CONTRACT-TRANSFER-2026",
            "SIGNED-CHARGE-DAY-AND-ROUNDING-2026",
            "SIGNED-TARGET-DEPOSIT-REQUIREMENT-2026",
            "SIGNED-ALL-TENANTS-TRANSFER-2026",
            Guid.NewGuid(), Now.AddSeconds(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster,
            Guid.NewGuid(), Now.AddSeconds(3), true,
            "INDEPENDENT-OWNER-OI07-REVIEW-2026",
            "Owner independently reviewed the original transfer policy");
        var electricity = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Electricity, "D06-E-METER-001", 0m,
            new DateTime(2026, 10, 1));
        var water = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Water, "D06-W-METER-001", 0m,
            new DateTime(2026, 10, 1));
        var e = new MeterReading(Guid.NewGuid(), electricity.Id,
            2026, 11, 10m, 80m, new DateTime(2026, 11, 30));
        var w = new MeterReading(Guid.NewGuid(), water.Id,
            2026, 11, 5m, 35m, new DateTime(2026, 11, 30));
        var physical = RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), request, electricity, e, water, w,
            "SIGNED-ASSET-INSPECTION-D06-2026",
            "SIGNED-METER-HANDOVER-D06-2026",
            Guid.NewGuid(), Now.AddSeconds(5));
        var signed = RoomChangeHandoverThreePartySignoff.Sign(
            Guid.NewGuid(), request, roster, physical,
            review.ReviewedByUserId, physical.AttestedByUserId, primary,
            "OWNER-SIGNED-TRANSFER-CHECKLIST-2026",
            "STAFF-SIGNED-METER-AND-ASSET-2026",
            "TENANT-SIGNED-ROOM-HANDOVER-2026",
            "SIGNED-DOCUMENTED-ASSET-DEFECT-REVIEW-2026",
            Now.AddSeconds(8));
        var archive = RoomChangeEvidenceArchiveRecord.CaptureThreePartyHandover(
            Guid.NewGuid(), request, roster, physical,
            electricity, e, water, w, signed, Now.AddSeconds(9));
        return new Facts(request, archive);
    }

    private static RoomChangeSignedHandoverScanRecord Capture(Facts f,
        string? documentSha = null, string? mediaType = null,
        int byteLength = 2048, string? vault = null,
        DateTime? at = null, Guid? uploader = null,
        RoomChangeEvidenceArchiveRecord? archive = null)
        => RoomChangeSignedHandoverScanRecord.Capture(
            Guid.NewGuid(), f.Request, archive ?? f.Handover,
            documentSha ?? Doc, mediaType ?? "application/pdf", byteLength,
            vault ?? Vault, uploader ?? Guid.NewGuid(),
            at ?? Now.AddSeconds(10));

    [Fact]
    public void TRFD06S01_Scan_Is_Bound_To_The_Archived_Three_Party_Handover()
    {
        var f = Create();
        var scan = Capture(f);
        scan.Verify(f.Request, f.Handover);
        Assert.Equal(f.Handover.RecordSha256, scan.HandoverEvidenceSha256);
        Assert.Equal(f.Request.Id, scan.RequestId);
        Assert.Equal("application/pdf", scan.MediaType);
        Assert.Equal(64, scan.EvidenceSha256.Length);
        Assert.False(scan.ProvesSignerIdentity);
        Assert.False(scan.CanExecuteRoomChange);
    }

    [Fact]
    public void TRFD06S02_Scan_Against_A_Different_Request_Is_Denied()
    {
        var a = Create();
        var b = Create();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeSignedHandoverScanRecord.Capture(
                Guid.NewGuid(), b.Request, a.Handover,
                Doc, "application/pdf", 2048, Vault, Guid.NewGuid(),
                Now.AddSeconds(10)));
    }

    [Fact]
    public void TRFD06S03_Only_Real_Scan_Formats_Are_Accepted()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, mediaType: "text/plain"));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, mediaType: "application/msword"));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, mediaType: string.Empty));
        Capture(f, mediaType: "image/png");
        Capture(f, mediaType: "image/jpeg");
    }

    [Fact]
    public void TRFD06S04_Document_Digest_Must_Be_A_Real_Sha256()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() => Capture(f, documentSha: "abc"));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, documentSha: new string('Z', 64)));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, documentSha: new string('A', 64)));
    }

    [Fact]
    public void TRFD06S05_Byte_Bounds_Are_Enforced()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() => Capture(f, byteLength: 100));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, byteLength: 5_242_881));
    }

    [Fact]
    public void TRFD06S06_Vault_Handle_Must_Be_An_Opaque_32_Hex_Reference()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() => Capture(f, vault: "short"));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, vault: "../../etc/passwd"));
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, vault: Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void TRFD06S07_Upload_Cannot_Precede_The_Handover_Archive()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, at: Now));
    }

    [Fact]
    public void TRFD06S08_Empty_Uploader_Is_Denied()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            Capture(f, uploader: Guid.Empty));
    }

    [Fact]
    public void TRFD06S09_Detached_Scan_Is_Rejected_On_Verify()
    {
        var f = Create();
        var scan = Capture(f);
        Assert.Throws<System.IO.InvalidDataException>(() =>
            scan.Verify(Create().Request, f.Handover));
    }

    [Fact]
    public void TRFD06S10_A_Download_Trail_Needs_Purpose_Sha_And_A_Real_File()
    {
        var contract = Guid.NewGuid();
        var building = Guid.NewGuid();
        var user = Guid.NewGuid();
        var record = RoomChangeContractDocumentAccessRecord.Record(
            Guid.NewGuid(), contract, building, user,
            "Owner exported the contract document for review",
            Doc, 4096, Now);
        record.Verify(contract, building);
        Assert.False(record.GrantsAccess);

        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeContractDocumentAccessRecord.Record(
                Guid.NewGuid(), contract, building, user, "too short",
                Doc, 4096, Now));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeContractDocumentAccessRecord.Record(
                Guid.NewGuid(), contract, building, user,
                "Owner exported the contract document for review",
                "not-a-sha", 4096, Now));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeContractDocumentAccessRecord.Record(
                Guid.NewGuid(), contract, building, user,
                "Owner exported the contract document for review",
                Doc, 100, Now));
    }
}
