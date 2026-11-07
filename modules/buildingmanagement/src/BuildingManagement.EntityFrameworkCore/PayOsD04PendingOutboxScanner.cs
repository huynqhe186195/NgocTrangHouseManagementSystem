using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Read-only pending receipts to drive a bounded, restartable D04 ACK pass.
/// It does not grant approval, create receipts or acknowledge anything.
/// </summary>
public interface IPayOsD04PendingOutboxScanner
{
    Task<IReadOnlyList<Guid>> ReadPendingIntentIdsAsync(
        int maxResults, CancellationToken cancellationToken);
}

/// <summary>
/// This is a source-only helper for an explicitly gated future recovery job.
/// NO background worker or DI registration has been activated.
/// </summary>
public sealed class PayOsD04PendingOutboxScanner : IPayOsD04PendingOutboxScanner
{
    private readonly BuildingManagementDbContext _db;

    public PayOsD04PendingOutboxScanner(BuildingManagementDbContext db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<Guid>> ReadPendingIntentIdsAsync(
        int maxResults, CancellationToken cancellationToken)
    {
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults),
                "A D04 recovery pass may read only 1-50 pending receipts.");

        // Do not open a transaction here: each subsequent ACK runs two
        // separate database commits, never under a distributed/global lock.
        var connection = _db.Database.GetDbConnection() as NpgsqlConnection ??
            throw new InvalidOperationException("D04 outbox needs PostgreSQL.");
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT j."PayOsIntentId"
                  FROM "BuildingManagementPayOsD04Outbox" o
                  JOIN "BuildingManagementPayOsD04ReceiptJournal" j
                    ON j."Id"=o."ReceiptId" AND j."SourceKey"=o."SourceKey"
                 WHERE o."AcknowledgedAtUtc" IS NULL
                 ORDER BY o."CreatedAtUtc",o."Id"
                 LIMIT @maxResults
                """, connection);
            cmd.Parameters.AddWithValue("maxResults", maxResults);
            var pending = new List<Guid>(maxResults);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pending.Add(reader.GetGuid(0));
            return pending;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }
}
