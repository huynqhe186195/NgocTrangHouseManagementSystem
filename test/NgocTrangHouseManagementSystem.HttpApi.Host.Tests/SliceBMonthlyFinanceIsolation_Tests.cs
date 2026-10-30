using System;
using System.Data;
using System.Linq;
using BuildingManagement.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// 2G.3 guard against lost credit/receipt balance checks under PostgreSQL
/// REPEATABLE READ snapshots. Both balance-changing endpoints must use
/// SERIALIZABLE, so SSI aborts one conflicting writer at commit (40001).
/// This is intentionally independent of a database or production host.
/// </summary>
public sealed class SliceBMonthlyFinanceIsolation_Tests
{
    [Theory]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.RecordMonthlyReceiptAsync))]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.ReviewMonthlyAdjustmentAsync))]
    public void MONTHLYISO01_BalanceWriters_Require_Serializable_Uow(string endpoint)
        => AssertIsolation(endpoint, IsolationLevel.Serializable);

    [Theory]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.PrepareMonthlyCandidateAsync))]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.IssueMonthlyAsync))]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.GetMonthlyBalanceAsync))]
    public void MONTHLYISO02_SnapshotReads_Keep_RepeatableRead_Uow(string endpoint)
        => AssertIsolation(endpoint, IsolationLevel.RepeatableRead);

    [Fact]
    public void MONTHLYISO03_Monthly_Finance_Is_Not_Enabled_Outside_Isolated_Staging()
    {
        var controller = new SliceBStagingFinalInvoiceController(
            new ProductionEnvironment(), new ConfigurationBuilder().Build());
        Assert.True(typeof(SliceBStagingFinalInvoiceController)
            .IsDefined(typeof(AuthorizeAttribute), inherit: true));

        // Controller performs its deny-all gate synchronously, before
        // returning the Task; use an Action (not Func<Task>) to assert it.
        Assert.Throws<AbpAuthorizationException>(() =>
        {
            _ = controller.RecordMonthlyReceiptAsync(
                Guid.NewGuid(), Guid.NewGuid(),
                new SliceBFinalInvoiceManualReceiptInput());
        });

        Assert.Throws<AbpAuthorizationException>(() =>
        {
            _ = controller.ReviewMonthlyAdjustmentAsync(
                Guid.NewGuid(), Guid.NewGuid(),
                new SliceBMonthlyAdjustmentDecisionInput());
        });
    }

    private static void AssertIsolation(string methodName, IsolationLevel expected)
    {
        var method = typeof(SliceBStagingFinalInvoiceController).GetMethod(methodName);
        Assert.NotNull(method);
        var attribute = Assert.Single(method.GetCustomAttributesData()
            .Where(x => x.AttributeType == typeof(UnitOfWorkAttribute)));
        Assert.Equal(2, attribute.ConstructorArguments.Count);
        Assert.Equal(true, attribute.ConstructorArguments[0].Value);
        Assert.Equal((int)expected,
            Convert.ToInt32(attribute.ConstructorArguments[1].Value));
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
    }
}
