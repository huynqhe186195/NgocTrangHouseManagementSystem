using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Permissions;
using BuildingManagement.Rooms;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;

namespace BuildingManagement.Invoices;

/// <summary>
/// Defines which CAPABILITY is being checked. The client cannot supply
/// arbitrary ABP permission names. A positive response remains design-only.
/// </summary>
public enum InvoiceDraftRevisionCapability
{
    RebuildDraft = 1,
    ReadRevisionHistory = 2
}

public sealed record InvoiceDraftRevisionPrincipal(Guid OperatorId, bool PermissionGranted);

/// <summary>Gets identity/permission facts ONLY from server security context.</summary>
public interface IInvoiceDraftRevisionPrincipalReader
{
    Task<InvoiceDraftRevisionPrincipal?> ReadAsync(InvoiceDraftRevisionCapability capability);
}

public sealed class AbpInvoiceDraftRevisionPrincipalReader :
    IInvoiceDraftRevisionPrincipalReader, ITransientDependency
{
    private readonly ICurrentUser _user;
    private readonly IPermissionChecker _permissions;

    public AbpInvoiceDraftRevisionPrincipalReader(
        ICurrentUser user, IPermissionChecker permissions)
    {
        _user = user;
        _permissions = permissions;
    }

    public async Task<InvoiceDraftRevisionPrincipal?> ReadAsync(
        InvoiceDraftRevisionCapability capability)
    {
        if (!_user.Id.HasValue || _user.Id.Value == Guid.Empty)
            return null;

        var permission = capability switch
        {
            InvoiceDraftRevisionCapability.RebuildDraft =>
                BuildingManagementPermissions.Invoices.RebuildDraft,
            InvoiceDraftRevisionCapability.ReadRevisionHistory =>
                BuildingManagementPermissions.Invoices.ReadRevisionHistory,
            _ => null
        };

        if (permission is null)
            return null;

        return new InvoiceDraftRevisionPrincipal(
            _user.Id.Value,
            await _permissions.IsGrantedAsync(permission));
    }
}

/// <summary>
/// Server-read invoice hierarchy, never values submitted in a client DTO.
/// The reader checks existence; the pure policy checks all relationships.
/// A later mutating command MUST reread the graph after acquiring locks.
/// </summary>
public sealed record InvoiceDraftRevisionResourceGraph(
    Guid InvoiceId,
    Guid InvoiceContractId,
    Guid InvoiceRoomId,
    Guid ContractId,
    Guid ContractRoomId,
    Guid RoomId,
    Guid RoomFloorId,
    Guid FloorId,
    Guid FloorBuildingId,
    Guid BuildingId,
    InvoiceStatus InvoiceStatus);

public interface IInvoiceDraftRevisionResourceGraphReader
{
    Task<InvoiceDraftRevisionResourceGraph?> ReadAsync(Guid invoiceId);
}

public sealed class EfInvoiceDraftRevisionResourceGraphReader :
    IInvoiceDraftRevisionResourceGraphReader, ITransientDependency
{
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Building, Guid> _buildings;

    public EfInvoiceDraftRevisionResourceGraphReader(
        IRepository<Invoice, Guid> invoices,
        IRepository<Contract, Guid> contracts,
        IRepository<Room, Guid> rooms,
        IRepository<Floor, Guid> floors,
        IRepository<Building, Guid> buildings)
    {
        _invoices = invoices;
        _contracts = contracts;
        _rooms = rooms;
        _floors = floors;
        _buildings = buildings;
    }

    public async Task<InvoiceDraftRevisionResourceGraph?> ReadAsync(Guid invoiceId)
    {
        if (invoiceId == Guid.Empty)
            return null;

        var invoice = await _invoices.FindAsync(invoiceId);
        if (invoice is null)
            return null;

        var contract = await _contracts.FindAsync(invoice.ContractId);
        if (contract is null)
            return null;

        var room = await _rooms.FindAsync(invoice.RoomId);
        if (room is null)
            return null;

        var floor = await _floors.FindAsync(room.FloorId);
        if (floor is null)
            return null;

        var building = await _buildings.FindAsync(floor.BuildingId);
        if (building is null)
            return null;

        return new InvoiceDraftRevisionResourceGraph(
            invoice.Id, invoice.ContractId, invoice.RoomId,
            contract.Id, contract.RoomId, room.Id, room.FloorId,
            floor.Id, floor.BuildingId, building.Id, invoice.Status);
    }
}

/// <summary>
/// Must eventually be backed by a separately reviewed server-side
/// building-user authorization system. There is none in this module yet.
/// It MUST NOT use client-submitted IDs, a blanket admin role,
/// or infer access from visibility of a building.
/// </summary>
public interface IInvoiceDraftRevisionBuildingGrants
{
    Task<IReadOnlyCollection<Guid>> FindGrantedBuildingIdsAsync(Guid operatorId);
}

/// <summary>
/// Safe default until reviewed building membership storage and
/// authorization integration exists: every user has ZERO building grants.
/// This is a deliberate hard blocker for real revision operations.
/// </summary>
public sealed class DenyAllInvoiceDraftRevisionBuildingGrants :
    IInvoiceDraftRevisionBuildingGrants, ITransientDependency
{
    public Task<IReadOnlyCollection<Guid>> FindGrantedBuildingIdsAsync(Guid operatorId)
        => Task.FromResult<IReadOnlyCollection<Guid>>(Array.Empty<Guid>());
}

/// <summary>
/// Read-only composition root. It does not authenticate clients, assign
/// rights, modify invoices, issue bills, or approve any write operation.
/// Returns an advisory result, NOT an authorization token.
/// </summary>
public sealed class InvoiceDraftRevisionServerAccessGate : ITransientDependency
{
    private readonly IInvoiceDraftRevisionPrincipalReader _principal;
    private readonly IInvoiceDraftRevisionResourceGraphReader _resources;
    private readonly IInvoiceDraftRevisionBuildingGrants _grants;

    public InvoiceDraftRevisionServerAccessGate(
        IInvoiceDraftRevisionPrincipalReader principal,
        IInvoiceDraftRevisionResourceGraphReader resources,
        IInvoiceDraftRevisionBuildingGrants grants)
    {
        _principal = principal;
        _resources = resources;
        _grants = grants;
    }

    public async Task<InvoiceDraftRevisionAccessStatus> AssessDesignOnlyAsync(
        Guid invoiceId, InvoiceDraftRevisionCapability capability)
    {
        if (invoiceId == Guid.Empty ||
            (capability != InvoiceDraftRevisionCapability.RebuildDraft &&
             capability != InvoiceDraftRevisionCapability.ReadRevisionHistory))
            return InvoiceDraftRevisionAccessStatus.Denied;

        var actor = await _principal.ReadAsync(capability);
        if (actor is null || actor.OperatorId == Guid.Empty ||
            !actor.PermissionGranted)
            return InvoiceDraftRevisionAccessStatus.Denied;

        var graph = await _resources.ReadAsync(invoiceId);
        if (graph is null)
            return InvoiceDraftRevisionAccessStatus.Denied;

        // The grants source is deliberately empty until a reviewed
        // building membership implementation exists.
        var granted = await _grants.FindGrantedBuildingIdsAsync(actor.OperatorId);
        var facts = new InvoiceDraftRevisionAccessFacts(
            actor.OperatorId,
            actor.PermissionGranted,
            invoiceId,
            graph.InvoiceId,
            graph.InvoiceContractId,
            graph.InvoiceRoomId,
            graph.ContractId,
            graph.ContractRoomId,
            graph.RoomId,
            graph.RoomFloorId,
            graph.FloorId,
            graph.FloorBuildingId,
            graph.BuildingId,
            granted,
            graph.InvoiceStatus);

        return InvoiceDraftRevisionAccessPolicy.Evaluate(facts);
    }
}
