using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Staging reconciliation handoff only. NEVER completes a Payment or posts a
/// D04 official receipt. A signed inbox record AND an independent provider
/// lookup that agrees with the immutable payment target are required before
/// marking the intent "confirmed pending independent financial posting".
/// </summary>
public sealed class PayOsStagingReconciliationService
{
    private readonly PayOsPaymentMigrationDbContext _db;
    private readonly IPayOsPaymentLinkProvider _provider;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly ICurrentUser _user;

    public PayOsStagingReconciliationService(
        PayOsPaymentMigrationDbContext db,
        IPayOsPaymentLinkProvider provider,
        IRepository<Payment, Guid> payments,
        IBuildingCrudAccessPolicy access,
        ICurrentUser user)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public async Task<PayOsReconciliationView> CheckAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        if (paymentId == Guid.Empty || !_user.IsAuthenticated || !_user.Id.HasValue)
            throw Denied();

        var payment = await _payments.FindAsync(paymentId);
        if (payment?.ContractId is not Guid contractId || contractId == Guid.Empty)
            throw Denied();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);
        var intent = await _db.PayOsPaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PaymentId == paymentId, cancellationToken);
        if (intent is null || intent.InvoiceId.HasValue ||
            intent.PayerId != payment.TenantId ||
            payment.Amount != intent.AmountVnd ||
            intent.Status == PayOsPaymentIntentStatus.Created ||
            string.IsNullOrWhiteSpace(intent.PaymentLinkId))
            return new PayOsReconciliationView(0,
                PayOsReconciliationDisposition.ConflictRequiresReview, false);

        // Never call merchant status with a caller-supplied order code.
        var provider = await _provider.GetAsync(intent.OrderCode, cancellationToken);
        var classification = PayOsPaymentReconciliationPolicy.Classify(intent, provider);
        if (classification != PayOsReconciliationDisposition.FundsDetectedPendingIndependentPosting)
            return new PayOsReconciliationView(intent.OrderCode, classification, false);

        // A provider status alone is not a bank transaction identifier.
        // Require exactly one previously signed and durably inserted inbox row.
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);

        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        PayOsPaymentIntent? locked = null;
        await foreach (var entry in _db.PayOsPaymentIntents
            .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                 WHERE ""Id"" = {intent.Id} FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            locked = entry;
        }
        if (locked is null || locked.PaymentId != paymentId ||
            locked.BuildingId != intent.BuildingId || locked.PayerId != intent.PayerId ||
            locked.AmountVnd != intent.AmountVnd ||
            locked.Status != PayOsPaymentIntentStatus.AwaitingPayment ||
            locked.PaymentLinkId != intent.PaymentLinkId)
            return new PayOsReconciliationView(intent.OrderCode,
                PayOsReconciliationDisposition.ConflictRequiresReview, false);

        var events = await _db.PayOsWebhookInbox.Where(x =>
            x.PaymentIntentId == locked.Id &&
            x.BuildingId == locked.BuildingId &&
            x.OrderCode == locked.OrderCode)
            .Take(2).ToListAsync(cancellationToken);
        if (events.Count != 1 ||
            events[0].Status != PayOsWebhookInboxStatus.PendingReconciliation)
            return new PayOsReconciliationView(locked.OrderCode,
                PayOsReconciliationDisposition.ConflictRequiresReview, false);

        // ConfirmedAtUtc is the reconciliation/verification timestamp, not
        // a claimed bank-paid timestamp. D04 must independently verify cash
        // evidence and financial review before any Payment/ledger mutation.
        locked.MarkConfirmed(events[0].ProviderReference, DateTime.UtcNow);
        events[0].MarkReconciled(DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new PayOsReconciliationView(locked.OrderCode,
            PayOsReconciliationDisposition.FundsDetectedPendingIndependentPosting, true);
    }

    private static AbpAuthorizationException Denied() =>
        new("No authenticated, active building-scoped authority for payOS reconciliation.");
}

public sealed record PayOsReconciliationView(
    long OrderCode, PayOsReconciliationDisposition Disposition,
    bool ConfirmedPendingFinancialReview);
