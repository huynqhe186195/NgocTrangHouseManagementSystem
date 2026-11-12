using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;

namespace BuildingManagement.Security;

/// <summary>
/// Domain-layer contract for atomically inserting a building and its
/// first server-identified owner grant. The actual runtime implementation
/// remains deny-all until membership storage and permissions are approved.
/// Placed in Domain so both Application and EntityFrameworkCore can consume
/// the contract without introducing an EF -> Application dependency.
/// </summary>
public interface IBuildingOwnerBootstrapWriter
{
    Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId,
        string name,
        string address,
        string? description);
}
