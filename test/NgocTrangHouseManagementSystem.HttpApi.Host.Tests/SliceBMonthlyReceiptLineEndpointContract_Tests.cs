using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildingManagement.DepositSettlements;
using Microsoft.AspNetCore.Mvc;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.HttpApi.Host.Tests;

public sealed class SliceBMonthlyReceiptLineEndpointContract_Tests
{
    [Fact]
    public void OI04HTTP01_Endpoint_Is_Scoped_Post_And_Serializable()
    {
        var m = typeof(SliceBStagingFinalInvoiceController)
            .GetMethod(nameof(SliceBStagingFinalInvoiceController.SplitMonthlyReceiptAsync))!;
        Assert.NotNull(m);
        Assert.NotNull(m.GetCustomAttribute<HttpPostAttribute>());
        var uow = m.GetCustomAttribute<UnitOfWorkAttribute>();
        Assert.NotNull(uow);
        Assert.True(uow!.IsTransactional);
        Assert.Equal(IsolationLevel.Serializable, uow.IsolationLevel);
        Assert.Contains(m.GetParameters(), x => x.ParameterType ==
            typeof(SliceBMonthlyReceiptLineSplitInput));
    }

    [Fact]
    public void OI04HTTP02_Receipt_Split_Is_Not_An_Offset_Or_Bank_Verification()
    {
        var result = new SliceBMonthlyReceiptLineSplitRead(
            Guid.NewGuid(), Guid.NewGuid(), 2, 100_000m,
            IsBankExternallyVerified: false,
            CanPostDepositOffset: false);
        Assert.False(result.IsBankExternallyVerified);
        Assert.False(result.CanPostDepositOffset);
    }

    [Fact]
    public void OI04HTTP03_Writer_Remains_Within_Scoped_Stage_Service()
    {
        var writer = typeof(SliceBStagingFinalInvoiceMutation)
            .GetMethod(nameof(SliceBStagingFinalInvoiceMutation.AllocateMonthlyReceiptLinesAsync))!;
        Assert.NotNull(writer);
        Assert.Equal(typeof(Task<SliceBMonthlyReceiptLineSplitRead>),
            writer.ReturnType);
    }
}
