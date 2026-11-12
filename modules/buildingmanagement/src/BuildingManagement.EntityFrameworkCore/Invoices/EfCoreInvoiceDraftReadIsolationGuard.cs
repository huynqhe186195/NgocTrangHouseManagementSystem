using System;
using System.Data;
using System.Threading.Tasks;
using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.Invoices;

/// <summary>
/// PostgreSQL fail-closed check for ambient UOWs. Method attributes cannot
/// change an already-started ABP UOW, so inspect the ACTUAL EF transaction
/// before touching contract, meter or tariff data.
/// </summary>
public sealed class EfCoreInvoiceDraftReadIsolationGuard
    : IInvoiceDraftReadIsolationGuard, ITransientDependency
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _contexts;

    public EfCoreInvoiceDraftReadIsolationGuard(
        IDbContextProvider<BuildingManagementDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task RequireRepeatableReadAsync()
    {
        var db = await _contexts.GetDbContextAsync();

        // Existing SQLite-only tests disable EF transactions. PostgreSQL
        // checks below are the production authority for draft consistency.
        if (!db.Database.IsNpgsql())
            return;

        var transaction = db.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException(
                "Invoice draft capture requires a PostgreSQL transaction " +
                "at RepeatableRead or Serializable isolation.");

        var isolation = transaction.GetDbTransaction().IsolationLevel;
        if (isolation != IsolationLevel.RepeatableRead &&
            isolation != IsolationLevel.Serializable)
            throw new InvalidOperationException(
                "Invoice draft capture requires RepeatableRead or Serializable " +
                "isolation; the ambient transaction uses " + isolation + ".");
    }
}
