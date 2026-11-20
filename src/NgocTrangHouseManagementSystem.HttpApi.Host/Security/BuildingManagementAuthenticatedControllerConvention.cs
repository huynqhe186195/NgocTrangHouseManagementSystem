using System;
using System.Linq;
using BuildingManagement;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Payments;
using BuildingManagement.Tenants;
using BuildingManagement.OperatorMemberships;
using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace NgocTrangHouseManagementSystem;

/// <summary>
/// Defense-in-depth for conventional HTTP controllers generated from the
/// BuildingManagement application assembly. This establishes ONLY an
/// authenticated-user boundary. It does not authorize a building/room scope,
/// grant invoice revision rights or make remote-disabled services public.
/// </summary>
public sealed class BuildingManagementAuthenticatedControllerConvention
    : IApplicationModelConvention
{
    private static readonly AuthorizeFilter AuthenticatedOnly = new(
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

    // Default-deny ALL unreviewed conventional BuildingManagement
    // application services, including Slice C utilities and finance.
    // Login alone must never disclose meter usage, rates, payment records,
    // account details, tenant PII or allow inter-building mutations.
    // The allowlisted services below keep their service-level per-building
    // authorization (IBuildingCrudAccessPolicy + the write boundary).
    // BuildingPublicInfo is on the list because it enforces the same pair on
    // both of its actions: read needs ReadBuilding, publish needs ManageBuilding.
    // Explicit isolated staging adapters live in the host assembly and are NOT
    // unlocked by this.
    private static readonly AuthorizeFilter UnscopedApplicationDenied = new(
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(_ => false)
            .Build());

    internal static bool RequiresUnscopedApplicationDeny(Type type)
        => type.Assembly == typeof(BuildingManagementApplicationModule).Assembly &&
           type != typeof(BuildingAppService) &&
           type != typeof(FloorAppService) &&
           type != typeof(RoomAppService) &&
           type != typeof(BuildingPublicInfoAppService) &&
           // Read-only by construction: two Get methods, no writes. If a write
           // is ever added to this service it must leave this list first.
           type != typeof(ContractsAppService) &&
           // Same rule: the roster read only. TenantAppService, which writes
           // and returns every tenant unscoped, stays denied.
           type != typeof(TenantsAppService) &&
           // OperatorMembershipsAppService has exactly one read-only paged
           // method; adding a write must remove it from this allowlist first.
           type != typeof(OperatorMembershipsAppService);

    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var controller in application.Controllers)
        {
            // Include both conventionally generated application-service
            // controllers AND explicitly authored BuildingManagement.HttpApi
            // controllers (for example, the sample GET route).
            var assembly = controller.ControllerType.Assembly;
            if (assembly != typeof(BuildingManagementApplicationModule).Assembly &&
                assembly != typeof(BuildingManagementHttpApiModule).Assembly)
                continue;

            // ABP may retain a disabled RemoteService method as a
            // conventional MVC action without an attribute route. Depending
            // on endpoint routing, that action can still receive a route
            // endpoint. Remove the P0 direct-contract-end action from MVC
            // discovery entirely; the internal C# method still exists and
            // still has its own deny-all termination guard.
            if (controller.ControllerType.AsType() == typeof(ContractAppService))
            {
                var directEndActions = controller.Actions
                    .Where(action =>
                        action.ActionMethod.Name == nameof(ContractAppService.EndAsync))
                    .ToArray();
                foreach (var directEnd in directEndActions)
                    controller.Actions.Remove(directEnd);
            }

            // Explicit anonymous overrides can bypass an authorization filter.
            // A future anonymous business action requires a separate security
            // review instead of silently bypassing this default.
            if (controller.Attributes.OfType<IAllowAnonymous>().Any() ||
                controller.Actions.Any(action =>
                    action.Attributes.OfType<IAllowAnonymous>().Any()))
            {
                throw new InvalidOperationException(
                    $"Unreviewed AllowAnonymous on BuildingManagement controller: " +
                    controller.ControllerType.FullName);
            }

            controller.Filters.Add(AuthenticatedOnly);

            if (RequiresUnscopedApplicationDeny(
                    controller.ControllerType.AsType()))
                controller.Filters.Add(UnscopedApplicationDenied);
        }
    }
}
