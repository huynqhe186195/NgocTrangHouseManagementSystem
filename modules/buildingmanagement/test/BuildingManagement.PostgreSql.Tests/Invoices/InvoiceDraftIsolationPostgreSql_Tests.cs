using System;
using System.Data;
using System.Threading.Tasks;
using BuildingManagement.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Uow;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real PostgreSQL isolation gate. Uses only the dedicated localhost test DB
/// and nonexistent contract IDs. No persistent business data is changed.
/// Both app calls occur inside an explicit pre-existing ABP UOW to verify
/// that their method attributes are not silently relied upon.
/// </summary>
[Collection("Phase2G2A_UtilityIntegrity")]
public sealed class InvoiceDraftIsolationPostgreSql_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementPostgreSqlTestModule>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PGINV02_Ambient_ReadCommitted_Is_Rejected_Before_Draft_Data_Read(
        bool storage)
    {
        using var scope = GetRequiredService<IServiceScopeFactory>().CreateScope();
        var provider = scope.ServiceProvider;
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var outer = manager.Begin(
            new AbpUnitOfWorkOptions
            {
                IsTransactional = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            }, requiresNew: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeDraftAsync(provider, storage));
        Assert.Contains("RepeatableRead", error.Message, StringComparison.Ordinal);
        // Do not complete the rejected ambient UOW: dispose rolls it back.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PGINV03_Ambient_RepeatableRead_Passes_Isolation_Guard(
        bool storage)
    {
        using var scope = GetRequiredService<IServiceScopeFactory>().CreateScope();
        var provider = scope.ServiceProvider;
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var outer = manager.Begin(
            new AbpUnitOfWorkOptions
            {
                IsTransactional = true,
                IsolationLevel = IsolationLevel.RepeatableRead
            }, requiresNew: true);

        // An unknown contract is expected to fail at the next business
        // check, proving the actual PostgreSQL isolation guard allowed
        // the UOW without touching any real contract or invoice.
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => InvokeDraftAsync(provider, storage));
        Assert.Equal(BuildingManagementErrorCodes.ContractNotFound, error.Code);
    }

    [Fact]
    public async Task PGINV07_Ambient_ReadCommitted_Cannot_Check_Stored_Draft_Freshness()
    {
        using var scope = GetRequiredService<IServiceScopeFactory>().CreateScope();
        var provider = scope.ServiceProvider;
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var outer = manager.Begin(
            new AbpUnitOfWorkOptions
            {
                IsTransactional = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            }, requiresNew: true);

        // The guard rejects isolation BEFORE any invoice-table query,
        // so this is safe even on a historical test DB whose invoice
        // migration has not been applied.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IInvoiceDraftStorageAppService>()
                .HasCurrentSourceSnapshotAsync(Guid.NewGuid()));
        Assert.Contains("RepeatableRead", error.Message, StringComparison.Ordinal);
    }

    private static async Task InvokeDraftAsync(IServiceProvider provider, bool storage)
    {
        var unknownContractId = Guid.NewGuid();
        if (storage)
        {
            await provider.GetRequiredService<IInvoiceDraftStorageAppService>()
                .CreateAsync(new CreateInvoiceDraftDto
                {
                    ContractId = unknownContractId,
                    BillingYear = 2026,
                    BillingMonth = 10
                });
        }
        else
        {
            await provider.GetRequiredService<IInvoiceDraftCandidateAppService>()
                .GetMonthlyCandidateAsync(unknownContractId, 2026, 10);
        }
    }
}
