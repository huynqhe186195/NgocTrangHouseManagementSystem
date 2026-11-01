using System;
using System.Data;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.HttpApi.Host.Tests;

public sealed class SliceBOwnerDebtOffsetStageBoundary_Tests
{
    [Theory]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.ReviewDebtOffsetAsync))]
    [InlineData(nameof(SliceBStagingFinalInvoiceController.ApproveDebtOffsetAsync))]
    public void OI04AUTHHTTP01_Only_OptIn_Stage_Post_And_Serializable(string method)
    {
        var action=typeof(SliceBStagingFinalInvoiceController).GetMethod(method)!;
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
        var attr=action.GetCustomAttribute<UnitOfWorkAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr!.IsTransactional);
        Assert.Equal(IsolationLevel.Serializable,attr.IsolationLevel);
    }

    [Fact]
    public void OI04AUTHHTTP02_Approvals_Are_Explicitly_Not_Money_Postings()
    {
        var read=new SliceBOwnerDebtOffsetApprovalRead(Guid.NewGuid(),Guid.NewGuid(),
            100_000m,900_000m,0m,false,false);
        Assert.False(read.IsMoneyPosted);
        Assert.False(read.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04AUTHHTTP03_Review_And_Owner_Writers_Are_Separate()
    {
        var service=typeof(SliceBStagingFinalInvoiceMutation);
        Assert.Equal(typeof(Task<SliceBOffsetDebtReviewRead>),
            service.GetMethod(nameof(
                SliceBStagingFinalInvoiceMutation.ReviewDebtOffsetEvidenceAsync))!.ReturnType);
        Assert.Equal(typeof(Task<SliceBOwnerDebtOffsetApprovalRead>),
            service.GetMethod(nameof(
                SliceBStagingFinalInvoiceMutation.ApproveDebtOffsetAsync))!.ReturnType);
    }
}
