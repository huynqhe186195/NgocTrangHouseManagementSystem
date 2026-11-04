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
        Assert.Equal(28, methods.Length);
        Assert.Contains(methods, m => m.Name == "CompleteRoomChangeAsync");
        var h4Writer=Assert.Single(methods.Where(m =>
            m.Name=="CompleteH4MidMonthRoomChangeAsync"));
        Assert.Equal("{requestId:guid}/complete-h4-mid-month-room-change",
            h4Writer.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.NotNull(h4Writer.GetCustomAttribute<UnitOfWorkAttribute>());
        foreach (var name in new[] {
            "ArchiveExactPricingAsync",
            "ArchiveThreePartyHandoverAsync",
            "ArchiveClassifiedD03Async",
            "PostClassifiedD03Async"
        })
        {
            var money = Assert.Single(methods.Where(m => m.Name == name));
            Assert.NotNull(money.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(money.GetCustomAttribute<UnitOfWorkAttribute>());
        }
        var mid = Assert.Single(methods.Where(m =>
            m.Name == "CompleteMidMonthRoomChangeAsync"));
        Assert.NotNull(mid.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal("{requestId:guid}/complete-mid-month-room-change",
            mid.GetCustomAttribute<HttpPostAttribute>()?.Template);
        foreach (var (name, verb) in new[] {
            ("CaptureH4DispositionIntentAsync", "POST"),
            ("GetH4DispositionIntentAsync", "GET")
        })
        {
            var method = Assert.Single(methods.Where(m => m.Name == name));
            var uow = method.GetCustomAttribute<UnitOfWorkAttribute>();
            Assert.NotNull(uow);
            var routeAttribute = method.GetCustomAttributes()
                .OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                .Single();
            Assert.Equal(verb, Assert.Single(routeAttribute.HttpMethods));
            Assert.Equal("{requestId:guid}/h4-disposition-intent", routeAttribute.Template);
        }
        Assert.Contains(methods, m => m.Name == "GetExecutionReadinessAsync");
        var journal = Assert.Single(methods.Where(
            m => m.Name == "GetMidMonthFinancialJournalAsync"));
        Assert.NotNull(journal.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("{requestId:guid}/mid-month-financial-journal",
            journal.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.NotNull(journal.GetCustomAttribute<UnitOfWorkAttribute>());
        Assert.Contains(methods, m => m.Name == "AttestHandoverAsync");
        Assert.Contains(methods, m => m.Name == "GetHandoverAsync");
        Assert.Contains(methods, m => m.Name == "ProposeDayPricingRuleAsync");
        Assert.Contains(methods, m => m.Name == "GetDayPricingRuleAsync");
        Assert.Contains(methods, m => m.Name == "ReviewBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "GetBusinessPolicyReviewAsync");
        Assert.Contains(methods, m => m.Name == "ProposeBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "GetBusinessPolicyAsync");
        Assert.Contains(methods, m => m.Name == "CaptureRosterAsync");
        Assert.Contains(methods, m => m.Name == "GetRosterAsync");
        Assert.Contains(methods, m => m.Name == "GetLiveReadinessAsync");
        Assert.DoesNotContain(methods, m =>
            m.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            (m.Name.Contains("Complete", StringComparison.OrdinalIgnoreCase) &&
             m.Name != "CompleteRoomChangeAsync" &&
             m.Name != "CompleteMidMonthRoomChangeAsync" &&
             m.Name != "CompleteH4MidMonthRoomChangeAsync") ||
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
    public void TRFHTTP03_Operator_Financial_Journal_Is_Observational_Only()
    {
        var route = typeof(SliceBStagingRoomChangesController).GetMethod(
            nameof(SliceBStagingRoomChangesController.GetMidMonthFinancialJournalAsync));
        Assert.NotNull(route);
        Assert.NotNull(route!.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(route.GetCustomAttribute<UnitOfWorkAttribute>());

        var projection = new SliceBRoomChangeFinancialJournalRead(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2, -50000m, 1, 1000000m,
            IsActualOutboundBankRefundVerified: false,
            IsMidMonthContractCommitEnabled: false,
            CanExecuteTransfer: false);
        Assert.False(projection.CanExecuteTransfer);
        Assert.False(projection.IsActualOutboundBankRefundVerified);
        Assert.False(projection.IsMidMonthContractCommitEnabled);
        Assert.Equal(-50000m, projection.SignedCorrectionNetVnd);
    }

    [Fact]
    public void TRFHTTP02_Staging_Adapter_Has_No_Transfer_Or_Deposit_Writer()
    {
        var methods = typeof(SliceBStagingRoomChangeRequests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Select(m => m.Name).ToArray();
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.SubmitAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.ProposeDayPricingRuleAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.GetDayPricingRuleAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.CaptureH4DispositionIntentAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.GetH4DispositionIntentAsync), methods);
        Assert.Contains(nameof(SliceBStagingRoomChangeRequests.HoldForPolicyAsync), methods);
        Assert.DoesNotContain(methods, x =>
            x.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
            (x.Contains("Complete", StringComparison.OrdinalIgnoreCase) &&
             x != "CompleteRoomChangeAsync" &&
             x != "CompleteMidMonthRoomChangeAsync" &&
             x != "CompleteH4MidMonthRoomChangeAsync"));
    }
}
