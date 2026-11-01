using System;
using System.Data;
using System.Linq;
using System.Reflection;
using BuildingManagement.DepositSettlements;
using Volo.Abp;
using Volo.Abp.Uow;
using Xunit;

namespace BuildingManagement.Application.Tests.DepositSettlements;

public sealed class ContractDepositOfficialOffsetReadAppService_Tests
{
    [Fact]
    public void DEPREADER01_Internal_Service_Is_Never_Remote_Or_Financial_Writer()
    {
        var service = typeof(ContractDepositOfficialOffsetReadAppService);
        var remote = service.GetCustomAttribute<RemoteServiceAttribute>();
        Assert.NotNull(remote);
        Assert.False(remote!.IsEnabled);
        var model = new ContractDepositOfficialOffsetRead(
            Guid.NewGuid(), Guid.NewGuid(), 1_000_000m,
            Array.Empty<ContractDepositOffsetClaim>(),
            Array.Empty<string>());
        Assert.False(model.CanPostOffset);
        Assert.False(model.CanRefund);
    }

    [Fact]
    public void DEPREADER02_Contract_Wide_Reader_Requires_RepeatableRead_Uow()
    {
        var method = typeof(ContractDepositOfficialOffsetReadAppService)
            .GetMethod(nameof(ContractDepositOfficialOffsetReadAppService.GetAsync));
        Assert.NotNull(method);
        var uow = method!.GetCustomAttribute<UnitOfWorkAttribute>();
        Assert.NotNull(uow);
        Assert.True(uow!.IsTransactional);
        Assert.Equal(IsolationLevel.RepeatableRead, uow.IsolationLevel);
    }

    [Fact]
    public void DEPREADER03_Financial_Output_Cannot_Claim_Authority_Even_When_Reviewable()
    {
        var model = new ContractDepositOfficialOffsetRead(
            Guid.NewGuid(), Guid.NewGuid(), 3_000_000m,
            Array.Empty<ContractDepositOffsetClaim>(),
            Array.Empty<string>());
        Assert.True(model.EvidenceReviewable);
        Assert.False(model.CanPostOffset);
        Assert.False(model.CanRefund);
    }
}
