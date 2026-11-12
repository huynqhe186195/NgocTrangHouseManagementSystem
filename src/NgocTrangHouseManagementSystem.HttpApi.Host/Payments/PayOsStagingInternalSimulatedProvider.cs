using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// INTERNAL SIMULATED payOS provider for the P1.5 internal staging lane
/// (Owner decision 2026-11-11: internal staging only, no real payOS calls,
/// no public callback URL). Implements IPayOsPaymentLinkProvider entirely
/// from the persisted payOS staging database state:
///
/// - CreateAsync validates the command against a just-locked RESERVED
///   intent row (same orderCode, amount, not-yet-linked, not terminal) and
///   derives the payment link deterministically from the orderCode. It never
///   reaches the network and never invents a different order.
/// - FindAsync/GetAsync reconstruct the provider snapshot from the persisted
///   intent; Get reports PAID only when the intent is confirmed pending
///   posting (i.e. an accepted signed webhook plus operator reconciliation
///   happened), otherwise PENDING (or 404-equivalent null for a fully
///   unknown order code).
///
/// It cannot move real money and is registered ONLY under the
/// PayOsStaging gate with ProviderMode=InternalSimulated plus the same
/// Staging-environment + activation-phrase + loopback-disposable-DB gates.
/// </summary>
public sealed class PayOsStagingInternalSimulatedProvider :
    IPayOsPaymentLinkProvider, IDisposable
{
    private const string LinkPrefix = "SIM-P15-";
    private const int MaxLinkTail = 38;

    private readonly PayOsPaymentMigrationDbContext _db;
    private readonly IServiceScope? _scope;

    public PayOsStagingInternalSimulatedProvider(PayOsPaymentMigrationDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Host-DI constructor: owns a scope so the DbContext (and its
    /// connection) is disposed with the provider/dispatcher, not pooled per
    /// request. The direct constructor exists for narrow unit tests.
    /// </summary>
    public PayOsStagingInternalSimulatedProvider(IServiceScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _db = scope.ServiceProvider.GetRequiredService<PayOsPaymentMigrationDbContext>()
            ?? throw new ArgumentNullException(nameof(scope));
    }

    public void Dispose() => _scope?.Dispose();

    public async Task<PayOsCheckoutResult> CreateAsync(
        PayOsCheckoutCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        PayOsPaymentIntent? intent = null;
        await foreach (var row in _db.PayOsPaymentIntents
            .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                WHERE ""OrderCode"" = {command.OrderCode} FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            intent = row;
        }

        if (intent is null ||
            intent.PaymentId is null ||
            intent.AmountVnd != command.AmountVnd ||
            intent.PaymentLinkId is not null ||
            intent.Status is not (PayOsPaymentIntentStatus.Created or
                PayOsPaymentIntentStatus.AwaitingPayment) ||
            intent.ExpiresAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException(
                "Simulated payOS provider refuses a command that does not match " +
                "a reserved, unlinked, non-terminal staging intent.");

        // Deterministic link id derived from the reserved order code: the
        // same retry of the SAME order always produces the SAME link, which
        // is exactly the recovery semantics the checkout service needs.
        var linkId = LinkPrefix + command.OrderCode.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return new PayOsCheckoutResult(
            command.OrderCode, command.AmountVnd, linkId,
            new Uri("https://pay.payos.vn/web/" + linkId),
            "SIMULATED-QR-P15-" + command.OrderCode);
    }

    public async Task<PayOsPaymentLinkSnapshot?> FindAsync(
        long orderCode, CancellationToken cancellationToken = default)
    {
        var intent = await _db.PayOsPaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrderCode == orderCode, cancellationToken);
        if (intent is null)
            return null;

        // The simulated provider's "money arrived" signal is the accepted
        // signed webhook (a durable inbox row), so reconciliation can see
        // PAID BEFORE the intent itself is confirmed — mirroring the real
        // provider where the bank payment, not the intent state, is truth.
        var hasSignedInbox = await _db.PayOsWebhookInbox.AsNoTracking()
            .AnyAsync(x => x.OrderCode == orderCode, cancellationToken);
        return SnapshotFor(intent, hasSignedInbox);
    }

    public async Task<PayOsPaymentLinkSnapshot> GetAsync(
        long orderCode, CancellationToken cancellationToken = default)
    {
        var snapshot = await FindAsync(orderCode, cancellationToken);
        if (snapshot is null)
            // The simulated provider has no provider-side record at all for
            // an unknown order: the real client surfaces HttpRequestException
            // (404) which FindAsync maps to null.
            throw new HttpRequestException(
                "Simulated payOS provider has no payment request for this order code.",
                null, System.Net.HttpStatusCode.NotFound);
        return snapshot;
    }

    private static PayOsPaymentLinkSnapshot SnapshotFor(
        PayOsPaymentIntent intent, bool hasSignedInbox)
    {
        if (intent.PaymentLinkId is null)
        {
            // Reserved but never linked (provider create never happened in
            // the simulation). Represent as PENDING with nothing paid.
            return new PayOsPaymentLinkSnapshot(
                intent.OrderCode, intent.AmountVnd, 0, intent.AmountVnd,
                LinkPrefix + intent.OrderCode, "PENDING");
        }

        if (intent.Status == PayOsPaymentIntentStatus.Cancelled)
            return new PayOsPaymentLinkSnapshot(
                intent.OrderCode, intent.AmountVnd, 0, intent.AmountVnd,
                intent.PaymentLinkId, "CANCELLED");

        var paid = intent.Status is PayOsPaymentIntentStatus.Posted or
            PayOsPaymentIntentStatus.ConfirmedPendingPosting || hasSignedInbox;
        return new PayOsPaymentLinkSnapshot(
            intent.OrderCode, intent.AmountVnd,
            paid ? intent.AmountVnd : 0,
            paid ? 0 : intent.AmountVnd,
            intent.PaymentLinkId, paid ? "PAID" : "PENDING");
    }
}
