using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Source-only D04-B internal coordinator. The public webhook CANNOT invoke
/// this type. Both immutable review and Owner approval must exist first.
/// Only the exact, currently authenticated approving Owner can request a NEW
/// posting. The writer independently reacquires live dual-authority locks
/// and runs its SERIALIZABLE Payment+settlement+receipt+outbox transaction.
/// The ACK is a second, proof-backed operation and never mints more money.
/// This class is NOT DI-registered until authenticated three-DB HTTP tests
/// and statement archival have passed.
/// </summary>
public interface IPayOsD04ReviewedPostingCoordinator
{
    Task<PayOsD04PostingOutcome> PostApprovedAsync(
        Guid intentId, CancellationToken ct = default);
}

public sealed class PayOsD04ReviewedPostingCoordinator : IPayOsD04ReviewedPostingCoordinator
{
    private readonly BuildingManagementDbContext _db;
    private readonly PayOsD04LegacyPostingWriter _writer;
    private readonly PayOsD04StagingReceiptAcknowledger _acknowledger;
    private readonly ICurrentUser _user;

    public PayOsD04ReviewedPostingCoordinator(
        BuildingManagementDbContext db,
        PayOsD04LegacyPostingWriter writer,
        PayOsD04StagingReceiptAcknowledger acknowledger,
        ICurrentUser user)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _acknowledger = acknowledger ??
            throw new ArgumentNullException(nameof(acknowledger));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public async Task<PayOsD04PostingOutcome> PostApprovedAsync(
        Guid intentId, CancellationToken ct = default)
    {
        if (!_user.IsAuthenticated || _user.Id is not Guid signedInOwner ||
            signedInOwner == Guid.Empty || intentId == Guid.Empty)
            throw Denied();

        // The approving Owner comes ONLY from a durable immutable JOIN,
        // never a supplied DTO or a cached role claim. Missing review blocks.
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT a."OwnerApproverId"
                  FROM "BuildingManagementPayOsD04BankReviews" b
                  JOIN "BuildingManagementPayOsD04OwnerApprovals" a
                    ON a."BankReviewId"=b."Id"
                   AND a."ReviewerId"=b."ReviewerId"
                   AND a."DocumentSha256"=b."DocumentSha256"
                 WHERE b."PayOsIntentId"=@intent
                """, conn);
            cmd.Parameters.AddWithValue("intent", intentId);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid approvedOwner ||
                approvedOwner != signedInOwner)
                throw Denied();
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }

        // The writer does NOT trust the above preflight: its own SERIALIZABLE
        // transaction rechecks current ABP account/Owner role, two financial
        // permissions and Building memberships via the commit-bound lease.
        var write = await _writer.PostAsync(intentId, ct);

        // Never report a bank receipt as confirmed based on just the writer
        // result. Read exact committed Payment+settlement+outbox from the
        // legacy DB and then separately ACK the staging state.
        // If ACK fails, return a distinctive retriable result; the outbox
        // remains durable and the recovery pass will retry SAME intent key.
        try
        {
            var ack = await _acknowledger.AcknowledgeAsync(intentId, ct);
            if (ack == PayOsD04AckOutcome.NoCommittedFinancialJournal)
                throw new InvalidOperationException(
                    "D04 legacy commit missing during outbox ACK; investigate.");
            return new PayOsD04PostingOutcome(write.ReceiptId,
                write.DepositSettlementId, write.OutboxId,
                AckPending: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is NpgsqlException or
            Microsoft.EntityFrameworkCore.DbUpdateException or
            IOException or TimeoutException or OperationCanceledException ||
            IsTransientEfWrappedTransportFailure(e))
        {
            // Transport/DB timeout after a COMMITTED receipt is a retriable
            // ACK gap. NEVER create a new order or a second financial row.
            // EF Core wraps transport failures (connection terminated,
            // socket reset) in InvalidOperationException("...transient
            // failure") with the Npgsql/IO transport exception only in the
            // INNER chain; a direct-type filter alone would miss those and
            // surface 409 instead of the designed retriable AckPending.
            return new PayOsD04PostingOutcome(write.ReceiptId,
                write.DepositSettlementId, write.OutboxId,
                AckPending: true);
        }
        // Integrity mismatch / unexpected exception MUST propagate for
        // investigation. Never advertise an inconsistent ledger as simply
        // "ACK pending"; the legacy source receipt remains immutable.
    }

    /// <summary>
    /// EF Core (NpgsqlExecutionStrategy) reports transport failures as a
    /// generic InvalidOperationException whose INNER exception chain still
    /// carries the Npgsql/IO transport exception. Walk the chain to
    /// recover the original transport classification; the EF wrapper
    /// message text itself is not a stable contract.
    /// </summary>
    private static bool IsTransientEfWrappedTransportFailure(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or IOException or TimeoutException or
                Microsoft.EntityFrameworkCore.DbUpdateException)
                return true;
        }
        return false;
    }

    private static AbpAuthorizationException Denied()
        => new("Only the currently authenticated approving Owner may request D04 posting.");
}

public sealed record PayOsD04PostingOutcome(
    Guid ReceiptId, Guid DepositSettlementId, Guid OutboxId, bool AckPending);
