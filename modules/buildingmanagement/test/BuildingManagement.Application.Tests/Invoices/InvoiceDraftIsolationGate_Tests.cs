using System;
using System.Data;
using System.Reflection;
using Volo.Abp.Uow;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// An invoice's two utilities, source meter readings and tariff rates
/// must never be read from unrelated PostgreSQL READ COMMITTED snapshots.
/// Attribute tests cover root-UOW declaration only. PostgreSQL integration
/// tests are still required for concurrent source edits / nested UOWs.
/// </summary>
public sealed class InvoiceDraftIsolationGate_Tests
{
    [Theory]
    [InlineData(typeof(InvoiceDraftStorageAppService), nameof(InvoiceDraftStorageAppService.CreateAsync))]
    [InlineData(typeof(InvoiceDraftStorageAppService), nameof(InvoiceDraftStorageAppService.HasCurrentSourceSnapshotAsync))]
    [InlineData(typeof(InvoiceDraftCandidateAppService), nameof(InvoiceDraftCandidateAppService.GetMonthlyCandidateAsync))]
    public void Invoice_draft_capture_requires_repeatable_read_root_uow(
        Type serviceType, string methodName)
    {
        var method = serviceType.GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<UnitOfWorkAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(true, attribute.IsTransactional);
        Assert.Equal(IsolationLevel.RepeatableRead, attribute.IsolationLevel);
    }
}
