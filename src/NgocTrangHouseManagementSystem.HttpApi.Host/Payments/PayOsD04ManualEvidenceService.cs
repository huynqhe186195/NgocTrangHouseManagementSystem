using System;
using System.Data;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Payments;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Internal D04-B manual statement attestation and separate Owner approval.
/// No DI registration, HTTP route, or production activation is provided.
/// Requires an authenticated principal AND the live, row-locking PostgreSQL
/// identity/building authority. payOS PAID or webhook are never themselves
/// considered a bank statement. The operator must actually inspect separate
/// bank evidence; a reference to the separately protected evidence archive
/// is retained for later independent audit.
///
/// The stored SHA256 is computed from supplied file bytes ON THE SERVER.
/// This class does NOT securely archive those bytes, and therefore must
/// not be enabled with real bank documents until a protected durable archive
/// and retention/deletion policy have passed a separate security review.
/// </summary>
public sealed class PayOsD04ManualEvidenceService
{
    private readonly BuildingManagementDbContext _db;
    private readonly IPayOsD04TrustedStagingEvidenceReader _staging;
    private readonly PayOsD04StagingDualAuthority _authority;
    private readonly ICurrentUser _user;

    public PayOsD04ManualEvidenceService(
        BuildingManagementDbContext db,
        IPayOsD04TrustedStagingEvidenceReader staging,
        PayOsD04StagingDualAuthority authority, ICurrentUser user)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public async Task<Guid> RecordIndependentBankReviewAsync(
        Guid intentId, PayOsD04ManualBankStatementSubmission evidence,
        CancellationToken ct = default)
    {
        var reviewerId = CurrentUser();
        if (intentId == Guid.Empty || evidence is null ||
            !evidence.ReviewerAttestsBankStatementMatched ||
            evidence.AmountVnd <= 0 ||
            string.IsNullOrWhiteSpace(evidence.BankReference) ||
            evidence.BankReference.Length > 128 ||
            evidence.BankReference != evidence.BankReference.Trim() ||
            string.IsNullOrWhiteSpace(evidence.ProtectedArchiveReference) ||
            evidence.ProtectedArchiveReference.Length is < 8 or > 256 ||
            evidence.StatementDocumentBytes is null ||
            evidence.StatementDocumentBytes.Length is < 256 or > 5_242_880)
            throw Denied();

        // Prevent empty/unknown proof blobs; accepted only for the test-only
        // PDF/PNG/JPEG fixture pipeline. No file contents are logged.
        var bytes = evidence.StatementDocumentBytes;
        var pdf = bytes.Length >= 4 && bytes[0] == '%' && bytes[1] == 'P' &&
                  bytes[2] == 'D' && bytes[3] == 'F';
        var png = bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 &&
                  bytes[2] == 78 && bytes[3] == 71 &&
                  bytes[4] == 13 && bytes[5] == 10 &&
                  bytes[6] == 26 && bytes[7] == 10;
        var jpg = bytes.Length >= 3 && bytes[0] == 0xff &&
                  bytes[1] == 0xd8 && bytes[2] == 0xff;
        if (!pdf && !png && !jpg) throw Denied();

        var documentSha = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        var source = await _staging.ReadCurrentAsync(intentId, ct)
            ?? throw Denied();
        if (source.Intent.Status != PayOsPaymentIntentStatus.ConfirmedPendingPosting ||
            source.Intent.PaymentId is not Guid paymentId ||
            evidence.AmountVnd != source.Intent.AmountVnd ||
            evidence.BankReference != source.Intent.ProviderReference ||
            source.Inbox.ProviderReference != source.Intent.ProviderReference)
            throw Denied();

        var buildingId = await ResolveBuildingAsync(paymentId, source.Intent, ct);
        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);
        await using var lease = await _authority.AcquireReviewLeaseAsync(
            buildingId, reviewerId, ct);

        // Re-read signed/reconciled source while holding active reviewer
        // authority. A changed source must never authorize stale proof.
        var fresh = await _staging.ReadCurrentAsync(intentId, ct)
            ?? throw Denied();
        if (!SameSource(source.Intent, fresh.Intent) ||
            fresh.Inbox.ProviderReference != evidence.BankReference)
            throw Denied();
        if (await ResolveBuildingAsync(paymentId, fresh.Intent, ct) != buildingId)
            throw Denied();

        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var npgTx = (NpgsqlTransaction)tx.GetDbTransaction();
        await using var exists = new NpgsqlCommand("""
            SELECT "Id","ReviewerId","PaymentId","BuildingId","OrderCode",
                   "AmountVnd","BankReference","DocumentSha256",
                   "ExternalBankProofReference"
              FROM "BuildingManagementPayOsD04BankReviews"
             WHERE "PayOsIntentId"=@intent
             FOR SHARE
            """, conn, npgTx);
        exists.Parameters.AddWithValue("intent", intentId);
        await using (var row = await exists.ExecuteReaderAsync(ct))
        {
            if (await row.ReadAsync(ct))
            {
                var id = row.GetGuid(0);
                if (row.GetGuid(1) != reviewerId ||
                    row.GetGuid(2) != paymentId ||
                    row.GetGuid(3) != buildingId ||
                    row.GetInt64(4) != fresh.Intent.OrderCode ||
                    row.GetInt64(5) != evidence.AmountVnd ||
                    row.GetString(6) != evidence.BankReference ||
                    row.GetString(7) != documentSha ||
                    row.GetString(8) != evidence.ProtectedArchiveReference)
                    throw Denied();
                await row.DisposeAsync();
                await tx.CommitAsync(ct);
                return id;
            }
        }

        var reviewId = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementPayOsD04BankReviews"
              ("Id","PayOsIntentId","OrderCode","BuildingId","PaymentId",
               "AmountVnd","BankReference","DocumentSha256",
               "ExternalBankProofReference","ReviewerId","VerifiedAtUtc")
            VALUES (@id,@intent,@order,@building,@payment,@amount,@reference,
                    @sha,@archive,@reviewer,@at)
            """, conn, npgTx))
        {
            cmd.Parameters.AddWithValue("id", reviewId);
            cmd.Parameters.AddWithValue("intent", intentId);
            cmd.Parameters.AddWithValue("order", fresh.Intent.OrderCode);
            cmd.Parameters.AddWithValue("building", buildingId);
            cmd.Parameters.AddWithValue("payment", paymentId);
            cmd.Parameters.AddWithValue("amount", evidence.AmountVnd);
            cmd.Parameters.AddWithValue("reference", evidence.BankReference);
            cmd.Parameters.AddWithValue("sha", documentSha);
            cmd.Parameters.AddWithValue("archive", evidence.ProtectedArchiveReference);
            cmd.Parameters.AddWithValue("reviewer", reviewerId);
            cmd.Parameters.AddWithValue("at", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return reviewId;
    }

    public async Task<Guid> ApproveReviewedDepositAsync(
        Guid bankReviewId, CancellationToken ct = default)
    {
        var ownerId = CurrentUser();
        if (bankReviewId == Guid.Empty) throw Denied();
        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var pgTx = (NpgsqlTransaction)tx.GetDbTransaction();

        Guid reviewerId, buildingId, paymentId, intentId;
        string sha;
        long orderCode, amount;
        string bankReference;

        await using (var query = new NpgsqlCommand("""
            SELECT "ReviewerId","BuildingId","PaymentId","PayOsIntentId",
                   "DocumentSha256","OrderCode","AmountVnd","BankReference"
              FROM "BuildingManagementPayOsD04BankReviews"
             WHERE "Id"=@id
             FOR SHARE
            """, conn, pgTx))
        {
            query.Parameters.AddWithValue("id", bankReviewId);
            await using var r = await query.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw Denied();
            reviewerId = r.GetGuid(0);
            buildingId = r.GetGuid(1);
            paymentId = r.GetGuid(2);
            intentId = r.GetGuid(3);
            sha = r.GetString(4);
            orderCode = r.GetInt64(5);
            amount = r.GetInt64(6);
            bankReference = r.GetString(7);
            if (reviewerId == ownerId) throw Denied();
        }

        // Owner approval requires the two actors' CURRENT ABP identity,
        // direct financial permissions, actual Owner role and both persisted
        // Building memberships. This is not inferred from a JWT/role claim.
        await using var lease = await _authority.AcquireCommitLeaseAsync(
            buildingId, paymentId, reviewerId, ownerId, ct);

        var verified = await _staging.ReadCurrentAsync(intentId, ct)
            ?? throw Denied();
        if (verified.Intent.PaymentId != paymentId ||
            verified.Intent.BuildingId != buildingId ||
            verified.Intent.OrderCode != orderCode ||
            verified.Intent.AmountVnd != amount ||
            verified.Intent.ProviderReference != bankReference ||
            verified.Inbox.ProviderReference != bankReference ||
            await ResolveBuildingAsync(paymentId, verified.Intent, ct) != buildingId)
            throw Denied();

        await using (var existing = new NpgsqlCommand("""
            SELECT "Id","OwnerApproverId","DocumentSha256"
              FROM "BuildingManagementPayOsD04OwnerApprovals"
             WHERE "BankReviewId"=@review
             FOR SHARE
            """, conn, pgTx))
        {
            existing.Parameters.AddWithValue("review", bankReviewId);
            await using var row = await existing.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                var id = row.GetGuid(0);
                if (row.GetGuid(1) != ownerId || row.GetString(2) != sha)
                    throw Denied();
                await row.DisposeAsync();
                await tx.CommitAsync(ct);
                return id;
            }
        }

        var approvalId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementPayOsD04OwnerApprovals"
              ("Id","BankReviewId","ReviewerId","DocumentSha256",
               "OwnerApproverId","ApprovedAtUtc","ExpiresAtUtc")
            VALUES (@id,@review,@reviewer,@sha,@owner,@at,@expires)
            """, conn, pgTx))
        {
            insert.Parameters.AddWithValue("id", approvalId);
            insert.Parameters.AddWithValue("review", bankReviewId);
            insert.Parameters.AddWithValue("reviewer", reviewerId);
            insert.Parameters.AddWithValue("sha", sha);
            insert.Parameters.AddWithValue("owner", ownerId);
            insert.Parameters.AddWithValue("at", now);
            insert.Parameters.AddWithValue("expires", now.AddMinutes(15));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return approvalId;
    }

    private async Task<Guid> ResolveBuildingAsync(
        Guid paymentId, PayOsPaymentIntent source, CancellationToken ct)
    {
        var payment = await _db.Payments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null || payment.ContractId is not Guid contractId ||
            payment.TenantId != source.PayerId ||
            payment.Status != PaymentStatus.Pending ||
            payment.Purpose != PaymentPurpose.ContractDeposit ||
            payment.Amount != source.AmountVnd)
            throw Denied();

        var contract = await _db.Contracts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == contractId, ct);
        if (contract is null ||
            contract.Status is not (ContractStatus.Signed or ContractStatus.Active))
            throw Denied();

        var room = await _db.Rooms.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == contract.RoomId, ct);
        var floor = room is null ? null : await _db.Floors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == room.FloorId, ct);
        if (floor is null || floor.BuildingId == Guid.Empty ||
            floor.BuildingId != source.BuildingId)
            throw Denied();
        return floor.BuildingId;
    }

    private static bool SameSource(PayOsPaymentIntent a, PayOsPaymentIntent b)
        => a.Id == b.Id && a.PaymentId == b.PaymentId &&
           a.BuildingId == b.BuildingId && a.PayerId == b.PayerId &&
           a.OrderCode == b.OrderCode && a.AmountVnd == b.AmountVnd &&
           a.ProviderReference == b.ProviderReference &&
           b.Status == PayOsPaymentIntentStatus.ConfirmedPendingPosting;

    private Guid CurrentUser()
    {
        if (!_user.IsAuthenticated || _user.Id is not Guid id ||
            id == Guid.Empty)
            throw Denied();
        return id;
    }

    private static AbpAuthorizationException Denied()
        => new("D04 requires a current independent bank reviewer, unaltered source, and a different currently authorized Owner.");
}

public sealed record PayOsD04ManualBankStatementSubmission(
    string BankReference,
    long AmountVnd,
    byte[] StatementDocumentBytes,
    string ProtectedArchiveReference,
    bool ReviewerAttestsBankStatementMatched);
