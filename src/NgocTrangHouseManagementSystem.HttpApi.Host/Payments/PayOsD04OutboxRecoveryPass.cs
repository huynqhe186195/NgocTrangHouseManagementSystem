using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// A bounded, explicit one-pass recovery driver over the DURABLE legacy D04
/// outbox. No background polling, API endpoint, DI registration or automatic
/// financial writer is enabled by adding this source file.
///
/// A retry reads the same immutable intent key. Each item must re-prove its
/// committed legacy Payment + receipt + allocation and signed staging inbox;
/// only the payOS marker and legacy ACK fields can be changed. Failures are
/// counted but never silently marked successful.
/// </summary>
public sealed class PayOsD04OutboxRecoveryPass
{
    private readonly IPayOsD04PendingOutboxScanner _scanner;
    private readonly PayOsD04StagingReceiptAcknowledger _acknowledger;

    public PayOsD04OutboxRecoveryPass(
        IPayOsD04PendingOutboxScanner scanner,
        PayOsD04StagingReceiptAcknowledger acknowledger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _acknowledger = acknowledger ??
            throw new ArgumentNullException(nameof(acknowledger));
    }

    public async Task<PayOsD04RecoveryPassResult> RunOnceAsync(
        int maxResults, CancellationToken cancellationToken = default)
    {
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var ids = await _scanner.ReadPendingIntentIdsAsync(
            maxResults, cancellationToken);
        if (ids.Count > maxResults)
            throw new InvalidOperationException(
                "The D04 scanner exceeded its bounded recovery batch.");

        var newlyPosted = 0;
        var alreadyPosted = 0;
        var failures = new List<PayOsD04RecoveryFailure>();
        var seen = new HashSet<Guid>();

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == Guid.Empty || !seen.Add(id))
            {
                failures.Add(new PayOsD04RecoveryFailure(
                    id, "InvalidOrDuplicateOutboxSource"));
                continue;
            }
            try
            {
                var result = await _acknowledger.AcknowledgeAsync(
                    id, cancellationToken);
                switch (result)
                {
                    case PayOsD04AckOutcome.PostedAndOutboxAcknowledged:
                        newlyPosted++;
                        break;
                    case PayOsD04AckOutcome.RecoveredAlreadyPosted:
                        alreadyPosted++;
                        break;
                    default:
                        failures.Add(new PayOsD04RecoveryFailure(
                            id, "MissingCommittedLegacyProof"));
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Failure is NEVER an ACK. The durable row remains pending and
                // another explicit pass can re-read it after investigation.
                // Return an exception type, not bank evidence or PII.
                failures.Add(new PayOsD04RecoveryFailure(
                    id, e.GetType().Name));
            }
        }

        return new PayOsD04RecoveryPassResult(
            ids.Count, newlyPosted, alreadyPosted, failures);
    }
}

public sealed record PayOsD04RecoveryFailure(Guid IntentId, string ErrorKind);
public sealed record PayOsD04RecoveryPassResult(
    int Examined, int Posted, int AlreadyPosted,
    IReadOnlyList<PayOsD04RecoveryFailure> Failures);
