using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Floors;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomChanges;
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
    // Schema-only rollout; runtime grant/account/writer DI remains DenyAll.
    public DbSet<BuildingOperatorAccessGrant> OperatorBuildingGrants { get; set; } = default!;
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
    public DbSet<RoomChangeRequest> RoomChangeRequests { get; set; } = default!;
    public DbSet<RoomChangeTenantRosterSnapshotRecord> RoomChangeRosterSnapshots { get; set; } = default!;
    public DbSet<RoomChangeBusinessPolicyRecord> RoomChangeBusinessPolicies { get; set; } = default!;
    public DbSet<MoveOutRequest> MoveOutRequests { get; set; } = default!;
    public DbSet<MoveOutChecklistTask> MoveOutChecklistTasks { get; set; } = default!;
    public DbSet<MoveOutRoomDispositionRecord> MoveOutRoomDispositions { get; set; } = default!;
    public DbSet<Invoice> Invoices { get; set; } = default!;
    public DbSet<InvoiceItem> InvoiceItems { get; set; } = default!;
    public DbSet<InvoiceItemMeterSource> InvoiceItemMeterSources { get; set; } = default!;
    // Phase 2G.3A.7 — staged history model; migration not generated/applied.
    public DbSet<InvoiceDraftRevisionRecord> InvoiceDraftRevisions { get; set; } = default!;
    // STAGED evidence only: NOT issued or receivable; migration source only.
    public DbSet<FinalInvoiceIssueEvidenceRecord> FinalInvoiceIssueEvidence { get; set; } = default!;
    // STAGED only: journal migration source exists, no financial posting.
    public DbSet<InvoicePaymentAllocationJournalEntry> InvoicePaymentAllocationJournal { get; set; } = default!;
    // Staged two-person review record; no production issue/approval writer.
    public DbSet<InvoiceIssuanceReviewTicket> InvoiceIssuanceReviews { get; set; } = default!;
    // SOURCE-ONLY: immutable issuance ledger is not writable by any current API.
    public DbSet<IssuedInvoiceLedgerRecord> IssuedInvoiceLedger { get; set; } = default!;
    // UNISSUED monthly candidate archive; no official receivable/issuer API.
    public DbSet<MonthlyInvoiceCandidateLedgerRecord> MonthlyInvoiceCandidates { get; set; } = default!;
    public DbSet<MonthlyInvoiceIndependentReviewRecord> MonthlyInvoiceReviews { get; set; } = default!;
    public DbSet<MonthlyIssuedInvoiceRecord> MonthlyIssuedInvoices { get; set; } = default!;
    public DbSet<MonthlyInvoicePaymentAllocationRecord> MonthlyInvoicePaymentAllocations { get; set; } = default!;
    public DbSet<MonthlyInvoiceAdjustmentRecord> MonthlyInvoiceAdjustments { get; set; } = default!;
    public DbSet<MonthlyInvoiceAdjustmentDecisionRecord> MonthlyInvoiceAdjustmentDecisions { get; set; } = default!;
    // Phase 2G.3: immutable official debt, separate from original issuance.
    public DbSet<OfficialInvoiceReceivablePostingRecord> OfficialInvoiceReceivables { get; set; } = default!;
    public DbSet<OfficialInvoiceReceiptReviewRecord> OfficialInvoiceReceiptReviews { get; set; } = default!;
    // Source-only explicit root deposit disposition approval; no automatic refund.
    public DbSet<ContractDepositSettlementDecisionRecord> DepositSettlementDecisions { get; set; } = default!;
    public DbSet<ContractDepositCloseoutApprovalRecord> DepositCloseoutApprovals { get; set; } = default!;

    public BuildingManagementDbContext(DbContextOptions<BuildingManagementDbContext> options)
        : base(options)
    {

    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        BuildingManagement.EntityFrameworkCore.MoveOutModelBuilderExtensions.ConfigureMoveOutRequests(builder);
        base.OnModelCreating(builder);

        builder.ConfigureBuildingManagement();
        // Source-only Slice A membership model. Adding the mapping alone
        // never authorizes a user; production providers stay DenyAll.
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.BuildingOperatorAccessGrantDesignConfiguration());
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
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.IssuedInvoiceLedgerRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyInvoiceCandidateLedgerRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyInvoiceIndependentReviewRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyIssuedInvoiceRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyInvoicePaymentAllocationRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyInvoiceAdjustmentRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MonthlyInvoiceAdjustmentDecisionRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.OfficialInvoiceReceivablePostingRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.OfficialInvoiceReceiptReviewRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.ContractDepositSettlementDecisionRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.MoveOutRoomDispositionRecordConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.RoomChangeRequestConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.RoomChangeTenantRosterSnapshotConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.RoomChangeBusinessPolicyConfiguration());
        builder.ApplyConfiguration(
            new BuildingManagement.EntityFrameworkCore.EntityConfigurations.ContractDepositCloseoutApprovalRecordConfiguration());
    }
}
