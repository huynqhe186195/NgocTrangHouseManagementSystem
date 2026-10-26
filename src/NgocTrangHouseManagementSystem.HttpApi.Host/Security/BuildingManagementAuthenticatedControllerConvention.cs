using System;
using System.Linq;
using BuildingManagement;
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

    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var controller in application.Controllers)
        {
            if (controller.ControllerType.Assembly !=
                typeof(BuildingManagementApplicationModule).Assembly)
                continue;

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
        }
    }
}
