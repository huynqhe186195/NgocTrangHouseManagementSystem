using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Read-only, INTERNAL staging-side proof reader for a future separately
/// authenticated D04 coordinator. Only persisted payOS intent AND reconciled
/// signed webhook evidence are returned; provider PAID alone is insufficient.
/// This reader is NOT registered with DI or reachable by a controller.
/// </summary>
public sealed class PayOsD04TrustedStagingEvidenceReader :
    IPayOsD04TrustedStagingEvidenceReader
{
    private readonly PayOsPaymentMigrationDbContext _db;

    public PayOsD04TrustedStagingEvidenceReader(
        PayOsPaymentMigrationDbContext db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<(PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox)?>
        ReadCurrentAsync(Guid intentId, CancellationToken cancellationToken)
    {
        if (intentId == Guid.Empty) return null;

        var intent = await _db.PayOsPaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == intentId, cancellationToken);
        if (intent is null ||
            intent.Status is not (
                PayOsPaymentIntentStatus.ConfirmedPendingPosting or
                PayOsPaymentIntentStatus.Posted) ||
            intent.PaymentId is null || intent.InvoiceId.HasValue ||
            intent.AmountVnd <= 0 || intent.BuildingId == Guid.Empty ||
            string.IsNullOrWhiteSpace(intent.PaymentLinkId) ||
            string.IsNullOrWhiteSpace(intent.ProviderReference))
            return null;

        var inbox = await _db.PayOsWebhookInbox.AsNoTracking()
            .Where(x => x.PaymentIntentId == intentId &&
                x.BuildingId == intent.BuildingId &&
                x.OrderCode == intent.OrderCode)
            .Take(2).ToListAsync(cancellationToken);
        if (inbox.Count != 1 ||
            inbox[0].Status != PayOsWebhookInboxStatus.ReconciledNoPosting ||
            !string.Equals(inbox[0].ProviderReference,
                intent.ProviderReference, StringComparison.Ordinal))
            return null;

        return (intent, inbox[0]);
    }
}
