using System;
using Volo.Abp;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// Guard against accidentally exposing unscoped contract/meter/price
/// details via ABP conventional HTTP controller generation.
/// Internal DI calls are intentionally still allowed.
/// </summary>
public sealed class InvoiceRemoteAccessGate_Tests
{
    [Theory]
    [InlineData(typeof(InvoiceDraftCandidateAppService))]
    [InlineData(typeof(InvoiceDraftStorageAppService))]
    [InlineData(typeof(StagedIssuedInvoiceDebtReviewAppService))]
    public void Invoice_services_remain_internal_until_operator_permissions_exist(Type serviceType)
    {
        var remote = (RemoteServiceAttribute?)Attribute.GetCustomAttribute(
            serviceType, typeof(RemoteServiceAttribute));
        Assert.NotNull(remote);
        Assert.False(remote.IsEnabled);
    }
}
