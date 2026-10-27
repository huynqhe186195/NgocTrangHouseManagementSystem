using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Floors;
using BuildingManagement.MoveOuts;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore;

[ConnectionStringName(BuildingManagementDbProperties.ConnectionStringName)]
public class BuildingManagementDbContext : AbpDbContext<BuildingManagementDbContext>, IBuildingManagementDbContext
{
    /* Add DbSet for each Aggregate Root here. Example:
     * public DbSet<Question> Questions { get; set; }
     */
    // The register entity Building into database
    public DbSet<Building> Buildings { get; set; } = default!;
    public DbSet<Floor> Floors { get; set; } = default!;
    public DbSet<Room> Rooms { get; set; } = default!;
    public DbSet<Tenant> Tenants { get; set; } = default!;
    public DbSet<Contract> Contracts { get; set; } = default!;
    public DbSet<ContractTenant> ContractTenants { get; set; } = default!;
    public DbSet<UtilityRate> UtilityRates { get; set; } = default!;
    public DbSet<UtilityMeter> UtilityMeters { get; set; } = default!;
    public DbSet<MeterReading> MeterReadings { get; set; } = default!;
    public DbSet<MeterReplacement> MeterReplacements { get; set; } = default!;
    public DbSet<ContractRenewalHold> ContractRenewalHolds { get; set; } = default!;
    public DbSet<RoomReservation> RoomReservations { get; set; } = default!;
    public DbSet<Payment> Payments { get; set; } = default!;
    public DbSet<DepositSettlement> DepositSettlements {get; set;} = default!;
    public DbSet<MoveOutRequest> MoveOutRequests { get; set; } = default!;
    public DbSet<MoveOutChecklistTask> MoveOutChecklistTasks { get; set; } = default!;
    public DbSet<Invoice> Invoices { get; set; } = default!;
    public DbSet<InvoiceItem> InvoiceItems { get; set; } = default!;
    public DbSet<InvoiceItemMeterSource> InvoiceItemMeterSources { get; set; } = default!;
    // Phase 2G.3A.7 — staged history model; migration not generated/applied.
    public DbSet<InvoiceDraftRevisionRecord> InvoiceDraftRevisions { get; set; } = default!;
    // STAGED evidence only: NOT issued, NOT a receivable, migration absent.
    public DbSet<FinalInvoiceIssueEvidenceRecord> FinalInvoiceIssueEvidence { get; set; } = default!;
    // STAGED only: not a posted receivable or payment. Production migration absent.
    public DbSet<InvoicePaymentAllocationJournalEntry> InvoicePaymentAllocationJournal { get; set; } = default!;
    // Staged two-person review record; no production issue/approval writer.
    public DbSet<InvoiceIssuanceReviewTicket> InvoiceIssuanceReviews { get; set; } = default!;

    public BuildingManagementDbContext(DbContextOptions<BuildingManagementDbContext> options)
        : base(options)
    {

    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        BuildingManagement.EntityFrameworkCore.MoveOutModelBuilderExtensions.ConfigureMoveOutRequests(builder);
        base.OnModelCreating(builder);

        builder.ConfigureBuildingManagement();
        // Explicit mapping: survives differing module model-builder conventions.
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MeterReplacementConfiguration());
        // Phase 2G.3A.2: draft-only invoice snapshot schema.
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoiceConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoiceItemConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoiceItemMeterSourceConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoiceDraftRevisionRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.FinalInvoiceIssueEvidenceRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoicePaymentAllocationJournalEntryConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.InvoiceIssuanceReviewTicketConfiguration());
    }
}
