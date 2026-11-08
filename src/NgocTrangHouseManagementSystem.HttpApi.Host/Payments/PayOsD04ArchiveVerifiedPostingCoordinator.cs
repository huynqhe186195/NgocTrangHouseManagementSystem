using System;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Volo.Abp.Authorization;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Fail-closed D04-B posting wrapper: a valid Owner approval alone is not
/// enough when its immutable bank statement has become unreadable/tampered.
/// Verify the SQL review's SERVER-issued protected reference and SHA-256
/// before entering the existing real financial writer/coordinator.
///
/// This wrapper has NO Host DI registration or operational activation. The
/// underlying coordinator remains responsible for real dual-actor identity,
/// serializable Building financial locks, outbox and 3-DB ACK/idempotence.
/// </summary>
public sealed class PayOsD04ArchiveVerifiedPostingCoordinator :
    IPayOsD04ReviewedPostingCoordinator
{
    private readonly IPayOsD04ReviewedPostingCoordinator _inner;
    private readonly IPayOsD04ProtectedBankEvidenceArchive _archive;
    private readonly BuildingManagementDbContext _db;

    public PayOsD04ArchiveVerifiedPostingCoordinator(
        IPayOsD04ReviewedPostingCoordinator inner,
        IPayOsD04ProtectedBankEvidenceArchive archive,
        BuildingManagementDbContext db)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<PayOsD04PostingOutcome> PostApprovedAsync(
        Guid intentId, CancellationToken ct = default)
    {
        if (intentId == Guid.Empty) throw Denied();
        Guid reviewerId;
        string reference, sha;
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            // The SQL row is immutable and was reviewed by a separate
            // authenticated user. Do NOT use any client-posted archive URL.
            // The real writer independently checks the matching Owner
            // approval, source, roles, privileges and live scope under locks.
            await using var cmd = new NpgsqlCommand("""
                SELECT r."ReviewerId",r."ExternalBankProofReference",
                       r."DocumentSha256"
                  FROM "BuildingManagementPayOsD04BankReviews" r
                  JOIN "BuildingManagementPayOsD04OwnerApprovals" a
                    ON a."BankReviewId"=r."Id"
                 WHERE r."PayOsIntentId"=@intent
                """, (NpgsqlConnection)_db.Database.GetDbConnection());
            cmd.Parameters.AddWithValue("intent", intentId);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw Denied();
            reviewerId = row.GetGuid(0);
            reference = row.GetString(1);
            sha = row.GetString(2);
            // A second matching row should not exist. Fail closed if the
            // immutable review/approval uniqueness constraints regress.
            if (await row.ReadAsync(ct)) throw Denied();
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }

        if (!await _archive.VerifyAsync(intentId, reviewerId,
                reference, sha, ct))
            throw Denied();

        return await _inner.PostApprovedAsync(intentId, ct);
    }

    private static AbpAuthorizationException Denied() =>
        new("D04-B posting requires accessible immutable bank statement proof.");
}
