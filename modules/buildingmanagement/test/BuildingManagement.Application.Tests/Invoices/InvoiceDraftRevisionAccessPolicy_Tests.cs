using System;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// PURE security design tests: no database queries, role assignments,
/// remote routes, invoice mutations, or production credentials.
/// All supplied facts simulate trusted server-side resolution.
/// </summary>
public sealed class InvoiceDraftRevisionAccessPolicy_Tests
{
    [Fact]
    public void ACCESS01_FullyMatchingServerScope_Is_Only_DesignEligible()
    {
        Assert.Equal(InvoiceDraftRevisionAccessStatus.EligibleForDesignOnly,
            InvoiceDraftRevisionAccessPolicy.Evaluate(ValidFacts()));
    }

    [Fact]
    public void ACCESS02_Anonymous_Or_Empty_Actor_Is_Denied()
    {
        var valid = ValidFacts();
        AssertDenied(valid with { OperatorId = null });
        AssertDenied(valid with { OperatorId = Guid.Empty });
    }

    [Fact]
    public void ACCESS03_Permission_Is_Required_Even_With_Building_Scope()
    {
        AssertDenied(ValidFacts() with { PermissionGranted = false });
    }

    [Fact]
    public void ACCESS04_No_Building_Grant_Or_Cross_Building_Is_Denied()
    {
        var valid = ValidFacts();
        AssertDenied(valid with { AuthorizedBuildingIds = null });
        AssertDenied(valid with { AuthorizedBuildingIds = Array.Empty<Guid>() });
        AssertDenied(valid with { AuthorizedBuildingIds = new[] { Guid.NewGuid() } });
    }

    [Fact]
    public void ACCESS05_Spoofed_Invoice_Id_Is_Denied()
    {
        AssertDenied(ValidFacts() with { RequestedInvoiceId = Guid.NewGuid() });
    }

    [Fact]
    public void ACCESS06_Invoice_Contract_And_Room_Relationships_Must_Match()
    {
        var valid = ValidFacts();
        AssertDenied(valid with { InvoiceContractId = Guid.NewGuid() });
        AssertDenied(valid with { InvoiceRoomId = Guid.NewGuid() });
        AssertDenied(valid with { ContractRoomId = Guid.NewGuid() });
    }

    [Fact]
    public void ACCESS07_Room_Floor_Building_Relationships_Must_Match()
    {
        var valid = ValidFacts();
        AssertDenied(valid with { RoomFloorId = Guid.NewGuid() });
        AssertDenied(valid with { FloorBuildingId = Guid.NewGuid() });
    }

    [Fact]
    public void ACCESS08_NonDraft_Invoice_Is_Denied()
    {
        AssertDenied(ValidFacts() with { InvoiceStatus = (InvoiceStatus)99 });
    }

    [Fact]
    public void ACCESS09_Missing_Server_Resolution_Fails_Closed()
    {
        var valid = ValidFacts();
        AssertDenied(null);
        AssertDenied(valid with { ResolvedInvoiceId = Guid.Empty });
        AssertDenied(valid with { ContractId = Guid.Empty });
        AssertDenied(valid with { RoomId = Guid.Empty });
        AssertDenied(valid with { BuildingId = Guid.Empty });
    }

    [Fact]
    public void ACCESS10_Access_Grant_Must_Match_Resolved_Building()
    {
        var valid = ValidFacts();
        var other = Guid.NewGuid();
        AssertDenied(valid with
        {
            BuildingId = other,
            FloorBuildingId = other
        });
    }

    private static void AssertDenied(InvoiceDraftRevisionAccessFacts? facts)
    {
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            InvoiceDraftRevisionAccessPolicy.Evaluate(facts));
    }

    private static InvoiceDraftRevisionAccessFacts ValidFacts()
    {
        var invoice = Guid.NewGuid();
        var contract = Guid.NewGuid();
        var room = Guid.NewGuid();
        var floor = Guid.NewGuid();
        var building = Guid.NewGuid();

        return new InvoiceDraftRevisionAccessFacts(
            OperatorId: Guid.NewGuid(),
            PermissionGranted: true,
            RequestedInvoiceId: invoice,
            ResolvedInvoiceId: invoice,
            InvoiceContractId: contract,
            InvoiceRoomId: room,
            ContractId: contract,
            ContractRoomId: room,
            RoomId: room,
            RoomFloorId: floor,
            FloorId: floor,
            FloorBuildingId: building,
            BuildingId: building,
            AuthorizedBuildingIds: new[] { building },
            InvoiceStatus: InvoiceStatus.Draft);
    }
}
