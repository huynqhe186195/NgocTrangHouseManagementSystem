using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.RoomChanges;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Root Owner-only H4 *documentary* deposit disposition. It computes the
/// original held deposit from locked, completed payment allocations. Client
/// input is restricted to a proposed new deposit requirement and signed
/// Owner choice, not original cash or deductions. No financial posting.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    public Task<SliceBRoomChangeH4IntentRead> CaptureH4DispositionIntentAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeH4IntentInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if (input is null ||
                db.Database.CurrentTransaction?.GetDbTransaction()
                    .IsolationLevel != IsolationLevel.Serializable)
                throw Denied();

            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, request.TargetRoomId,
                BuildingBusinessCapability.ManageContract);

            var roomIds = new[] { sourceRoom, request.TargetRoomId }
                .Distinct().OrderBy(id => id).ToArray();
            if (roomIds.Length != 2) throw Denied();

            (Guid Building, RoomStatus Status) originalRoom = default;
            (Guid Building, RoomStatus Status) destinationRoom = default;
            foreach (var id in roomIds)
            {
                var room = await LockRoomStatusAsync(db, id);
                if (id == sourceRoom) originalRoom = room;
                else destinationRoom = room;
            }
            if (originalRoom.Building != building ||
                destinationRoom.Building != building ||
                originalRoom.Status != RoomStatus.Occupied ||
                destinationRoom.Status != RoomStatus.Available ||
                request.Status is not (RoomChangeRequestStatus.Pending or
                    RoomChangeRequestStatus.PolicyHold) ||
                request.RequestedDate.Day <= 1 ||
                request.RequestedDate.Day >= DateTime.DaysInMonth(
                    request.RequestedDate.Year, request.RequestedDate.Month) ||
                await db.RoomChangeDepositDispositionIntents.AsNoTracking()
                    .AnyAsync(x => x.SourceContractId == contractId) ||
                await db.RoomChangeDepositDispositionIntents.AsNoTracking()
                    .AnyAsync(x => x.RequestId == request.Id) ||
                await db.RoomChangeCompletions.AsNoTracking()
                    .AnyAsync(x => x.RequestId == request.Id) ||
                await db.RoomChangeMidMonthCompletions.AsNoTracking()
                    .AnyAsync(x => x.RequestId == request.Id) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                    (x.Status == ContractStatus.Active ||
                     x.Status == ContractStatus.Signed)) ||
                await db.RoomReservations.AsNoTracking().AnyAsync(x =>
                    x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                    (x.Status == RoomReservationStatus.Reserved ||
                     x.Status == RoomReservationStatus.PendingPayment)))
                throw Denied();

            var source = await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted)
                ?? throw Denied();
            if (source.Status != ContractStatus.Active ||
                source.RoomId != sourceRoom || source.DepositAmount <= 0m)
                throw Denied();

            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.RoomChangeRequestId == request.Id &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            await LockCurrentRosterAsync(db, contractId);
            var currentRoster = await ReadCurrentRosterAsync(db, contractId);
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == request.Id &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == policy.Id &&
                    x.RequestId == request.Id &&
                    x.BuildingId == building) ?? throw Denied();
            var rule = await db.RoomChangeDayPricingRuleCandidates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == request.Id &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building &&
                    x.PolicyId == policy.Id &&
                    x.PolicyReviewId == review.Id) ?? throw Denied();

            try
            {
                if (!roster.MatchesCurrentRoster(request.Id, contractId,
                        sourceRoom, building, currentRoster) ||
                    !review.Accepted ||
                    policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
                    policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
                    policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants)
                    throw Denied();

                policy.Verify(request, roster);
                review.Verify(policy, request, roster);
                rule.Verify(request, source, roster, policy, review);
                RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableRule(
                    rule, request.RequestedDate);

                // This verifies completed source internal payment allocations,
                // not settlement at a bank or any future top-up.
                await VerifyTransferDepositAsync(db, source);

                var completedAllocations = await db.DepositSettlements.AsNoTracking()
                    .Where(x => x.TargetContractId == contractId &&
                        x.Type == DepositSettlementType.AllocateToContractDeposit &&
                        x.Status == DepositSettlementStatus.Completed)
                    .OrderBy(x => x.Id).ToArrayAsync();
                if (completedAllocations.Length == 0) throw Denied();
                var originalDigest = string.Join("\n", completedAllocations.Select(x =>
                    x.Id.ToString("D") + "|" + x.PaymentId!.Value.ToString("D") +
                    "|" + x.Amount.ToString("0", CultureInfo.InvariantCulture)));
                var sourceReference = "SOURCE-ALLOCATION-SHA256-" +
                    Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(originalDigest))).ToLowerInvariant();

                // The target policy's new deposit amount is an explicit
                // signed Owner PROPOSAL: there is no target contract or
                // independently verified new deposit policy yet.
                var facts = new RoomChangeDepositDifferenceFacts(
                    contractId, Guid.NewGuid(), source.DepositAmount, 0m,
                    input.TargetRequiredDepositVnd, sourceReference,
                    policy.DepositPolicyReference, null,
                    HasUnresolvedDispute: false,
                    HasEarlierRefundOrCarryForward: false);
                // Contract currently sets DepositAmount from MonthlyRent.
                // Reject an Owner proposal that no target contract can fulfill.
                if (input.TargetRequiredDepositVnd != rule.TargetMonthlyRentVnd)
                    throw Denied();
                var preview = RoomChangeDepositDifferencePreviewCalculator.Calculate(facts);
                if (preview.ProposedPolicyLane != policy.DepositTreatment ||
                    preview.ProposedPolicyLane == RoomChangeDepositTreatment.CarryAllWithNoDifference)
                    throw Denied();
                var disposition = RoomChangeDeferredDepositDispositionPlanner.Plan(
                    request.Id, facts, request.RequestedDate,
                    input.SurplusChoice, input.ProposedTopUpDueDateVietnam);
                var record = RoomChangeDepositDispositionIntentRecord.Capture(
                    Guid.NewGuid(), request, facts, disposition,
                    actor, _clock.GetUtcNow().UtcDateTime,
                    input.OwnerSignedDispositionReference);
                record.VerifyFingerprint();
                db.RoomChangeDepositDispositionIntents.Add(record);
                return AsH4DispositionRead(record);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                InvalidDataException or ArgumentException or OverflowException)
            {
                throw Denied();
            }
        });

    public Task<SliceBRoomChangeH4IntentRead> GetH4DispositionIntentAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            _ = await LockRequestAsync(db, requestId, contractId, sourceRoom, building);
            var row = await db.RoomChangeDepositDispositionIntents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            try { row.VerifyFingerprint(); }
            catch (InvalidDataException) { throw Denied(); }
            return AsH4DispositionRead(row);
        }, permitEndedContract: true);

    private static SliceBRoomChangeH4IntentRead AsH4DispositionRead(
        RoomChangeDepositDispositionIntentRecord row)
        => new(row.Id, row.RequestId, row.SourceContractId,
            row.TargetContractId, row.BuildingId, row.TransferDateVietnam,
            row.ClaimedOriginalSourceHeldVnd, row.ProposedCarryTargetVnd,
            row.TargetRequiredDepositVnd, row.ProposedUnpaidTopUpVnd,
            row.ProposedTopUpDueDateVietnam, row.ProposedFutureRentCreditVnd,
            row.ProposedManualRefundVnd, row.SurplusChoice,
            row.TargetDepositPolicyReference, row.OwnerSignedDispositionReference,
            row.ProposedByOwnerId, row.ProposedAtUtc, row.EvidenceSha256,
            IsBankCashVerified: false, IsFinancialPosting: false,
            CanExecuteTransfer: false);
}

public sealed record SliceBRoomChangeH4IntentInput(
    decimal TargetRequiredDepositVnd,
    DateOnly? ProposedTopUpDueDateVietnam,
    RoomChangeDepositSurplusChoice SurplusChoice,
    string? OwnerSignedDispositionReference);

public sealed record SliceBRoomChangeH4IntentRead(
    Guid Id, Guid RequestId, Guid SourceContractId,
    Guid ProspectiveTargetContractId, Guid BuildingId,
    DateOnly TransferDateVietnam,
    decimal RecordedSourceDepositVnd, decimal ProposedCarriedDepositVnd,
    decimal ProposedRequiredTargetDepositVnd, decimal UnpaidTopUpReceivableVnd,
    DateOnly? ProposedTopUpDueDateVietnam, decimal ProposedNextRentCreditVnd,
    decimal ProposedManualRefundVnd, RoomChangeDepositSurplusChoice SurplusChoice,
    string TargetDepositPolicyReference, string OwnerSignedDispositionReference,
    Guid ProposedByOwnerId, DateTime ProposedAtUtc, string EvidenceSha256,
    bool IsBankCashVerified, bool IsFinancialPosting, bool CanExecuteTransfer);
