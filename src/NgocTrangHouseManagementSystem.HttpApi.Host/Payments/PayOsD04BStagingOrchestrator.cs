using System;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Builds the real D04-B services PER REQUEST from ABP's UoW DbContext
/// provider. No manually 'new AbpDbContext' for financial SaveChanges.
/// This is registered only under the disposable-only triple-DB host guard.
/// It is not registered, scheduled or invoked on production.
/// </summary>
public sealed class PayOsD04BStagingOrchestrator :
    IPayOsD04ManualEvidenceService,
    IPayOsD04ReviewedPostingCoordinator
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly PayOsPaymentMigrationDbContext _staging;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _host;
    private readonly ICurrentUser _actor;
    private readonly IPayOsD04ProtectedBankEvidenceArchive _archive;

    public PayOsD04BStagingOrchestrator(
        IDbContextProvider<BuildingManagementDbContext> provider,
        PayOsPaymentMigrationDbContext staging,
        IConfiguration config, IHostEnvironment host, ICurrentUser actor,
        IPayOsD04ProtectedBankEvidenceArchive archive)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        // No archive is registered in the normal Host. Enabling the D04-B
        // test flag must fail closed rather than silently treating an
        // unverified client archive reference as independent bank evidence.
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    }

    public async Task<Guid> RecordIndependentBankReviewAsync(
        Guid intentId, PayOsD04ManualBankStatementSubmission evidence,
        CancellationToken ct = default)
    {
        var db = await _provider.GetDbContextAsync();
        var service = ReviewService(db);
        return await service.RecordIndependentBankReviewAsync(
            intentId, evidence, ct);
    }

    public async Task<Guid> ApproveReviewedDepositAsync(
        Guid bankReviewId, CancellationToken ct = default)
    {
        var db = await _provider.GetDbContextAsync();
        var service = ReviewService(db);
        return await service.ApproveReviewedDepositAsync(
            bankReviewId, ct);
    }

    public async Task<PayOsD04PostingOutcome> PostApprovedAsync(
        Guid intentId, CancellationToken ct = default)
    {
        var db = await _provider.GetDbContextAsync();
        var reader = new PayOsD04TrustedStagingEvidenceReader(_staging);
        var authority = new PayOsD04StagingDualAuthority(db, _config, _host);
        var writer = new PayOsD04LegacyPostingWriter(db, reader, authority);
        var committed = new PayOsD04CommittedReceiptStore(db);
        var ack = new PayOsD04StagingReceiptAcknowledger(_staging, committed);
        var coordinator = new PayOsD04ReviewedPostingCoordinator(
            db, writer, ack, _actor);
        // Even an already-approved SQL row cannot post money if its
        // persisted physical bank statement is missing or tampered.
        var verified = new PayOsD04ArchiveVerifiedPostingCoordinator(
            coordinator, _archive, db);
        return await verified.PostApprovedAsync(intentId, ct);
    }

    private IPayOsD04ManualEvidenceService ReviewService(
        BuildingManagementDbContext db)
    {
        var reader = new PayOsD04TrustedStagingEvidenceReader(_staging);
        var authority = new PayOsD04StagingDualAuthority(db, _config, _host);
        var canonical = new PayOsD04ManualEvidenceService(
            db, reader, authority, _actor);
        return new PayOsD04ArchiveBackedManualEvidenceService(
            canonical, _archive, db, _actor, authority, reader);
    }
}
