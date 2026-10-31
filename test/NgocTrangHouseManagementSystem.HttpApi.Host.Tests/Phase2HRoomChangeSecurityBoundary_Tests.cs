using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

public sealed class Phase2HRoomChangeSecurityBoundary_Tests
{
    [Fact]
    public void TRFHTTP01_Routes_Are_Authenticated_Staging_Only_And_NonExecuting()
    {
        var controller = typeof(SliceBStagingRoomChangesController);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<ApiControllerAttribute>());
        var route = controller.GetCustomAttribute<RouteAttribute>();
        Assert.Equal("api/slice-b-staging/contracts/{contractId:guid}/room-changes",
            route?.Template);

        var methods = controller.GetMethods(BindingFlags.Public |
            BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(13, methods.Length);
        Assert.Contains(methods, m => m.Name == "ReviewBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "GetBusinessPolicyReviewAsync");
        Assert.Contains(methods, m => m.Name == "ProposeBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "GetBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "CaptureRosterAsync");
        Assert.Contains(methods, m => m.Name == "GetRosterAsync");
        Assert.Contains(methods, m => m.Name == "GetLiveReadinessAsync");
        Assert.DoesNotContain(methods, m =>
            m.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Complete", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("CarryForward", StringComparison.OrdinalIgnoreCase));

        foreach (var method in methods)
        {
            Assert.NotNull(method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>());
            Assert.NotNull(method.GetCustomAttribute<UnitOfWorkAttribute>());
        }
    }

    [Fact]
    public void TRFHTTP02_Staging_Adapter_Has_No_Transfer_Or_Deposit_Writer()
    {
        var methods = typeof(SliceBStagingRoomChangeRequests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Select(m => m.Name).ToArray();
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.SubmitAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.HoldForPolicyAsync), methods);
        Assert.DoesNotContain(methods, x =>
            x.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Complete", StringComparison.OrdinalIgnoreCase));
    }
}
