using System;
using System.Linq;
using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Provider-independent EF model checks. No production database and no
/// migration execution is required to run these checks.
/// </summary>
public sealed class InvoiceSchema_Tests
{
    private static BuildingManagementDbContext Model()
    {
        return new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseSqlite("Data Source=:memory:").Options);
    }

    [Fact]
    public void Invoice_Header_Has_Contract_Period_Unique_Key()
    {
        using var db = Model();
        var entity = db.Model.FindEntityType(typeof(Invoice));
        Assert.NotNull(entity);
        Assert.Equal("BuildingManagementInvoices", entity.GetTableName());

        var index = entity.GetIndexes().Single(x =>
            x.Properties.Select(p => p.Name).SequenceEqual(
                new[] { "ContractId", "BillingYear", "BillingMonth" }));
        Assert.True(index.IsUnique);
        Assert.Equal("UX_BM_Invoice_Contract_Period", index.GetDatabaseName());
    }

    [Fact]
    public void Invoice_Items_And_Meter_Sources_Have_Unique_Source_Keys()
    {
        using var db = Model();
        var item = db.Model.FindEntityType(typeof(InvoiceItem));
        var source = db.Model.FindEntityType(typeof(InvoiceItemMeterSource));
        Assert.NotNull(item);
        Assert.NotNull(source);
        Assert.Equal("BuildingManagementInvoiceItems", item.GetTableName());
        Assert.Equal("BuildingManagementInvoiceItemMeterSources", source.GetTableName());

        Assert.Contains(item.GetIndexes(), x =>
            x.IsUnique && x.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "InvoiceId", "UtilityType" }));
        Assert.Contains(source.GetIndexes(), x =>
            x.IsUnique && x.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "InvoiceItemId", "SourceType", "SourceId" }));
    }

    [Fact]
    public void Historical_Invoice_Relationships_Cannot_Cascade_Delete()
    {
        using var db = Model();
        foreach (var entityType in new[]
        {
            db.Model.FindEntityType(typeof(Invoice)),
            db.Model.FindEntityType(typeof(InvoiceItem)),
            db.Model.FindEntityType(typeof(InvoiceItemMeterSource))
        })
        {
            Assert.NotNull(entityType);
            Assert.All(entityType.GetForeignKeys(),
                x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        }
    }
}
