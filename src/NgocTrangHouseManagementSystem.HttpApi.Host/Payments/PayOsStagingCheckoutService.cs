using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Staging-only operator checkout orchestrator. The input is ONLY an existing
/// Payment ID: payer, building, amount and the payment target come from
/// repository state. No tenant-supplied amount, return URL or building scope.
/// 
/// This stages a QR for an existing, pending CONTRACT DEPOSIT payment; it
/// intentionally does not authorize a bank receipt or change Payment.Status.
/// Other payment purposes remain held until their full financial review rules
/// are independently accepted.
/// </summary>
public sealed class PayOsStagingCheckoutService
{
    private readonly PayOsPaymentMigrationDbContext _db;
    private readonly IPayOsPaymentLinkProvider _provider;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly ICurrentUser _user;
    private readonly PayOsStagingCheckoutOptions _options;

    public PayOsStagingCheckoutService(
        PayOsPaymentMigrationDbContext db,
        IPayOsPaymentLinkProvider provider,
        IRepository<Payment, Guid> payments,
        IRepository<Contract, Guid> contracts,
        IRepository<Room, Guid> rooms,
        IRepository<Floor, Guid> floors,
        IBuildingCrudAccessPolicy access,
        ICurrentUser user,
        PayOsStagingCheckoutOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _floors = floors ?? throw new ArgumentNullException(nameof(floors));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<PayOsCheckoutView> StartOrRecoverAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        if (paymentId == Guid.Empty || !_user.IsAuthenticated ||
            !_user.Id.HasValue || _user.Id.Value == Guid.Empty)
            throw Denied();

        var payment = await _payments.FindAsync(paymentId);
        if (payment?.ContractId is not Guid contractId || contractId == Guid.Empty)
            throw Denied();

        // Authoritative ABP permission + active account + persisted building
        // membership + contract->room->floor->building scope, fail closed.
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);

        if (payment.Purpose != PaymentPurpose.ContractDeposit ||
            payment.Status != PaymentStatus.Pending ||
            payment.Method != PaymentMethod.BankTransfer ||
            payment.Channel != PaymentChannel.VietQr ||
            payment.TenantId == Guid.Empty ||
            payment.Amount <= 0 ||
            payment.Amount > long.MaxValue ||
            decimal.Truncate(payment.Amount) != payment.Amount)
            throw Denied();

        var contract = await _contracts.FindAsync(contractId);
        if (contract is null ||
            contract.Status is not (ContractStatus.Signed or ContractStatus.Active) ||
            payment.Amount > contract.DepositAmount)
            throw Denied();
        var room = await _rooms.FindAsync(contract.RoomId);
        var floor = room is null ? null : await _floors.FindAsync(room.FloorId);
        if (floor is null || floor.BuildingId == Guid.Empty)
            throw Denied();

        var amount = (long)payment.Amount;
        var buildingId = floor.BuildingId;

        // An issued checkout cannot be retargeted to another amount, tenant,
        // building or invoice after a caller retries. Unique PaymentId in PG
        // is the ultimate barrier to parallel requests for one source payment.
        var reservedNow = false;
        var intent = await _db.PayOsPaymentIntents.SingleOrDefaultAsync(
            x => x.PaymentId == paymentId, cancellationToken);
        if (intent is null)
        {
            intent = new PayOsPaymentIntent(
                Guid.NewGuid(), buildingId, payment.TenantId,
                RandomNumberGenerator.GetInt32(1, int.MaxValue),
                amount, DateTime.UtcNow.AddMinutes(20),
                paymentId: paymentId);
            _db.PayOsPaymentIntents.Add(intent);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                reservedNow = true;
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException pg &&
                pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                _db.Entry(intent).State = EntityState.Detached;
                intent = await _db.PayOsPaymentIntents.SingleOrDefaultAsync(
                    x => x.PaymentId == paymentId, cancellationToken);
                if (intent is null) throw new InvalidOperationException(
                    "Order code collision: retry the caller request; never reuse a different payment intent.");
            }
        }

        if (intent.PaymentId != paymentId || intent.InvoiceId.HasValue ||
            intent.BuildingId != buildingId || intent.PayerId != payment.TenantId ||
            intent.AmountVnd != amount)
            throw Denied();

        if (intent.Status == PayOsPaymentIntentStatus.Cancelled ||
            intent.Status == PayOsPaymentIntentStatus.Posted ||
            intent.Status == PayOsPaymentIntentStatus.ConfirmedPendingPosting)
            return new PayOsCheckoutView(intent.OrderCode, amount, null, null,
                PayOsCheckoutViewStatus.ReconciliationOnly);

        // Re-authorization immediately before any external provider operation.
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);

        if (intent.PaymentLinkId is not null)
        {
            var known = await _provider.GetAsync(intent.OrderCode, cancellationToken);
            if (!MatchesProvider(intent, known))
                throw new InvalidOperationException("Provider order/link or amount conflict. Independent review required.");
            return new PayOsCheckoutView(intent.OrderCode, amount,
                known.Status is "PENDING" or "PROCESSING"
                    ? CheckoutUrl(intent.PaymentLinkId)
                    : null,
                null, known.Status == "PAID"
                    ? PayOsCheckoutViewStatus.ReconciliationOnly
                    : known.Status is "PENDING" or "PROCESSING"
                        ? PayOsCheckoutViewStatus.Recovered
                        : PayOsCheckoutViewStatus.ReconciliationOnly);
        }

        // An earlier create may have reached payOS before our HTTP request
        // timed out. ALWAYS recover the SAME orderCode first. 5xx / timeout
        // must propagate; only definitive HTTP 404 can authorize create.
        // A newly reserved intent has never been sent to payOS: create once.
        // Only a retry of an existing Created intent needs a provider lookup.
        var recovered = reservedNow
            ? null
            : await _provider.FindAsync(intent.OrderCode, cancellationToken);
        if (recovered is not null)
        {
            if (recovered.OrderCode != intent.OrderCode ||
                recovered.AmountVnd != intent.AmountVnd ||
                recovered.AmountPaidVnd < 0 ||
                recovered.AmountRemainingVnd < 0 ||
                recovered.AmountPaidVnd > intent.AmountVnd ||
                recovered.AmountPaidVnd != intent.AmountVnd - recovered.AmountRemainingVnd)
                throw new InvalidOperationException("Ambiguous payOS recovery result; requires review.");

            await PersistProviderLinkAsync(intent, recovered.PaymentLinkId, cancellationToken);
            return new PayOsCheckoutView(intent.OrderCode, amount,
                recovered.Status is "PENDING" or "PROCESSING"
                    ? CheckoutUrl(recovered.PaymentLinkId) : null,
                null,
                recovered.Status is "PENDING" or "PROCESSING"
                    ? PayOsCheckoutViewStatus.Recovered
                    : PayOsCheckoutViewStatus.ReconciliationOnly);
        }

        // Even in a network race, all attempts use the SAME reserved orderCode.
        // payOS rejects duplicate order codes. We intentionally do not generate
        // a second code after 409, timeout, 429 or 5xx.
        var fresh = await _provider.CreateAsync(new PayOsCheckoutCommand(
            intent.OrderCode, intent.AmountVnd, "NT" + intent.OrderCode,
            _options.ReturnUrl, _options.CancelUrl, intent.ExpiresAtUtc),
            cancellationToken);
        if (fresh.OrderCode != intent.OrderCode || fresh.AmountVnd != intent.AmountVnd)
            throw new InvalidOperationException("Provider returned a different payment intent.");
        await PersistProviderLinkAsync(intent, fresh.PaymentLinkId, cancellationToken);
        return new PayOsCheckoutView(intent.OrderCode, amount, fresh.CheckoutUrl,
            fresh.QrCode, PayOsCheckoutViewStatus.Created);
    }

    private async Task PersistProviderLinkAsync(
        PayOsPaymentIntent original, string linkId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(linkId) || linkId.Length > 128 ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                linkId, "^[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException("Unsafe provider payment link identifier.");

        _db.ChangeTracker.Clear();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        PayOsPaymentIntent? locked = null;
        await foreach (var current in _db.PayOsPaymentIntents
            .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                WHERE ""Id"" = {original.Id} FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            locked = current;
        }
        if (locked is null || locked.BuildingId != original.BuildingId ||
            locked.PayerId != original.PayerId ||
            locked.OrderCode != original.OrderCode ||
            locked.AmountVnd != original.AmountVnd ||
            locked.PaymentId != original.PaymentId ||
            locked.Status is PayOsPaymentIntentStatus.Cancelled or
                PayOsPaymentIntentStatus.Posted)
            throw Denied();

        if (locked.PaymentLinkId is null)
        {
            locked.AttachProviderLink(linkId);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(locked.PaymentLinkId, linkId, StringComparison.Ordinal))
            throw new InvalidOperationException("Provider link conflict requires review.");

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool MatchesProvider(
        PayOsPaymentIntent intent, PayOsPaymentLinkSnapshot snapshot)
        => snapshot.OrderCode == intent.OrderCode &&
            snapshot.AmountVnd == intent.AmountVnd &&
            string.Equals(snapshot.PaymentLinkId, intent.PaymentLinkId, StringComparison.Ordinal) &&
            snapshot.AmountPaidVnd >= 0 && snapshot.AmountRemainingVnd >= 0 &&
            snapshot.AmountPaidVnd <= intent.AmountVnd &&
            snapshot.AmountPaidVnd == intent.AmountVnd - snapshot.AmountRemainingVnd;

    private static Uri CheckoutUrl(string paymentLinkId) =>
        new("https://pay.payos.vn/web/" + Uri.EscapeDataString(paymentLinkId));

    private static AbpAuthorizationException Denied() =>
        new("Payment is not eligible for a building-scoped staging payOS checkout.");
}

public sealed record PayOsStagingCheckoutOptions(Uri ReturnUrl, Uri CancelUrl);

public enum PayOsCheckoutViewStatus
{
    Created = 1,
    Recovered = 2,
    ReconciliationOnly = 3
}

public sealed record PayOsCheckoutView(
    long OrderCode, long AmountVnd, Uri? CheckoutUrl, string? QrCode,
    PayOsCheckoutViewStatus Status);
