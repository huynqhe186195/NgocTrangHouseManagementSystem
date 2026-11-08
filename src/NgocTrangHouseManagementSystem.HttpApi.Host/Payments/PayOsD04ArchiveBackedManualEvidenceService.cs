using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Pluggable, server-controlled archive contract for D04-B. The implementation
/// must durably store PRIVATE evidence and verify it by independently reading
/// back the exact bytes before issuing a reference. No application Host DI
/// registration is provided: production remains OFF until key management,
/// storage ACLs, retention and independent bank review are approved.
/// </summary>
public interface IPayOsD04ProtectedBankEvidenceArchive
{
    Task<PayOsD04ProtectedBankEvidenceReceipt> StoreAndVerifyAsync(
        Guid intentId, Guid reviewerId, byte[] document,
        CancellationToken ct = default);

    Task<bool> VerifyAsync(
        Guid intentId, Guid reviewerId,
        string reference, string documentSha256,
        CancellationToken ct = default);
}

public sealed record PayOsD04ProtectedBankEvidenceReceipt(
    string Reference, string DocumentSha256);

/// <summary>
/// Fail-closed D04-B facade. It NEVER trusts a client-submitted archive
/// reference. Reviewer identity comes from the authenticated principal; the
/// generated immutable reference is checked against the actual stored bytes
/// and is substituted into the existing canonical SQL bank-review writer.
/// Owner approval re-reads the persisted reference AND checks its protected
/// file before the real approval service acquires the dual-actor locks.
/// The real service repeats all authoritative live account/building/source
/// checks in SERIALIZABLE transactions; this facade adds only archive proof.
/// </summary>
public sealed class PayOsD04ArchiveBackedManualEvidenceService :
    IPayOsD04ManualEvidenceService
{
    private readonly IPayOsD04ManualEvidenceService _inner;
    private readonly IPayOsD04ProtectedBankEvidenceArchive _archive;
    private readonly BuildingManagementDbContext _db;
    private readonly ICurrentUser _currentUser;

    public PayOsD04ArchiveBackedManualEvidenceService(
        IPayOsD04ManualEvidenceService inner,
        IPayOsD04ProtectedBankEvidenceArchive archive,
        BuildingManagementDbContext db,
        ICurrentUser currentUser)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<Guid> RecordIndependentBankReviewAsync(
        Guid intentId, PayOsD04ManualBankStatementSubmission evidence,
        CancellationToken ct = default)
    {
        var reviewer = RequireUser();
        if (intentId == Guid.Empty || evidence is null ||
            !evidence.ReviewerAttestsBankStatementMatched ||
            evidence.StatementDocumentBytes is null ||
            evidence.StatementDocumentBytes.Length is < 256 or > 5_242_880)
            throw Denied();

        // The request's ProtectedArchiveReference is explicitly IGNORED.
        // Never let callers supply a storage identifier or filesystem path.
        var receipt = await _archive.StoreAndVerifyAsync(
            intentId, reviewer, evidence.StatementDocumentBytes, ct);
        var expected = Convert.ToHexString(
            SHA256.HashData(evidence.StatementDocumentBytes)).ToLowerInvariant();
        if (!string.Equals(receipt.DocumentSha256, expected,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.Reference) ||
            receipt.Reference.Length > 256 ||
            !await _archive.VerifyAsync(intentId, reviewer,
                receipt.Reference, expected, ct))
            throw Denied();

        // The protected, server-issued reference enters the canonical
        // immutable SQL review, not the untrusted HTTP form field.
        return await _inner.RecordIndependentBankReviewAsync(intentId,
            evidence with { ProtectedArchiveReference = receipt.Reference }, ct);
    }

    public async Task<Guid> ApproveReviewedDepositAsync(
        Guid bankReviewId, CancellationToken ct = default)
    {
        _ = RequireUser();
        if (bankReviewId == Guid.Empty) throw Denied();

        // An orphan/missing/tampered archive cannot authorize Owner approval.
        // We fetch ONLY immutable committed SQL data, never the untrusted
        // form's client archive reference. The inner approval path still
        // locks & checks source, two current actors, Owner and Building.
        Guid intent, reviewer;
        string archiveReference, documentSha;
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var query = new NpgsqlCommand("""
                SELECT "PayOsIntentId","ReviewerId",
                       "ExternalBankProofReference","DocumentSha256"
                  FROM "BuildingManagementPayOsD04BankReviews"
                 WHERE "Id"=@review
                """, (NpgsqlConnection)_db.Database.GetDbConnection());
            query.Parameters.AddWithValue("review", bankReviewId);
            await using var row = await query.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw Denied();
            intent = row.GetGuid(0);
            reviewer = row.GetGuid(1);
            archiveReference = row.GetString(2);
            documentSha = row.GetString(3);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }

        if (!await _archive.VerifyAsync(
                intent, reviewer, archiveReference, documentSha, ct))
            throw Denied();
        return await _inner.ApproveReviewedDepositAsync(bankReviewId, ct);
    }

    private Guid RequireUser()
    {
        if (_currentUser.IsAuthenticated &&
            _currentUser.Id is Guid userId && userId != Guid.Empty)
            return userId;
        throw Denied();
    }

    private static AbpAuthorizationException Denied() =>
        new("D04-B independent bank evidence is unavailable or unauthorized.");
}
