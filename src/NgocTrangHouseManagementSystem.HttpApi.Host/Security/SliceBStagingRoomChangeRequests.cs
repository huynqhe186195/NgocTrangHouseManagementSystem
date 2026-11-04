using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Payments;
using BuildingManagement.Invoices;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomChanges;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Phase 2H isolated staging request/review ONLY. Never executes a transfer,
/// changes a contract/room or settles/carries a deposit. Every operation
/// requires current ABP Contracts.Manage + active Identity permission lease
/// and live Building membership locked through the same PostgreSQL UoW.
/// </summary>
public sealed class SliceBStagingRoomChangeRequests
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly ICurrentUser _current;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly TimeProvider _clock;

    public SliceBStagingRoomChangeRequests(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser current,
        SliceAStagingIdentityCommitLease identity,
        TimeProvider? clock = null)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _current = current;
        _identity = identity;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<SliceBRoomChangeRequestRead> SubmitAsync(
        Guid contractId, SliceBRoomChangeSubmitInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.TargetRoomId == Guid.Empty ||
                input.TargetRoomId == sourceRoom ||
                input.RequestedDate == default ||
                input.RequestedDate < DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))))
                throw Denied();

            // The target must be independently authorized; a guessed GUID
            // must never be usable to infer/mutate another building.
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, input.TargetRoomId,
                BuildingBusinessCapability.ManageContract);

            // Lock both rooms in stable GUID order. Source contract was
            // already FOR UPDATE locked, so two competing transfers cannot
            // bypass a source contract's single-open-request uniqueness.
            var rooms = new[] { sourceRoom, input.TargetRoomId }
                .OrderBy(x => x).ToArray();
            (Guid Building, RoomStatus Status) source = default;
            (Guid Building, RoomStatus Status) target = default;
            foreach (var roomId in rooms)
            {
                var locked = await LockRoomStatusAsync(db, roomId);
                if (roomId == sourceRoom) source = locked;
                else target = locked;
            }
            var contract = await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted);
            if (contract is null || contract.Status != ContractStatus.Active ||
                contract.RoomId != sourceRoom ||
                source.Building != building || target.Building != building ||
                source.Status != RoomStatus.Occupied ||
                target.Status != RoomStatus.Available ||
                await db.MoveOutRequests.AsNoTracking().AnyAsync(x =>
                    x.ContractId == contractId && !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved)) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.RoomId == input.TargetRoomId && !x.IsDeleted &&
                    (x.Status == ContractStatus.Active ||
                     x.Status == ContractStatus.Signed)) ||
                await db.RoomReservations.AsNoTracking().AnyAsync(x =>
                    x.RoomId == input.TargetRoomId && !x.IsDeleted &&
                    (x.Status == RoomReservationStatus.PendingPayment ||
                     x.Status == RoomReservationStatus.Reserved)))
                throw Denied();

            RoomChangeRequest request;
            try
            {
                request = RoomChangeRequest.Submit(
                    Guid.NewGuid(), contractId, sourceRoom,
                    input.TargetRoomId, building, input.RequestedDate,
                    input.Reason, actor, _clock.GetUtcNow().UtcDateTime, contract.Status,
                    source.Status, target.Status);
            }
            catch (InvalidOperationException) { throw Denied(); }

            db.RoomChangeRequests.Add(request);
            return AsRead(request);
        });

    public Task<SliceBRoomChangeRequestRead> GetAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var item = await db.RoomChangeRequests.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == requestId &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building) ?? throw Denied();
            return AsRead(item);
        }, permitEndedContract: true);

    public Task<SliceBRoomChangeRequestRead[]> GetListAsync(Guid contractId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var items = await db.RoomChangeRequests.AsNoTracking()
                .Where(x => x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(100)
                .ToArrayAsync();
            return items.Select(AsRead).ToArray();
        }, permitEndedContract: true);

    /// <summary>
    /// Live PostgreSQL-only assessment, not a transfer preflight or approval.
    /// Both rooms are locked in canonical order; the request and source
    /// contract are server-resolved and membership is locked through commit.
    /// All evidence comes from the database; no client-supplied booleans.
    /// </summary>
    public Task<SliceBRoomChangeLiveReadiness> GetLiveReadinessAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var rooms = new[] { sourceRoom, request.TargetRoomId }
                .Distinct().OrderBy(x => x).ToArray();
            if (rooms.Length != 2) throw Denied();
            (Guid Building, RoomStatus Status) source = default;
            (Guid Building, RoomStatus Status) target = default;
            foreach (var id in rooms)
            {
                var locked = await LockRoomStatusAsync(db, id);
                if (id == sourceRoom) source = locked;
                else target = locked;
            }
            var current = await db.Contracts.AsNoTracking()
                .SingleAsync(x => x.Id == contractId && !x.IsDeleted);
            var targetReservations = await db.RoomReservations.AsNoTracking()
                .AnyAsync(x => x.RoomId == request.TargetRoomId &&
                    !x.IsDeleted &&
                    (x.Status == RoomReservationStatus.PendingPayment ||
                     x.Status == RoomReservationStatus.Reserved));
            var targetContracts = await db.Contracts.AsNoTracking()
                .AnyAsync(x => x.RoomId == request.TargetRoomId &&
                    !x.IsDeleted &&
                    (x.Status == ContractStatus.Active ||
                     x.Status == ContractStatus.Signed));
            var moveOut = await db.MoveOutRequests.AsNoTracking()
                .AnyAsync(x => x.ContractId == contractId &&
                    !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved));
            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == request.Id);
            var rosterCurrent = false;
            if (roster is not null)
            {
                await LockCurrentRosterAsync(db, contractId);
                var parties = await ReadCurrentRosterAsync(db, contractId);
                try
                {
                    rosterCurrent = roster.MatchesCurrentRoster(
                        request.Id, contractId, sourceRoom, building, parties);
                }
                catch (System.IO.InvalidDataException) { throw Denied(); }
            }
            var facts = new RoomChangeRequestReadinessFacts(
                request.Id, contractId, sourceRoom, request.TargetRoomId,
                building, request.RequestedDate, today, request.Status,
                current.Status, source.Status, target.Status,
                source.Building, target.Building, targetReservations,
                targetContracts, moveOut, rosterCurrent);
            var result = RoomChangeRequestReadiness.Assess(facts);
            return new SliceBRoomChangeLiveReadiness(
                request.Id, result.CanContinuePolicyReview,
                true, result.Blockers.ToArray());
        }, permitEndedContract: true);

    /// <summary>
    /// Server snapshots ALL current contract parties; no self-selected
    /// tenant list, no identity details, no implied tenant consent.
    /// The Contract UPDATE lock serializes against tenant membership writes.
    /// Database trigger verifies the persisted list independently.
    /// </summary>
    public Task<SliceBRoomChangeRosterRead> CaptureRosterAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            if (request.Status is not
                (RoomChangeRequestStatus.Pending or RoomChangeRequestStatus.PolicyHold))
                throw Denied();

            await LockCurrentRosterAsync(db, contractId);
            var parties = await ReadCurrentRosterAsync(db, contractId);
            RoomChangeTenantRosterSnapshotRecord snapshot;
            try
            {
                snapshot = RoomChangeTenantRosterSnapshotRecord.Capture(
                    Guid.NewGuid(), request, parties, actor, _clock.GetUtcNow().UtcDateTime);
            }
            catch (InvalidOperationException) { throw Denied(); }
            db.RoomChangeRosterSnapshots.Add(snapshot);
            return AsRosterRead(snapshot);
        });

    public Task<SliceBRoomChangeRosterRead> GetRosterAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var snapshot = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == request.Id &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building) ?? throw Denied();
            try { return AsRosterRead(snapshot); }
            catch (System.IO.InvalidDataException) { throw Denied(); }
        }, permitEndedContract: true);

    private static async Task LockCurrentRosterAsync(
        BuildingManagementDbContext db, Guid contractId)
    {
        await using var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementContractTenants"
             WHERE "ContractId" = @contract AND "IsDeleted" = FALSE
             ORDER BY "Id" FOR SHARE
            """);
        Add(cmd, "contract", contractId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }

    private static Task<RoomChangeRosterMember[]> ReadCurrentRosterAsync(
        BuildingManagementDbContext db, Guid contractId)
        => db.ContractTenants.AsNoTracking()
            .Where(x => x.ContractId == contractId && !x.IsDeleted)
            .Select(x => new RoomChangeRosterMember(x.TenantId, x.Role))
            .ToArrayAsync();

    private static SliceBRoomChangeRosterRead AsRosterRead(
        RoomChangeTenantRosterSnapshotRecord record)
        => new(record.Id, record.RoomChangeRequestId,
            record.SourceContractId, record.SourceRoomId, record.BuildingId,
            record.CapturedByUserId, record.CapturedAtUtc,
            record.RosterCount, record.RosterSha256,
            record.ReadVerifiedRoster().ToArray(),
            false);

    /// <summary>
    /// Records one explicit proposed OI-07 choice set, never an approval
    /// or an execution. No client-supplied roster or financial booleans.
    /// </summary>
    public Task<SliceBRoomChangeBusinessPolicyRead> ProposeBusinessPolicyAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeBusinessPolicyInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null) throw Denied();
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom && x.BuildingId == building)
                ?? throw Denied();
            await LockCurrentRosterAsync(db, contractId);
            var current = await ReadCurrentRosterAsync(db, contractId);
            try
            {
                if (!roster.MatchesCurrentRoster(
                    request.Id, contractId, sourceRoom, building, current))
                    throw Denied();
                var proposed = RoomChangeBusinessPolicyRecord.Propose(
                    Guid.NewGuid(), request, roster, input.ContractTreatment,
                    input.PricingTreatment, input.DepositTreatment,
                    input.TenantTreatment, input.ContractPolicyReference,
                    input.PricingPolicyReference, input.DepositPolicyReference,
                    input.TenantConsentReference, actor, _clock.GetUtcNow().UtcDateTime);
                db.RoomChangeBusinessPolicies.Add(proposed);
                return AsPolicyRead(proposed);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            { throw Denied(); }
        });

    public Task<SliceBRoomChangeBusinessPolicyRead> GetBusinessPolicyAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var plan = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId && x.BuildingId == building)
                ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == plan.RosterSnapshotId &&
                    x.RoomChangeRequestId == requestId &&
                    x.SourceContractId == contractId) ?? throw Denied();
            try { plan.Verify(request, roster); }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            { throw Denied(); }
            var rootReview = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == plan.Id);
            if (rootReview is not null)
            {
                try { rootReview.Verify(plan, request, roster); }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.IO.InvalidDataException or ArgumentException)
                { throw Denied(); }
            }
            return AsPolicyRead(plan, rootReview?.Accepted == true);
        }, permitEndedContract: true);

    private static SliceBRoomChangeBusinessPolicyRead AsPolicyRead(
        RoomChangeBusinessPolicyRecord plan, bool approved = false)
        => new(plan.Id, plan.RequestId, plan.SourceContractId,
            plan.BuildingId, plan.RosterSnapshotId, plan.RosterSha256,
            plan.ContractTreatment, plan.PricingTreatment,
            plan.DepositTreatment, plan.TenantTreatment,
            plan.ContractPolicyReference, plan.PricingPolicyReference,
            plan.DepositPolicyReference, plan.TenantConsentReference,
            plan.ProposedByUserId, plan.ProposedAtUtc,
            plan.EvidenceSha256,
            IsApproved: approved, IsExecuted: false);

    /// <summary>
    /// One independent ROOT signs an existing immutable policy proposal.
    /// This is NEVER permission to transfer deposits or end a contract.
    /// </summary>
    public Task<SliceBRoomChangeBusinessPolicyReviewRead> ReviewBusinessPolicyAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeBusinessPolicyReviewInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null) throw Denied();
            await _identity.AcquireBootstrapAsync(actor);
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var plan = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == request.Id &&
                    x.SourceContractId == contractId && x.BuildingId == building)
                ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == plan.RosterSnapshotId &&
                    x.RoomChangeRequestId == requestId &&
                    x.SourceContractId == contractId) ?? throw Denied();
            RoomChangeBusinessPolicyReviewRecord decision;
            try
            {
                decision = RoomChangeBusinessPolicyReviewRecord.Review(
                    Guid.NewGuid(), plan, request, roster, actor,
                    _clock.GetUtcNow().UtcDateTime, input.Accepted, input.SignedReviewReference,
                    input.Reason);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            { throw Denied(); }
            db.RoomChangePolicyReviews.Add(decision);
            return AsPolicyReviewRead(decision);
        });

    public Task<SliceBRoomChangeBusinessPolicyReviewRead> GetBusinessPolicyReviewAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var plan = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId && x.BuildingId == building)
                ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == plan.Id &&
                    x.RequestId == requestId && x.BuildingId == building)
                ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == plan.RosterSnapshotId &&
                    x.RoomChangeRequestId == requestId &&
                    x.SourceContractId == contractId) ?? throw Denied();
            try { review.Verify(plan, request, roster); }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            { throw Denied(); }
            return AsPolicyReviewRead(review);
        }, permitEndedContract: true);

    private static SliceBRoomChangeBusinessPolicyReviewRead AsPolicyReviewRead(
        RoomChangeBusinessPolicyReviewRecord review)
        => new(review.Id, review.PolicyId, review.RequestId,
            review.BuildingId, review.ProposalSha256, review.ReviewedByUserId,
            review.ReviewedAtUtc, review.Accepted,
            review.SignedReviewReference, review.Reason,
            IsTransferExecuted: false);

    /// <summary>
    /// D01/D02: only a scoped, reviewed, immutable pricing candidate.
    /// Does NOT post rent, adjust an invoice or complete a room transfer.
    /// Client sends NO signed-reference, receipts, debt or authorization flags.
    /// </summary>
    public Task<SliceBRoomChangeDayPricingRuleRead> ProposeDayPricingRuleAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeDayPricingRuleInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.TargetMonthlyRentVnd <= 0m ||
                decimal.Truncate(input.TargetMonthlyRentVnd) !=
                    input.TargetMonthlyRentVnd)
                throw Denied();

            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, request.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var destination = await LockRoomStatusAsync(db, request.TargetRoomId);
            if (destination.Building != building ||
                destination.Status != RoomStatus.Available)
                throw Denied();

            var source = await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contractId &&
                    !x.IsDeleted && x.Status == ContractStatus.Active)
                ?? throw Denied();
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId && x.BuildingId == building)
                ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == policy.Id &&
                    x.RequestId == requestId && x.BuildingId == building &&
                    x.Accepted) ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == policy.RosterSnapshotId &&
                    x.RoomChangeRequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building) ?? throw Denied();

            await LockCurrentRosterAsync(db, contractId);
            var parties = await ReadCurrentRosterAsync(db, contractId);
            try
            {
                if (!roster.MatchesCurrentRoster(
                    request.Id, contractId, sourceRoom, building, parties))
                    throw Denied();

                var existing = await db.RoomChangeDayPricingRuleCandidates
                    .AsNoTracking().SingleOrDefaultAsync(x =>
                        x.RequestId == requestId);
                if (existing is not null)
                {
                    existing.Verify(request, source, roster, policy, review);
                    if (existing.TargetMonthlyRentVnd != input.TargetMonthlyRentVnd ||
                        existing.TransferDayOwner != input.TransferDayOwner ||
                        existing.Rounding != input.Rounding)
                        throw Denied();
                    return AsDayPricingRuleRead(existing);
                }

                var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
                    request.RequestedDate, source.MonthlyRent,
                    input.TargetMonthlyRentVnd, input.TransferDayOwner,
                    input.Rounding, policy.PricingPolicyReference);
                var candidate = RoomChangeDayPricingRuleCandidate.Prepare(
                    Guid.NewGuid(), request, source, roster, policy, review,
                    quote, actor, _clock.GetUtcNow().UtcDateTime);
                db.RoomChangeDayPricingRuleCandidates.Add(candidate);
                return AsDayPricingRuleRead(candidate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException or
                OverflowException)
            {
                throw Denied();
            }
        });

    public Task<SliceBRoomChangeDayPricingRuleRead> GetDayPricingRuleAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var candidate = await db.RoomChangeDayPricingRuleCandidates
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building) ?? throw Denied();
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == candidate.PolicyId &&
                    x.RequestId == requestId &&
                    x.SourceContractId == contractId) ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == candidate.PolicyReviewId &&
                    x.PolicyId == policy.Id) ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == candidate.RosterSnapshotId &&
                    x.SourceContractId == contractId) ?? throw Denied();
            var source = await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contractId &&
                    !x.IsDeleted) ?? throw Denied();
            await LockCurrentRosterAsync(db, contractId);
            var parties = await ReadCurrentRosterAsync(db, contractId);
            try
            {
                if (!roster.MatchesCurrentRoster(
                    request.Id, contractId, sourceRoom, building, parties))
                    throw Denied();
                candidate.Verify(request, source, roster, policy, review);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException or
                OverflowException)
            {
                throw Denied();
            }
            return AsDayPricingRuleRead(candidate);
        });

    private static SliceBRoomChangeDayPricingRuleRead AsDayPricingRuleRead(
        RoomChangeDayPricingRuleCandidate r)
        => new(r.Id, r.RequestId, r.SourceContractId, r.SourceRoomId,
            r.TargetRoomId, r.BuildingId, r.TransferDate, r.TransferDayOwner,
            r.Rounding, r.CalendarDays, r.SourceChargeableDays,
            r.TargetChargeableDays, r.SourceMonthlyRentVnd,
            r.TargetMonthlyRentVnd, r.ProposedSourceRentVnd,
            r.ProposedTargetRentVnd, r.SignedPricingRuleReference,
            r.PreparedByUserId, r.PreparedAtUtc, r.EvidenceSha256,
            IsOfficialInvoicePosting: false, CanExecuteRoomChange: false);

    /// <summary>
    /// Physical evidence is materialized from CURRENT database readings,
    /// never from client values. A document reference is operator attestation,
    /// not an external cryptographic signature verification.
    /// </summary>
    public Task<SliceBRoomChangeHandoverRead> AttestHandoverAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeHandoverInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null) throw Denied();
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            if (request.RequestedDate > today ||
                await db.RoomChangeHandoverEvidence.AsNoTracking()
                    .AnyAsync(x => x.RequestId == requestId))
                throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId && x.Accepted)
                ?? throw Denied();
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == review.PolicyId &&
                    x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == policy.RosterSnapshotId &&
                    x.RoomChangeRequestId == requestId) ?? throw Denied();
            await LockCurrentRosterAsync(db, contractId);
            var current = await ReadCurrentRosterAsync(db, contractId);
            try
            {
                review.Verify(policy, request, roster);
                if (!roster.MatchesCurrentRoster(
                    requestId, contractId, sourceRoom, building, current))
                    throw Denied();
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            { throw Denied(); }

            var original = await LoadHandoverMetersAsync(db, sourceRoom,
                request.RequestedDate);
            RoomChangeHandoverEvidenceRecord signed;
            try
            {
                signed = RoomChangeHandoverEvidenceRecord.Attest(
                    Guid.NewGuid(), request, original.E, original.Er,
                    original.W, original.Wr,
                    input.InspectionEvidenceReference,
                    input.SignedHandoverReference, actor, _clock.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                ArgumentException) { throw Denied(); }
            db.RoomChangeHandoverEvidence.Add(signed);
            return AsHandoverRead(signed);
        });

    public Task<SliceBRoomChangeHandoverRead> GetHandoverAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var stored = await db.RoomChangeHandoverEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == request.Id &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom && x.BuildingId == building)
                ?? throw Denied();
            var meters = await LoadHandoverMetersAsync(
                db, sourceRoom, request.RequestedDate);
            try { stored.Verify(request, meters.E, meters.Er, meters.W, meters.Wr); }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException) { throw Denied(); }
            return AsHandoverRead(stored);
        }, permitEndedContract: true);

    private static async Task<(UtilityMeter E, MeterReading Er,
        UtilityMeter W, MeterReading Wr)> LoadHandoverMetersAsync(
        BuildingManagementDbContext db, Guid room, DateOnly date)
    {
        // Lock all potential originals in stable UUID order. A concurrent
        // meter replacement or reading edit must serialize through the UoW.
        await using (var meterLock = Command(db, """
            SELECT "Id" FROM "BuildingManagementUtilityMeters"
             WHERE "RoomId"=@room AND "IsDeleted"=FALSE
             ORDER BY "Id" FOR SHARE
            """))
        {
            Add(meterLock, "room", room);
            await using var reader = await meterLock.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        var meters = await db.UtilityMeters.AsNoTracking()
            .Where(x => x.RoomId == room && !x.IsDeleted &&
                x.InstalledDate.Date <= date.ToDateTime(TimeOnly.MinValue))
            .ToArrayAsync();
        // Require exactly one physical reading per utility *on that date*.
        // Missing/duplicate historical sources fail closed.
        if (meters.Count(x => x.UtilityType == UtilityType.Electricity) != 1 ||
            meters.Count(x => x.UtilityType == UtilityType.Water) != 1)
            throw Denied();

        var e = meters.Single(x => x.UtilityType == UtilityType.Electricity);
        var w = meters.Single(x => x.UtilityType == UtilityType.Water);
        var ids = new[] { e.Id, w.Id }.OrderBy(x => x).ToArray();
        await using (var readingLock = Command(db, """
            SELECT "Id" FROM "BuildingManagementMeterReadings"
             WHERE "UtilityMeterId" IN (@e,@w)
               AND "ReadingDate"=@date AND "IsDeleted"=FALSE
             ORDER BY "Id" FOR SHARE
            """))
        {
            Add(readingLock, "e", e.Id);
            Add(readingLock, "w", w.Id);
            var dateParam = readingLock.CreateParameter();
            dateParam.ParameterName = "date";
            dateParam.Value = date.ToDateTime(TimeOnly.MinValue);
            readingLock.Parameters.Add(dateParam);
            await using var reader = await readingLock.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        var readings = await db.MeterReadings.AsNoTracking()
            .Where(x => ids.Contains(x.UtilityMeterId) && !x.IsDeleted &&
                x.ReadingDate.Date == date.ToDateTime(TimeOnly.MinValue))
            .ToArrayAsync();
        if (readings.Count(x => x.UtilityMeterId == e.Id) != 1 ||
            readings.Count(x => x.UtilityMeterId == w.Id) != 1)
            throw Denied();
        return (e, readings.Single(x => x.UtilityMeterId == e.Id),
            w, readings.Single(x => x.UtilityMeterId == w.Id));
    }

    private static SliceBRoomChangeHandoverRead AsHandoverRead(
        RoomChangeHandoverEvidenceRecord record)
        => new(record.Id, record.RequestId, record.SourceContractId,
            record.SourceRoomId, record.HandoverDate,
            record.ElectricityMeterId, record.ElectricityReadingId,
            record.ElectricityFinalReading, record.WaterMeterId,
            record.WaterReadingId, record.WaterFinalReading,
            record.InspectionEvidenceReference, record.SignedHandoverReference,
            record.AttestedByUserId, record.AttestedAtUtc,
            record.EvidenceSha256, false, false);

    /// <summary>
    /// Source-only financial+physical transfer rehearsal. No client fact
    /// switches, no authorized transfer writer, no deposit mutation.
    /// </summary>
    public Task<SliceBRoomChangeExecutionReadiness> GetExecutionReadinessAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var rooms = new[] { sourceRoom, request.TargetRoomId }
                .Distinct().OrderBy(x => x).ToArray();
            if (rooms.Length != 2) throw Denied();
            (Guid Building, RoomStatus Status) source = default, target = default;
            foreach (var id in rooms)
            {
                var locked = await LockRoomStatusAsync(db, id);
                if (id == sourceRoom) source = locked;
                else target = locked;
            }
            var contract = await db.Contracts.AsNoTracking()
                .SingleAsync(x => x.Id == contractId && !x.IsDeleted);
            var competitors = await db.RoomChangeRequests.AsNoTracking()
                .AnyAsync(x => x.Id != requestId &&
                    x.BuildingId == building && x.TargetRoomId == request.TargetRoomId &&
                    (x.Status == RoomChangeRequestStatus.Pending ||
                     x.Status == RoomChangeRequestStatus.PolicyHold));
            var committed = await db.RoomReservations.AsNoTracking()
                .AnyAsync(x => x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                    (x.Status == RoomReservationStatus.PendingPayment ||
                     x.Status == RoomReservationStatus.Reserved)) ||
                await db.Contracts.AsNoTracking()
                    .AnyAsync(x => x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                        (x.Status == ContractStatus.Active ||
                         x.Status == ContractStatus.Signed));
            var moveOut = await db.MoveOutRequests.AsNoTracking()
                .AnyAsync(x => x.ContractId == contractId && !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved));
            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == requestId);
            var rosterCurrent = false;
            if (roster is not null)
            {
                await LockCurrentRosterAsync(db, contractId);
                try
                {
                    rosterCurrent = roster.MatchesCurrentRoster(
                        requestId, contractId, sourceRoom, building,
                        await ReadCurrentRosterAsync(db, contractId));
                }
                catch (System.IO.InvalidDataException) { throw Denied(); }
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                _clock.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            var live = RoomChangeRequestReadiness.Assess(
                new RoomChangeRequestReadinessFacts(requestId, contractId,
                    sourceRoom, request.TargetRoomId, building,
                    request.RequestedDate, today, request.Status, contract.Status,
                    source.Status, target.Status, source.Building, target.Building,
                    committed, committed, moveOut, rosterCurrent));
            var plan = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId);
            var review = plan is null ? null :
                await db.RoomChangePolicyReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.PolicyId == plan.Id);
            var approved = false;
            if (roster is not null && plan is not null && review is not null)
            {
                try { review.Verify(plan, request, roster); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or
                    InvalidOperationException or ArgumentException) { throw Denied(); }
                approved = review.Accepted && rosterCurrent;
            }
            var handover = await db.RoomChangeHandoverEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId);
            var handedOver = false;
            if (handover is not null)
            {
                var originals = await LoadHandoverMetersAsync(
                    db, sourceRoom, request.RequestedDate);
                try
                {
                    handover.Verify(request, originals.E, originals.Er,
                        originals.W, originals.Wr);
                    handedOver = true;
                }
                catch (System.IO.InvalidDataException) { throw Denied(); }
            }

            var issuedFinal = await db.IssuedInvoiceLedger.AsNoTracking()
                .CountAsync(x => x.ContractId == contractId);
            var issuedMonthly = await db.MonthlyIssuedInvoices.AsNoTracking()
                .CountAsync(x => x.ContractId == contractId);
            var postings = await db.OfficialInvoiceReceivables.AsNoTracking()
                .Where(x => x.ContractId == contractId).ToArrayAsync();
            // The signed full-month lane settles the last source month
            // through the officially posted monthly ledger; a second final
            // invoice is not mandatory and would double-bill those charges.
            var sourceMonthPosted = await db.MonthlyIssuedInvoices.AsNoTracking()
                .AnyAsync(x => x.ContractId == contractId &&
                    x.BillingYear == request.RequestedDate.Year &&
                    x.BillingMonth == request.RequestedDate.Month);
            var allPaid = sourceMonthPosted &&
                postings.Length == issuedFinal + issuedMonthly;
            if (allPaid)
            {
                var reviews = await db.OfficialInvoiceReceiptReviews.AsNoTracking()
                    .Where(x => x.ContractId == contractId).ToArrayAsync();
                var adjustments = await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                    .Where(x => x.ContractId == contractId).ToArrayAsync();
                allPaid = !reviews.Any(x => !x.Accepted ||
                    !postings.Any(p => p.Id == x.ReceivablePostingId)) &&
                    !adjustments.Any() &&
                    postings.All(p => p.BuildingId == building &&
                        p.RoomId == sourceRoom &&
                        reviews.Where(r => r.ReceivablePostingId == p.Id)
                            .Sum(r => r.Amount) == p.OriginalAmount);
            }

            var related = await db.DepositSettlements.AsNoTracking()
                .Where(x => x.SourceContractId == contractId ||
                    x.TargetContractId == contractId).ToArrayAsync();
            var incoming = related.Where(x =>
                x.TargetContractId == contractId &&
                x.Type == DepositSettlementType.AllocateToContractDeposit)
                .ToArray();
            var ids = incoming.Where(x => x.PaymentId.HasValue)
                .Select(x => x.PaymentId!.Value).Distinct().ToArray();
            var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                .Where(x => ids.Contains(x.Id)).ToArrayAsync();
            var allocations = await db.DepositSettlements.AsNoTracking()
                .Where(x => x.PaymentId.HasValue &&
                    ids.Contains(x.PaymentId.Value)).ToArrayAsync();
            var depositProven = contract.DepositAmount > 0m &&
                incoming.Length > 0 && incoming.Sum(x => x.Amount) ==
                    contract.DepositAmount &&
                incoming.All(x => x.Status == DepositSettlementStatus.Completed &&
                    x.PaymentId.HasValue && x.Amount > 0m) &&
                payments.Length == ids.Length &&
                incoming.All(x => payments.Any(p => p.Id == x.PaymentId &&
                    p.Amount == x.Amount &&
                    p.Status == PaymentStatus.Completed && !p.IsDeleted &&
                    p.PaidAt.HasValue &&
                    (p.Purpose == PaymentPurpose.ContractDeposit ||
                     p.Purpose == PaymentPurpose.ReservationDeposit))) &&
                allocations.All(x => x.Type ==
                    DepositSettlementType.AllocateToContractDeposit &&
                    x.Status == DepositSettlementStatus.Completed &&
                    x.TargetContractId == contractId);
            var depositUnspent = !related.Any(x =>
                x.SourceContractId == contractId ||
                x.Status == DepositSettlementStatus.Pending ||
                x.Type != DepositSettlementType.AllocateToContractDeposit);
            var supported = approved && plan is not null &&
                plan.ContractTreatment == RoomChangeContractTreatment.NewContract &&
                plan.PricingTreatment == RoomChangePricingTreatment.FullOldMonthNewNextMonth &&
                plan.DepositTreatment == RoomChangeDepositTreatment.CarryAllWithNoDifference &&
                plan.TenantTreatment == RoomChangeTenantTreatment.AllCurrentTenants &&
                request.RequestedDate.Day == DateTime.DaysInMonth(
                    request.RequestedDate.Year, request.RequestedDate.Month) &&
                contract.MonthlyRent == contract.DepositAmount;
            var monthBilled = false;
            if (handedOver)
            {
                var sourceMonth = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.BillingYear == request.RequestedDate.Year &&
                        x.BillingMonth == request.RequestedDate.Month);
                var archived = sourceMonth is null ? null :
                    await db.MonthlyInvoiceCandidates.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == sourceMonth.CandidateId);
                if (archived is not null)
                {
                    try
                    {
                        var evidence = archived.VerifyStoredEvidence();
                        monthBilled = evidence.UtilityLines.Count == 2 &&
                            evidence.UtilityLines.Any(line =>
                                line.UtilityType == BuildingManagement.Utilities.UtilityType.Electricity &&
                                line.Sources.Any(x => x.SourceId ==
                                    handover!.ElectricityReadingId)) &&
                            evidence.UtilityLines.Any(line =>
                                line.UtilityType == BuildingManagement.Utilities.UtilityType.Water &&
                                line.Sources.Any(x => x.SourceId ==
                                    handover!.WaterReadingId));
                    }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException or
                        InvalidOperationException or ArgumentException) { throw Denied(); }
                }
            }
            var currentSource = request.Status is
                    (RoomChangeRequestStatus.Pending or RoomChangeRequestStatus.PolicyHold) &&
                request.RequestedDate <= today &&
                contract.Status == ContractStatus.Active &&
                source.Status == RoomStatus.Occupied &&
                target.Status == RoomStatus.Available &&
                source.Building == building && target.Building == building &&
                !moveOut && rosterCurrent;
            var assessed = RoomChangeExecutionReadiness.Assess(
                new RoomChangeExecutionFacts(currentSource,
                    approved, handedOver, competitors, sourceMonthPosted, allPaid,
                    depositProven, depositUnspent, !committed &&
                    target.Building == building &&
                    target.Status == RoomStatus.Available,
                    supported, monthBilled, true));
            return new SliceBRoomChangeExecutionReadiness(
                requestId, assessed.CanExecuteTransfer,
                assessed.Blockers.ToArray(), false);
        }, permitEndedContract: true);

    /// <summary>
    /// Root-only, SERIALIZABLE atomic execution of the explicitly signed
    /// NEW-CONTRACT / FULL-OLD-MONTH / EQUAL-DEPOSIT / ALL-TENANTS OI-07 lane.
    /// No inferred proration, no partial tenants and no silent refund/topup.
    /// Old invoices, originals and contract parties remain untouched.
    /// </summary>

    /// <summary>
    /// Authenticated, building-scoped, committed PostgreSQL D03/D04 journal
    /// READ. The original entries are immutable but this projection is
    /// NOT authenticated banking proof, not a transfer approval and not
    /// a mid-month transactional writer. No user-provided monetary totals.
    /// The same identity lease + membership commit fence used by the
    /// other staging business routes applies here.
    /// </summary>
    public Task<SliceBRoomChangeFinancialJournalRead> GetMidMonthFinancialJournalAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            if (db.Database.CurrentTransaction?.GetDbTransaction()
                    .IsolationLevel != System.Data.IsolationLevel.Serializable)
                throw Denied();

            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, request.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var room = await LockRoomStatusAsync(db, request.TargetRoomId);
            if (room.Building != building || request.BuildingId != building ||
                request.SourceContractId != contractId ||
                request.SourceRoomId != sourceRoom)
                throw Denied();

            int correctionCount = 0, topUpCount = 0;
            decimal correctionDelta = 0m, topUpAmount = 0m;
            await using (var cmd = Command(db, """
                SELECT "Kind", "SignedAmountVnd"
                  FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
                 WHERE "RequestId"=@request
                   AND "SourceContractId"=@contract
                   AND "BuildingId"=@building
                 ORDER BY "Id" FOR SHARE
                """))
            {
                Add(cmd, "request", requestId);
                Add(cmd, "contract", contractId);
                Add(cmd, "building", building);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var kind = reader.GetInt32(0);
                    var amount = reader.GetDecimal(1);
                    switch (kind)
                    {
                        case 1:
                            correctionCount++;
                            correctionDelta = checked(correctionDelta + amount);
                            break;
                        case 2:
                            topUpCount++;
                            if (amount <= 0m) throw Denied();
                            topUpAmount = checked(topUpAmount + amount);
                            break;
                        default:
                            // Unknown journal type or outgoing refund must
                            // not appear as executed in an operator view.
                            throw Denied();
                    }
                }
            }

            return new SliceBRoomChangeFinancialJournalRead(
                request.Id, contractId, building,
                correctionCount, correctionDelta,
                topUpCount, topUpAmount,
                IsActualOutboundBankRefundVerified: false,
                IsMidMonthContractCommitEnabled: false,
                CanExecuteTransfer: false);
        });


    /// <summary>
    /// Single SERIALIZABLE H1/H2/H3/H5/H6 same-deposit mid-month transfer.
    /// Explicit D01/D02 signed pricing, D03 source paid adjustment and
    /// D06 physical owner/staff/tenant documentary evidence are required.
    /// Top-ups/refunds and partial roster moves are NOT permitted in this
    /// narrow executable lane. New room's first-month prorated rent is
    /// frozen and enforced against its future issued invoice by PostgreSQL.
    /// </summary>

    /// <summary>
    /// Root Owner-signed exact per-request D01/D02 tariff; all inputs except
    /// the signed document reference are read from trusted scoped sources.
    /// </summary>
    public Task<SliceBRoomChangeArchiveRead> ArchiveExactPricingAsync(
        Guid contractId, Guid requestId, string? signedApprovalReference)
        => ExecuteScopedAsync(contractId, async(db,actor,building,sourceRoom)=>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if (!ValidEvidence(signedApprovalReference)) throw Denied();
            var req=await LockRequestAsync(db,requestId,contractId,sourceRoom,building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room,req.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            if (await db.RoomChangeEvidenceArchives.AsNoTracking().AnyAsync(x=>
                 x.RequestId==requestId &&
                 x.Kind==RoomChangeEvidenceArchiveKind.SignedExactPricing))
                throw Denied();
            var source=await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==contractId && !x.IsDeleted &&
                    x.Status==ContractStatus.Active)??throw Denied();
            var roster=await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.RoomChangeRequestId==requestId)
                ?? throw Denied();
            await LockCurrentRosterAsync(db,contractId);
            if (!roster.MatchesCurrentRoster(requestId,contractId,sourceRoom,
                    building,await ReadCurrentRosterAsync(db,contractId)))
                throw Denied();
            var policy=await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.RequestId==requestId)??throw Denied();
            var review=await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.PolicyId==policy.Id && x.Accepted)
                ??throw Denied();
            var rule=await db.RoomChangeDayPricingRuleCandidates.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.RequestId==requestId)??throw Denied();
            try
            {
                var now=_clock.GetUtcNow().UtcDateTime;
                var signed=RoomChangeDayPricingOwnerSignoff.Sign(
                    Guid.NewGuid(),rule,req,source,roster,policy,review,
                    actor,now,signedApprovalReference);
                var archive=RoomChangeEvidenceArchiveRecord.CapturePricing(
                    Guid.NewGuid(),req,source,roster,policy,review,rule,signed,now);
                db.RoomChangeEvidenceArchives.Add(archive);
                return AsArchiveRead(archive);
            }
            catch(Exception ex) when(ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            {throw Denied();}
        });

    /// <summary>
    /// Three role references must agree with original signed handover,
    /// current exact primary tenant and whole roster. This is documentary
    /// attestation and is NOT a cryptographic signature service.
    /// </summary>
    public Task<SliceBRoomChangeArchiveRead> ArchiveThreePartyHandoverAsync(
        Guid contractId, Guid requestId,
        SliceBRoomChangeThreePartyArchiveInput input)
        => ExecuteScopedAsync(contractId,async(db,actor,building,sourceRoom)=>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if(input is null)throw Denied();
            var req=await LockRequestAsync(db,requestId,contractId,sourceRoom,building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room,req.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            if (await db.RoomChangeEvidenceArchives.AsNoTracking().AnyAsync(x=>
                 x.RequestId==requestId &&
                 x.Kind==RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover))
                throw Denied();
            var roster=await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.RoomChangeRequestId==requestId)
                ??throw Denied();
            await LockCurrentRosterAsync(db,contractId);
            if(!roster.MatchesCurrentRoster(requestId,contractId,sourceRoom,
                  building,await ReadCurrentRosterAsync(db,contractId)))
                throw Denied();
            var physical=await db.RoomChangeHandoverEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.RequestId==requestId)
                ??throw Denied();
            var meter=await LoadHandoverMetersAsync(db,sourceRoom,req.RequestedDate);
            try
            {
                var now=_clock.GetUtcNow().UtcDateTime;
                var signed=RoomChangeHandoverThreePartySignoff.Sign(
                    Guid.NewGuid(),req,roster,physical,
                    actor,physical.AttestedByUserId,
                    input.TenantRepresentativeId,
                    input.OwnerSignedReference,input.StaffSignedReference,
                    input.TenantSignedReference,input.AssetDefectReviewReference,now);
                var archive=RoomChangeEvidenceArchiveRecord.CaptureThreePartyHandover(
                    Guid.NewGuid(),req,roster,physical,
                    meter.E,meter.Er,meter.W,meter.Wr,signed,now);
                db.RoomChangeEvidenceArchives.Add(archive);
                return AsArchiveRead(archive);
            }
            catch(Exception ex) when(ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            {throw Denied();}
        });

    /// <summary>
    /// Independently classified D03 amendment archived against original
    /// issued invoice, accepted adjustment decision and all signed lines.
    /// </summary>
    public Task<SliceBRoomChangeArchiveRead> ArchiveClassifiedD03Async(
        Guid contractId,Guid requestId,SliceBRoomChangeD03ClassificationInput input)
        => ExecuteScopedAsync(contractId,async(db,actor,building,sourceRoom)=>
        {
            // A distinct Contracts.Manage building-scoped classifier must be
            // able to sign this evidence. Root-only would make the
            // independent D03 posting gate impossible to satisfy.
            // ExecuteScopedAsync already enforces live Identity and
            // Building membership all the way to commit.
            if(input is null || input.AdjustmentId==Guid.Empty ||
                input.Lines is null || input.Lines.Length==0 ||
                input.Lines.Any(x=>x is null))
                throw Denied();
            var req=await LockRequestAsync(db,requestId,contractId,sourceRoom,building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room,req.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var proposal=await db.MonthlyInvoiceAdjustments.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==input.AdjustmentId &&
                    x.ContractId==contractId && x.BuildingId==building)
                ??throw Denied();
            var decision=await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.AdjustmentId==proposal.Id && x.Accepted)
                ??throw Denied();
            var issued=await db.MonthlyIssuedInvoices.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==proposal.IssuedInvoiceId &&
                    x.ContractId==contractId && x.RoomId==sourceRoom &&
                    x.BuildingId==building &&
                    x.BillingYear==req.RequestedDate.Year &&
                    x.BillingMonth==req.RequestedDate.Month)
                ??throw Denied();
            var candidate=await db.MonthlyInvoiceCandidates.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==issued.CandidateId)
                ??throw Denied();
            var review=await db.MonthlyInvoiceReviews.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==issued.ReviewId && x.Accepted)
                ??throw Denied();
            try
            {
                var lines=input.Lines.Select(x=>
                    new RoomChangeIssuedAdjustmentCategoryLine(
                        x.Category,x.SignedDeltaVnd,
                        x.SignedCategoryEvidenceReference)).ToArray();
                var now=_clock.GetUtcNow().UtcDateTime;
                var signed=RoomChangeIssuedAdjustmentCategoryProof.Capture(
                    Guid.NewGuid(),issued,proposal,decision,lines,
                    actor,now,input.SignedClassificationReference);
                var archive=RoomChangeEvidenceArchiveRecord.CaptureClassifiedCorrection(
                    Guid.NewGuid(),req,issued,candidate,review,
                    proposal,decision,signed,now);
                db.RoomChangeEvidenceArchives.Add(archive);
                return AsArchiveRead(archive);
            }
            catch(Exception ex) when(ex is InvalidOperationException or
                System.IO.InvalidDataException or ArgumentException)
            {throw Denied();}
        });

    /// <summary>
    /// Post one official D03 receivable correction from the ORIGINAL
    /// independent monthly decision and archived category signature.
    /// SQL dedupes by original AdjustmentId, not by archive ID or request.
    /// Never creates an incoming receipt, refund or deposit payment.
    /// </summary>
    public Task<SliceBRoomChangeD03PostingRead> PostClassifiedD03Async(
        Guid contractId,Guid requestId,Guid adjustmentId)
        => ExecuteScopedAsync(contractId,async(db,actor,building,sourceRoom)=>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if (adjustmentId==Guid.Empty ||
                db.Database.CurrentTransaction?.GetDbTransaction()
                   .IsolationLevel != System.Data.IsolationLevel.Serializable)
                throw Denied();
            var req=await LockRequestAsync(db,requestId,contractId,sourceRoom,building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room,req.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var proposal=await db.MonthlyInvoiceAdjustments.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Id==adjustmentId &&
                    x.ContractId==contractId && x.BuildingId==building)
                ??throw Denied();
            var decision=await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.AdjustmentId==adjustmentId &&
                    x.Accepted && x.ApprovedSignedDeltaAmount==
                        proposal.SignedDeltaAmount)
                ??throw Denied();
            var classified=await db.RoomChangeEvidenceArchives.AsNoTracking()
                .Where(x=>x.RequestId==requestId &&
                    x.Kind==RoomChangeEvidenceArchiveKind.ClassifiedInvoiceCorrection &&
                    x.ParentEvidenceId==proposal.IssuedInvoiceId)
                .ToArrayAsync();
            RoomChangeEvidenceArchiveRecord? archive=null;
            foreach(var evidence in classified)
            {
                try
                {
                    evidence.VerifyStoredSnapshot();
                    using var item=System.Text.Json.JsonDocument.Parse(evidence.EvidenceJson);
                    if(item.RootElement.GetProperty("AdjustmentId").GetGuid()
                         ==proposal.Id)
                    {
                        if(archive is not null)throw Denied();
                        archive=evidence;
                    }
                }
                catch(Exception ex) when(ex is System.Text.Json.JsonException or
                    System.IO.InvalidDataException or
                    System.Collections.Generic.KeyNotFoundException or
                    InvalidOperationException)
                {throw Denied();}
            }
            if(archive is null)throw Denied();
            var receivable=await db.OfficialInvoiceReceivables.AsNoTracking()
                .SingleOrDefaultAsync(x=>x.Kind==OfficialInvoiceKind.Monthly &&
                    x.IssuedInvoiceId==proposal.IssuedInvoiceId &&
                    x.ContractId==contractId && x.RoomId==sourceRoom &&
                    x.BuildingId==building)
                ??throw Denied();
            try
            {
                archive.VerifyStoredSnapshot();
                using var json=System.Text.Json.JsonDocument.Parse(archive.EvidenceJson);
                if(json.RootElement.GetProperty("AdjustmentId").GetGuid()!=proposal.Id ||
                   json.RootElement.GetProperty("DecisionId").GetGuid()!=decision.Id)
                    throw Denied();
                var id=Guid.NewGuid();
                await using var cmd=Command(db,"""
                    INSERT INTO "BuildingManagementRoomChangeMidMonthFinancialPostings"
                    ("Id","RequestId","SourceContractId","BuildingId","ArchiveId",
                     "OfficialReceivableId","SourcePaymentId","Kind",
                     "SignedAmountVnd","BankTransactionReference",
                     "RecordedByUserId","RecordedAtUtc","ApprovedAdjustmentId")
                    VALUES (@id,@request,@contract,@building,@archive,
                      @receivable,NULL,1,@amount,NULL,@actor,@at,@adjustment)
                    """);
                Add(cmd,"id",id);
                Add(cmd,"request",requestId);
                Add(cmd,"contract",contractId);
                Add(cmd,"building",building);
                Add(cmd,"archive",archive.Id);
                Add(cmd,"receivable",receivable.Id);
                Add(cmd,"actor",actor);
                Add(cmd,"adjustment",adjustmentId);
                var amount=cmd.CreateParameter();
                amount.ParameterName="amount";
                amount.Value=proposal.SignedDeltaAmount;
                cmd.Parameters.Add(amount);
                var at=cmd.CreateParameter();
                at.ParameterName="at";
                at.Value=_clock.GetUtcNow().UtcDateTime;
                cmd.Parameters.Add(at);
                await cmd.ExecuteNonQueryAsync();
                return new SliceBRoomChangeD03PostingRead(
                    id,requestId,proposal.Id,receivable.Id,
                    proposal.SignedDeltaAmount,
                    IsExternalBankPayment:false,CanExecuteTransfer:false);
            }
            catch(Exception ex) when(ex is System.Text.Json.JsonException or
                InvalidOperationException or System.IO.InvalidDataException or
                ArgumentException)
            {throw Denied();}
        });

    private static SliceBRoomChangeArchiveRead AsArchiveRead(
        RoomChangeEvidenceArchiveRecord a)
        => new(a.Id,a.RequestId,a.Kind,a.EvidenceId,
            a.SourceEvidenceSha256,a.RecordSha256,
            IsExternalSignatureAuthenticated:false,
            CanExecuteTransfer:false);

    public Task<SliceBRoomChangeCompletionRead> CompleteMidMonthRoomChangeAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeCompletionInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.TargetMonthlyRent <= 0m ||
                decimal.Truncate(input.TargetMonthlyRent) != input.TargetMonthlyRent ||
                input.SourceRoomDisposition is not
                    (RoomStatus.Available or RoomStatus.Reserved or RoomStatus.Maintenance) ||
                !ValidEvidence(input.DispositionEvidenceReference) ||
                !ValidEvidence(input.CompletionReference) ||
                db.Database.CurrentTransaction?.GetDbTransaction()
                    .IsolationLevel != System.Data.IsolationLevel.Serializable)
                throw Denied();

            await _identity.AcquireBootstrapAsync(actor);
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, request.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var utc = _clock.GetUtcNow().UtcDateTime;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            if (request.Status is not (RoomChangeRequestStatus.Pending or
                    RoomChangeRequestStatus.PolicyHold) ||
                request.RequestedDate > today ||
                request.RequestedDate >= DateOnly.MaxValue ||
                request.RequestedDate.Day == DateTime.DaysInMonth(
                    request.RequestedDate.Year,request.RequestedDate.Month) ||
                await db.RoomChangeCompletions.AsNoTracking().AnyAsync(x =>
                    x.RequestId == requestId || x.SourceContractId == contractId) ||
                await db.RoomChangeMidMonthCompletions.AsNoTracking().AnyAsync(x =>
                    x.RequestId == requestId || x.SourceContractId == contractId))
                throw Denied();

            var ids = new[] { sourceRoom, request.TargetRoomId }
                .Distinct().OrderBy(x => x).ToArray();
            if (ids.Length != 2) throw Denied();
            foreach (var id in ids)
            {
                var room = await LockRoomStatusAsync(db,id);
                if (room.Building != building ||
                    room.Status != (id == sourceRoom
                        ? RoomStatus.Occupied : RoomStatus.Available))
                    throw Denied();
            }

            // Do not bypass existing reservations, competing contracts,
            // source future commitments or concurrent move-out intentions.
            var reservations = await db.RoomReservations.AsNoTracking()
                .Where(x => !x.IsDeleted &&
                    (x.RoomId == sourceRoom || x.RoomId == request.TargetRoomId) &&
                    (x.Status == RoomReservationStatus.Reserved ||
                     x.Status == RoomReservationStatus.PendingPayment))
                .ToArrayAsync();
            if (reservations.Any(x => x.RoomId == request.TargetRoomId) ||
                reservations.Any(x => x.RoomId == sourceRoom &&
                    x.Status == RoomReservationStatus.PendingPayment) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                    (x.Status == ContractStatus.Signed ||
                     x.Status == ContractStatus.Active)) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.Id != contractId && x.RoomId == sourceRoom &&
                    !x.IsDeleted && x.Status == ContractStatus.Active) ||
                await db.MoveOutRequests.AsNoTracking().AnyAsync(x =>
                    x.ContractId == contractId && !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved)) ||
                await db.RoomChangeRequests.AsNoTracking().AnyAsync(x =>
                    x.Id != requestId && x.TargetRoomId == request.TargetRoomId &&
                    x.BuildingId == building &&
                    (x.Status == RoomChangeRequestStatus.Pending ||
                     x.Status == RoomChangeRequestStatus.PolicyHold)))
                throw Denied();

            var reserved = reservations.Count(x => x.RoomId == sourceRoom &&
                x.Status == RoomReservationStatus.Reserved);
            var signed = await db.Contracts.AsNoTracking().CountAsync(x =>
                x.Id != contractId && x.RoomId == sourceRoom &&
                !x.IsDeleted && x.Status == ContractStatus.Signed);
            var expectedDisposition = reserved + signed == 1
                ? RoomStatus.Reserved : RoomStatus.Available;
            if (reserved + signed > 1 ||
                (input.SourceRoomDisposition == RoomStatus.Reserved &&
                    expectedDisposition != RoomStatus.Reserved) ||
                (input.SourceRoomDisposition == RoomStatus.Available &&
                    expectedDisposition == RoomStatus.Reserved) ||
                (input.SourceRoomDisposition == RoomStatus.Maintenance &&
                    reserved + signed > 0))
                throw Denied();

            var source = await db.Contracts.SingleOrDefaultAsync(x =>
                x.Id == contractId && !x.IsDeleted);
            var oldRoom = await db.Rooms.SingleOrDefaultAsync(x =>
                x.Id == sourceRoom && !x.IsDeleted);
            var nextRoom = await db.Rooms.SingleOrDefaultAsync(x =>
                x.Id == request.TargetRoomId && !x.IsDeleted);
            if (source is null || oldRoom is null || nextRoom is null ||
                source.Status != ContractStatus.Active ||
                source.RoomId != sourceRoom ||
                source.MonthlyRent != input.TargetMonthlyRent ||
                source.DepositAmount != input.TargetMonthlyRent ||
                source.DepositAmount <= 0m)
                throw Denied();

            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == requestId)
                ?? throw Denied();
            await LockCurrentRosterAsync(db,contractId);
            var parties = await ReadCurrentRosterAsync(db,contractId);
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId)
                ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == policy.Id && x.Accepted)
                ?? throw Denied();
            var rule = await db.RoomChangeDayPricingRuleCandidates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId)
                ?? throw Denied();
            var handover = await db.RoomChangeHandoverEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId)
                ?? throw Denied();
            var archives = await db.RoomChangeEvidenceArchives.AsNoTracking()
                .Where(x => x.RequestId == requestId).ToArrayAsync();
            var pricingProof = archives.SingleOrDefault(x =>
                x.Kind == RoomChangeEvidenceArchiveKind.SignedExactPricing)
                ?? throw Denied();
            var physicalProof = archives.SingleOrDefault(x =>
                x.Kind == RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover)
                ?? throw Denied();
            if (archives.Any(x =>
                x.Kind == RoomChangeEvidenceArchiveKind.ReviewedTransferCash))
                throw Denied();

            var originals = await LoadHandoverMetersAsync(
                db,sourceRoom,request.RequestedDate);
            try
            {
                review.Verify(policy,request,roster);
                rule.Verify(request,source,roster,policy,review);
                handover.Verify(request,originals.E,originals.Er,
                    originals.W,originals.Wr);
                pricingProof.VerifyStoredSnapshot();
                physicalProof.VerifyStoredSnapshot();
                if (!roster.MatchesCurrentRoster(requestId,contractId,
                    sourceRoom,building,parties) ||
                    policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
                    policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
                    policy.DepositTreatment != RoomChangeDepositTreatment.CarryAllWithNoDifference ||
                    policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
                    rule.SourceChargeableDays <= 0 ||
                    rule.TargetChargeableDays <= 0 ||
                    rule.TargetMonthlyRentVnd != input.TargetMonthlyRent ||
                    rule.RequestId != requestId ||
                    pricingProof.ParentEvidenceId != rule.Id ||
                    pricingProof.ParentEvidenceSha256 != rule.EvidenceSha256 ||
                    physicalProof.ParentEvidenceId != handover.Id ||
                    physicalProof.ParentEvidenceSha256 != handover.EvidenceSha256)
                    throw Denied();
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException or OverflowException)
            {
                throw Denied();
            }

            var money = await VerifyMidMonthSourceRentAsync(
                db,request,sourceRoom,building,rule,
                handover.ElectricityReadingId,handover.WaterReadingId,utc);
            await VerifyTransferDepositAsync(db,source);

            // Every write belongs to the same EF UoW and the SQL deferred
            // checks must all pass at COMMIT; no provisional success outside
            // this atomic transaction can be exposed.
            var newId = Guid.NewGuid();
            var firstDay = new DateOnly(request.RequestedDate.Year,
                request.RequestedDate.Month,rule.SourceChargeableDays+1);
            var target = new Contract(newId,"TRF-"+newId.ToString("N"),
                request.TargetRoomId,
                firstDay.ToDateTime(TimeOnly.MinValue),
                null,input.TargetMonthlyRent,
                "Signed mid-month transfer "+request.Id.ToString("D"),
                renewedFromContractId:source.Id);
            target.Activate();
            var copiedTenants = parties.Select(x => new ContractTenant(
                Guid.NewGuid(),newId,x.TenantId,x.Role)).ToArray();
            var carry = new DepositSettlement(Guid.NewGuid(),
                DepositSettlementType.RoomChangeCarryForward,
                source.DepositAmount,
                sourceContractId:source.Id,targetContractId:target.Id,
                notes:"Verified full original deposit mid-month carry "+
                    request.Id.ToString("D"));
            carry.Complete(utc);
            source.End(utc,ContractEndReason.RoomChanged);
            oldRoom.ChangeStatus(input.SourceRoomDisposition);
            nextRoom.ChangeStatus(RoomStatus.Occupied);

            RoomChangeMidMonthCompletionRecord completion;
            try
            {
                completion=RoomChangeMidMonthCompletionRecord.Complete(
                    Guid.NewGuid(),request,source,target,roster,policy,
                    review,handover,rule,pricingProof,physicalProof,
                    money.Issued,money.Posted,money.OriginalRent,
                    money.Delta,money.Paid,carry,input.SourceRoomDisposition,
                    input.DispositionEvidenceReference,input.CompletionReference,
                    actor,utc);
                completion.VerifyFingerprint();
                request.Complete(actor,utc,input.CompletionReference!);
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException or OverflowException)
            {
                throw Denied();
            }

            db.Contracts.Add(target);
            db.ContractTenants.AddRange(copiedTenants);
            db.DepositSettlements.Add(carry);
            db.RoomChangeMidMonthCompletions.Add(completion);
            return new SliceBRoomChangeCompletionRead(
                completion.Id,request.Id,source.Id,target.Id,
                oldRoom.Id,nextRoom.Id,completion.CarriedDepositVnd,
                completion.SourceRoomDisposition,completion.EvidenceSha256,true);
        });

    private static async Task<(
        MonthlyIssuedInvoiceRecord Issued,
        OfficialInvoiceReceivablePostingRecord Posted,
        decimal OriginalRent,decimal Delta,decimal Paid)>
        VerifyMidMonthSourceRentAsync(
            BuildingManagementDbContext db,RoomChangeRequest request,
            Guid room,Guid building,RoomChangeDayPricingRuleCandidate rule,
            Guid electricityReading,Guid waterReading,DateTime utc)
    {
        await using (var locked=Command(db,"""
            SELECT "Id" FROM "BuildingManagementOfficialInvoiceReceivables"
             WHERE "ContractId"=@contract ORDER BY "Id" FOR UPDATE
            """))
        {
            Add(locked,"contract",request.SourceContractId);
            await using var reader=await locked.ExecuteReaderAsync();
            while(await reader.ReadAsync()) { }
        }

        var monthly=await db.MonthlyIssuedInvoices.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        var finals=await db.IssuedInvoiceLedger.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        var posted=await db.OfficialInvoiceReceivables.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        if (monthly.Length==0 || posted.Length!=monthly.Length+finals.Length ||
            posted.Select(x=>x.IssuedInvoiceId).Distinct().Count()!=posted.Length)
            throw Denied();
        var transferMonth=monthly.SingleOrDefault(x=>
            x.BillingYear==request.RequestedDate.Year &&
            x.BillingMonth==request.RequestedDate.Month)
            ?? throw Denied();
        var frozen=await db.MonthlyInvoiceCandidates.AsNoTracking()
            .SingleOrDefaultAsync(x=>x.Id==transferMonth.CandidateId)
            ?? throw Denied();
        var review=await db.MonthlyInvoiceReviews.AsNoTracking()
            .SingleOrDefaultAsync(x=>x.Id==transferMonth.ReviewId)
            ?? throw Denied();
        try
        {
            transferMonth.VerifyAgainst(frozen,review);
            var evidence=frozen.VerifyStoredEvidence();
            if (frozen.ContractId!=request.SourceContractId ||
                frozen.RoomId!=room || frozen.BuildingId!=building ||
                evidence.UtilityLines.Count!=2 ||
                !evidence.UtilityLines.Any(x=>
                    x.UtilityType==BuildingManagement.Utilities.UtilityType.Electricity &&
                    x.Sources.Any(s=>s.SourceId==electricityReading)) ||
                !evidence.UtilityLines.Any(x=>
                    x.UtilityType==BuildingManagement.Utilities.UtilityType.Water &&
                    x.Sources.Any(s=>s.SourceId==waterReading)))
                throw Denied();
        }
        catch(Exception ex) when (ex is System.IO.InvalidDataException or
            InvalidOperationException or ArgumentException) {throw Denied();}

        var current=posted.SingleOrDefault(x=>x.Kind==OfficialInvoiceKind.Monthly &&
            x.IssuedInvoiceId==transferMonth.Id) ?? throw Denied();
        var delta=checked(rule.ProposedSourceRentVnd-frozen.RentAmount);
        decimal actual=0m; int count=0;
        await using(var cmd=Command(db,"""
            SELECT "SignedAmountVnd" FROM
              "BuildingManagementRoomChangeMidMonthFinancialPostings"
             WHERE "RequestId"=@request AND "SourceContractId"=@contract
               AND "BuildingId"=@building AND "Kind"=1
               AND "OfficialReceivableId"=@receivable
             ORDER BY "Id" FOR SHARE
            """))
        {
            Add(cmd,"request",request.Id);
            Add(cmd,"contract",request.SourceContractId);
            Add(cmd,"building",building);
            Add(cmd,"receivable",current.Id);
            await using var reader=await cmd.ExecuteReaderAsync();
            while(await reader.ReadAsync())
            {
                actual=checked(actual+reader.GetDecimal(0));
                count++;
            }
        }
        if (actual!=delta || (delta==0 && count>0) ||
            (delta!=0 && count==0) ||
            await db.RoomChangeEvidenceArchives.AsNoTracking().AnyAsync(x=>
                x.RequestId==request.Id &&
                x.Kind==RoomChangeEvidenceArchiveKind.ReviewedTransferCash))
            throw Denied();

        var corrections=await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        var proposals=await db.MonthlyInvoiceAdjustments.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        if (corrections.Any(x=>!x.Accepted ||
                x.IssuedInvoiceId!=transferMonth.Id) ||
            proposals.Length!=corrections.Length || corrections.Length!=count ||
            (corrections.Length==0 && delta!=0))
            throw Denied();

        var reviews=await db.OfficialInvoiceReceiptReviews.AsNoTracking()
            .Where(x=>x.ContractId==request.SourceContractId).ToArrayAsync();
        if (reviews.Any(x=>!x.Accepted ||
            !posted.Any(p=>p.Id==x.ReceivablePostingId) ||
            x.ReviewedAtUtc>utc))
            throw Denied();
        foreach(var p in posted)
        {
            if(p.RoomId!=room || p.BuildingId!=building ||
                p.OriginalAmount<=0m) throw Denied();
            var accepted=reviews.Where(x=>x.ReceivablePostingId==p.Id).ToArray();
            decimal amount=0;
            if(p.Kind==OfficialInvoiceKind.Monthly)
            {
                var issue=monthly.SingleOrDefault(x=>x.Id==p.IssuedInvoiceId)
                    ?? throw Denied();
                var allocations=await db.MonthlyInvoicePaymentAllocations
                    .AsNoTracking().Where(x=>x.IssuedInvoiceId==issue.Id).ToArrayAsync();
                if(allocations.Length!=accepted.Length)throw Denied();
                foreach(var allocation in allocations)
                {
                    var original=await db.Payments.IgnoreQueryFilters().AsNoTracking()
                        .SingleOrDefaultAsync(x=>x.Id==allocation.PaymentId)
                        ?? throw Denied();
                    var receipt=OfficialInvoiceReceiptEvidence.FromMonthly(
                        allocation,original);
                    var acceptedReview=accepted.SingleOrDefault(x=>
                        x.ReceiptAllocationId==allocation.Id)??throw Denied();
                    acceptedReview.Verify(p,receipt);
                    amount=checked(amount+acceptedReview.Amount);
                }
            }
            else if(p.Kind==OfficialInvoiceKind.Final)
            {
                var issue=finals.SingleOrDefault(x=>x.Id==p.IssuedInvoiceId)
                    ?? throw Denied();
                var allocations=await db.InvoicePaymentAllocationJournal
                    .AsNoTracking().Where(x=>x.IssuedInvoiceId==issue.Id).ToArrayAsync();
                if(allocations.Length!=accepted.Length)throw Denied();
                foreach(var allocation in allocations)
                {
                    var original=await db.Payments.IgnoreQueryFilters().AsNoTracking()
                        .SingleOrDefaultAsync(x=>x.Id==allocation.PaymentId)
                        ?? throw Denied();
                    var receipt=OfficialInvoiceReceiptEvidence.FromFinal(
                        allocation,original);
                    var acceptedReview=accepted.SingleOrDefault(x=>
                        x.ReceiptAllocationId==allocation.Id)??throw Denied();
                    acceptedReview.Verify(p,receipt);
                    amount=checked(amount+acceptedReview.Amount);
                }
            }
            else throw Denied();
            var expected=checked(p.OriginalAmount+(p.Id==current.Id?delta:0m));
            if(expected<=0m || amount!=expected)throw Denied();
        }
        var paid=checked(current.OriginalAmount+delta);
        return (transferMonth,current,frozen.RentAmount,delta,paid);
    }

    public Task<SliceBRoomChangeCompletionRead> CompleteRoomChangeAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeCompletionInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.TargetMonthlyRent <= 0m ||
                decimal.Truncate(input.TargetMonthlyRent) != input.TargetMonthlyRent ||
                input.SourceRoomDisposition is not
                    (RoomStatus.Available or RoomStatus.Reserved or RoomStatus.Maintenance) ||
                !ValidEvidence(input.DispositionEvidenceReference) ||
                !ValidEvidence(input.CompletionReference) ||
                db.Database.CurrentTransaction?.GetDbTransaction()
                    .IsolationLevel != System.Data.IsolationLevel.Serializable)
                throw Denied();
            await _identity.AcquireBootstrapAsync(actor);
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, request.TargetRoomId,
                BuildingBusinessCapability.ManageContract);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                _clock.GetUtcNow().UtcDateTime,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            if (request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
                request.RequestedDate == default ||
                request.RequestedDate >= DateOnly.MaxValue ||
                request.RequestedDate > today ||
                request.RequestedDate.Day != DateTime.DaysInMonth(
                    request.RequestedDate.Year, request.RequestedDate.Month) ||
                await db.RoomChangeCompletions.AsNoTracking()
                    .AnyAsync(x => x.RequestId == requestId ||
                        x.SourceContractId == contractId))
                throw Denied();
            var roomIds = new[] { sourceRoom, request.TargetRoomId }
                .Distinct().OrderBy(x => x).ToArray();
            if (roomIds.Length != 2) throw Denied();
            foreach (var id in roomIds)
            {
                var locked = await LockRoomStatusAsync(db, id);
                if (locked.Building != building ||
                    locked.Status != (id == sourceRoom
                        ? RoomStatus.Occupied : RoomStatus.Available))
                    throw Denied();
            }
            var existingReservations = await db.RoomReservations.AsNoTracking()
                .Where(x => !x.IsDeleted &&
                    (x.RoomId == sourceRoom ||
                     x.RoomId == request.TargetRoomId) &&
                    (x.Status == RoomReservationStatus.Reserved ||
                     x.Status == RoomReservationStatus.PendingPayment))
                .ToArrayAsync();
            if (existingReservations.Any(x => x.RoomId == request.TargetRoomId))
                throw Denied();
            // Confirmed source commitments include BOTH Reserved bookings
            // and independently signed future contracts. Previous code
            // observed reservations only; database now proves both at COMMIT.
            var sourceReservedCount = existingReservations.Count(x =>
                x.RoomId == sourceRoom && x.Status == RoomReservationStatus.Reserved);
            var sourcePending = existingReservations.Any(x =>
                x.RoomId == sourceRoom && x.Status == RoomReservationStatus.PendingPayment);
            var sourceSignedCount = await db.Contracts.AsNoTracking().CountAsync(x =>
                x.Id != contractId && x.RoomId == sourceRoom &&
                !x.IsDeleted && x.Status == ContractStatus.Signed);
            var sourceOtherActive = await db.Contracts.AsNoTracking().AnyAsync(x =>
                x.Id != contractId && x.RoomId == sourceRoom &&
                !x.IsDeleted && x.Status == ContractStatus.Active);
            var sourceFutureCommitments = sourceReservedCount + sourceSignedCount;
            var sourceCommitted = sourceFutureCommitments == 1;
            if (sourcePending || sourceOtherActive || sourceFutureCommitments > 1 ||
                (sourceCommitted && input.SourceRoomDisposition != RoomStatus.Reserved) ||
                (!sourceCommitted && input.SourceRoomDisposition == RoomStatus.Reserved) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.RoomId == request.TargetRoomId && !x.IsDeleted &&
                    (x.Status == ContractStatus.Active ||
                     x.Status == ContractStatus.Signed)) ||
                await db.RoomChangeRequests.AsNoTracking().AnyAsync(x =>
                    x.Id != requestId && x.TargetRoomId == request.TargetRoomId &&
                    x.BuildingId == building &&
                    (x.Status == RoomChangeRequestStatus.Pending ||
                     x.Status == RoomChangeRequestStatus.PolicyHold)) ||
                await db.MoveOutRequests.AsNoTracking().AnyAsync(x =>
                    x.ContractId == contractId && !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved)))
                throw Denied();

            var source = await db.Contracts.SingleOrDefaultAsync(x =>
                x.Id == contractId && !x.IsDeleted);
            var oldRoom = await db.Rooms.SingleOrDefaultAsync(x =>
                x.Id == sourceRoom && !x.IsDeleted);
            var nextRoom = await db.Rooms.SingleOrDefaultAsync(x =>
                x.Id == request.TargetRoomId && !x.IsDeleted);
            if (source is null || oldRoom is null || nextRoom is null ||
                source.Status != ContractStatus.Active ||
                source.RoomId != sourceRoom ||
                source.MonthlyRent != input.TargetMonthlyRent ||
                source.DepositAmount != input.TargetMonthlyRent ||
                source.DepositAmount <= 0m)
                throw Denied();

            var roster = await db.RoomChangeRosterSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RoomChangeRequestId == requestId)
                ?? throw Denied();
            await LockCurrentRosterAsync(db, contractId);
            var parties = await ReadCurrentRosterAsync(db, contractId);
            var policy = await db.RoomChangeBusinessPolicies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId)
                ?? throw Denied();
            var review = await db.RoomChangePolicyReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PolicyId == policy.Id && x.Accepted)
                ?? throw Denied();
            var handover = await db.RoomChangeHandoverEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId)
                ?? throw Denied();
            var originals = await LoadHandoverMetersAsync(
                db, sourceRoom, request.RequestedDate);
            try
            {
                review.Verify(policy, request, roster);
                handover.Verify(request, originals.E, originals.Er,
                    originals.W, originals.Wr);
                if (!roster.MatchesCurrentRoster(requestId,
                    contractId, sourceRoom, building, parties) ||
                    policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
                    policy.PricingTreatment != RoomChangePricingTreatment.FullOldMonthNewNextMonth ||
                    policy.DepositTreatment != RoomChangeDepositTreatment.CarryAllWithNoDifference ||
                    policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants)
                    throw Denied();
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException) { throw Denied(); }

            await VerifyTransferReceivablesAsync(db, contractId,
                sourceRoom, building, request.RequestedDate,
                handover.ElectricityReadingId, handover.WaterReadingId);
            await VerifyTransferDepositAsync(db, source);
            var utc = _clock.GetUtcNow().UtcDateTime;

            // H6: server-owned reduced checklist after the LIVE source
            // E/W, complete tenant roster, official receivables and held
            // deposit have all been verified inside this SERIALIZABLE UoW.
            // No request/body-supplied Boolean can attest these prerequisites.
            try
            {
                RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                    new RoomChangeReducedHandoverCompletionFacts(
                        request, roster, policy, review, handover,
                        input.SourceRoomDisposition, sourceCommitted,
                        input.DispositionEvidenceReference,
                        input.CompletionReference, actor, utc,
                        ServerVerifiedOriginalElectricityWaterReadings: true,
                        ServerVerifiedExactFullTenantRoster: true,
                        ServerVerifiedAllOfficialSourceReceivablesSettled: true,
                        ServerVerifiedActualCashBackedHeldDeposit: true));
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException)
            {
                throw Denied();
            }

            var newId = Guid.NewGuid();
            var target = new Contract(newId, "TRF-" + newId.ToString("N"),
                request.TargetRoomId,
                request.RequestedDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
                null, input.TargetMonthlyRent,
                "Room change " + request.Id.ToString("D"),
                renewedFromContractId: source.Id);
            target.Activate();
            var freshParties = parties.Select(x => new ContractTenant(
                Guid.NewGuid(), newId, x.TenantId, x.Role)).ToArray();
            var settlement = new DepositSettlement(Guid.NewGuid(),
                DepositSettlementType.RoomChangeCarryForward,
                source.DepositAmount, sourceContractId: source.Id,
                targetContractId: target.Id,
                notes: "Signed same-deposit transfer " + request.Id.ToString("D"));
            settlement.Complete(utc);
            source.End(utc, ContractEndReason.RoomChanged);
            oldRoom.ChangeStatus(input.SourceRoomDisposition);
            nextRoom.ChangeStatus(RoomStatus.Occupied);
            var completion = RoomChangeCompletionRecord.Complete(
                Guid.NewGuid(), request, source, target, policy, review,
                roster, handover, settlement, input.SourceRoomDisposition,
                input.DispositionEvidenceReference,
                input.CompletionReference, actor, utc);
            request.Complete(actor, utc, input.CompletionReference!);
            db.Contracts.Add(target);
            db.ContractTenants.AddRange(freshParties);
            db.DepositSettlements.Add(settlement);
            db.RoomChangeCompletions.Add(completion);
            return new SliceBRoomChangeCompletionRead(completion.Id,
                request.Id, source.Id, target.Id, oldRoom.Id, nextRoom.Id,
                completion.CarriedDepositAmount, completion.SourceRoomDisposition,
                completion.EvidenceSha256, true);
        });

    private static async Task VerifyTransferReceivablesAsync(
        BuildingManagementDbContext db, Guid contractId, Guid roomId,
        Guid buildingId, DateOnly transferDate,
        Guid electricityReading, Guid waterReading)
    {
        await using (var lockPosting = Command(db, """
            SELECT "Id" FROM "BuildingManagementOfficialInvoiceReceivables"
             WHERE "ContractId"=@contract ORDER BY "Id" FOR UPDATE
            """))
        {
            Add(lockPosting, "contract", contractId);
            await using var reader = await lockPosting.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        var monthly = await db.MonthlyIssuedInvoices.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        var finalInvoices = await db.IssuedInvoiceLedger.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        var postings = await db.OfficialInvoiceReceivables.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        if (monthly.Length == 0 ||
            postings.Length != monthly.Length + finalInvoices.Length ||
            await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                .AnyAsync(x => x.ContractId == contractId) ||
            await db.MonthlyInvoiceAdjustments.AsNoTracking()
                .AnyAsync(x => x.ContractId == contractId))
            throw Denied();

        var endMonth = monthly.Where(x =>
            x.BillingYear == transferDate.Year &&
            x.BillingMonth == transferDate.Month).ToArray();
        if (endMonth.Length != 1 || endMonth[0].RoomId != roomId)
            throw Denied();
        var last = endMonth[0];
        var candidate = await db.MonthlyInvoiceCandidates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == last.CandidateId)
            ?? throw Denied();
        var signedReview = await db.MonthlyInvoiceReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == last.ReviewId)
            ?? throw Denied();
        try
        {
            last.VerifyAgainst(candidate, signedReview);
            var frozen = candidate.VerifyStoredEvidence();
            if (candidate.ContractId != contractId ||
                candidate.RoomId != roomId ||
                candidate.BuildingId != buildingId ||
                frozen.UtilityLines.Count != 2 ||
                !frozen.UtilityLines.Any(line =>
                    line.UtilityType == BuildingManagement.Utilities.UtilityType.Electricity &&
                    line.Sources.Any(x => x.SourceId == electricityReading)) ||
                !frozen.UtilityLines.Any(line =>
                    line.UtilityType == BuildingManagement.Utilities.UtilityType.Water &&
                    line.Sources.Any(x => x.SourceId == waterReading)))
                throw Denied();
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or
            InvalidOperationException or ArgumentException) { throw Denied(); }

        var receiptReviews = await db.OfficialInvoiceReceiptReviews.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        if (receiptReviews.Any(r => !r.Accepted ||
            !postings.Any(p => p.Id == r.ReceivablePostingId)) ||
            postings.Select(x => x.IssuedInvoiceId).Distinct().Count() != postings.Length)
            throw Denied();
        foreach (var posting in postings)
        {
            if (posting.BuildingId != buildingId || posting.RoomId != roomId ||
                posting.OriginalAmount <= 0m)
                throw Denied();
            if (posting.Kind == OfficialInvoiceKind.Monthly)
            {
                var issue = monthly.SingleOrDefault(x => x.Id == posting.IssuedInvoiceId);
                if (issue is null) throw Denied();
                var allocations = await db.MonthlyInvoicePaymentAllocations.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == issue.Id).ToArrayAsync();
                var confirmations = receiptReviews.Where(x =>
                    x.ReceivablePostingId == posting.Id).ToArray();
                if (allocations.Length != confirmations.Length ||
                    allocations.Any(a => !confirmations.Any(r =>
                        r.ReceiptAllocationId == a.Id && r.Amount == a.Amount)))
                    throw Denied();
            }
            else if (posting.Kind == OfficialInvoiceKind.Final)
            {
                var issue = finalInvoices.SingleOrDefault(x =>
                    x.Id == posting.IssuedInvoiceId);
                if (issue is null) throw Denied();
                var allocations = await db.InvoicePaymentAllocationJournal.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == issue.Id).ToArrayAsync();
                var confirmations = receiptReviews.Where(x =>
                    x.ReceivablePostingId == posting.Id).ToArray();
                if (allocations.Length != confirmations.Length ||
                    allocations.Any(a => !confirmations.Any(r =>
                        r.ReceiptAllocationId == a.Id && r.Amount == a.Amount)))
                    throw Denied();
            }
            else throw Denied();
            if (receiptReviews.Where(x => x.ReceivablePostingId == posting.Id)
                    .Sum(x => x.Amount) != posting.OriginalAmount)
                throw Denied();
        }
    }

    private static async Task VerifyTransferDepositAsync(
        BuildingManagementDbContext db, Contract source)
    {
        await using (var locked = Command(db, """
            SELECT "Id" FROM "BuildingManagementDepositSettlements"
             WHERE "SourceContractId"=@contract OR "TargetContractId"=@contract
             ORDER BY "Id" FOR UPDATE
            """))
        {
            Add(locked, "contract", source.Id);
            await using var reader = await locked.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        if (await db.DepositSettlementDecisions.AsNoTracking()
                .AnyAsync(x => x.ContractId == source.Id) ||
            await db.DepositCloseoutApprovals.AsNoTracking()
                .AnyAsync(x => x.ContractId == source.Id))
            throw Denied();
        var related = await db.DepositSettlements.AsNoTracking()
            .Where(x => x.SourceContractId == source.Id ||
                x.TargetContractId == source.Id).ToArrayAsync();
        if (related.Any(x => x.SourceContractId == source.Id ||
                x.Status != DepositSettlementStatus.Completed ||
                x.Type != DepositSettlementType.AllocateToContractDeposit ||
                x.TargetContractId != source.Id ||
                x.PaymentId is null || x.Amount <= 0m) ||
            related.Length == 0 ||
            related.Sum(x => x.Amount) != source.DepositAmount ||
            related.Select(x => x.PaymentId).Distinct().Count() != related.Length)
            throw Denied();

        var ids = related.Select(x => x.PaymentId!.Value).ToArray();
        await using (var locked = Command(db, """
            SELECT "Id" FROM "BuildingManagementPayments"
             WHERE "Id" = ANY(@payment_ids) ORDER BY "Id" FOR UPDATE
            """))
        {
            var p = locked.CreateParameter();
            p.ParameterName = "payment_ids";
            p.Value = ids;
            locked.Parameters.Add(p);
            await using var reader = await locked.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => ids.Contains(x.Id)).ToArrayAsync();
        var uses = await db.DepositSettlements.AsNoTracking()
            .Where(x => x.PaymentId.HasValue &&
                ids.Contains(x.PaymentId.Value)).ToArrayAsync();
        if (payments.Length != related.Length ||
            uses.Length != related.Length ||
            payments.Any(payment =>
            {
                var allocation = related.SingleOrDefault(x =>
                    x.PaymentId == payment.Id);
                return allocation is null || payment.IsDeleted ||
                    payment.Status != PaymentStatus.Completed ||
                    payment.PaidAt is null || payment.Amount != allocation.Amount ||
                    (payment.Purpose == PaymentPurpose.ContractDeposit
                        ? payment.ContractId != source.Id
                        : payment.Purpose != PaymentPurpose.ReservationDeposit ||
                          payment.RoomReservationId !=
                              allocation.SourceRoomReservationId);
            }) ||
            uses.Any(x => x.TargetContractId != source.Id ||
                x.Type != DepositSettlementType.AllocateToContractDeposit ||
                x.Status != DepositSettlementStatus.Completed))
            throw Denied();
        // Direct contract-deposit receipts must all be journaled into the
        // held contract ledger, not counted twice or silently ignored.
        var direct = await db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.ContractId == source.Id &&
                x.Purpose == PaymentPurpose.ContractDeposit &&
                x.Status == PaymentStatus.Completed).ToArrayAsync();
        if (direct.Any(x => !ids.Contains(x.Id)))
            throw Denied();
    }

    private static bool ValidEvidence(string? proof) =>
        !string.IsNullOrWhiteSpace(proof) &&
        proof.Trim().Length is >= 12 and <= 500;

    public Task<SliceBRoomChangeRequestRead> HoldForPolicyAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input)
        => ReviewAsync(contractId, requestId, input,
            (r, actor, now, note) => r.HoldForPolicy(actor, now, note));

    public Task<SliceBRoomChangeRequestRead> RejectAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input)
        => ReviewAsync(contractId, requestId, input,
            (r, actor, now, note) => r.Reject(actor, now, note));

    public Task<SliceBRoomChangeRequestRead> CancelAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            try { request.Cancel(actor, _clock.GetUtcNow().UtcDateTime); }
            catch (InvalidOperationException) { throw Denied(); }
            return AsRead(request);
        });

    private Task<SliceBRoomChangeRequestRead> ReviewAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input,
        Action<RoomChangeRequest, Guid, DateTime, string?> action)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null) throw Denied();
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            try { action(request, actor, _clock.GetUtcNow().UtcDateTime, input.ReviewNotes); }
            catch (InvalidOperationException) { throw Denied(); }
            return AsRead(request);
        });

    private async Task<T> ExecuteScopedAsync<T>(
        Guid contractId,
        Func<BuildingManagementDbContext, Guid, Guid, Guid, Task<T>> work,
        bool permitEndedContract = false)
    {
        if (contractId == Guid.Empty ||
            !_current.IsAuthenticated || _current.Id is not Guid actor ||
            actor == Guid.Empty)
            throw Denied();
        var scope = await _resources.ReadAsync(
            BuildingBusinessResourceKind.Contract, contractId);
        if (scope is null || scope.ResourceId != contractId ||
            scope.BuildingId == Guid.Empty)
            throw Denied();
        var building = scope.BuildingId;
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);

        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                var sourceRoom = await LockContractRoomAsync(db, contractId);
                var sourceBuilding = await LockRoomBuildingAsync(db, sourceRoom);
                if (sourceBuilding != building)
                    throw Denied();
                var contract = await db.Contracts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted);
                if (contract is null || contract.RoomId != sourceRoom ||
                    (contract.Status != ContractStatus.Active &&
                     !(permitEndedContract &&
                       contract.Status == ContractStatus.Ended)))
                    throw Denied();
                return await work(db, actor, building, sourceRoom);
            }, () => _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract, contractId,
                BuildingBusinessCapability.ManageContract));
    }

    private static async Task<RoomChangeRequest> LockRequestAsync(
        BuildingManagementDbContext db, Guid requestId,
        Guid contractId, Guid sourceRoomId, Guid buildingId)
    {
        if (requestId == Guid.Empty) throw Denied();
        await using (var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementRoomChangeRequests"
             WHERE "Id" = @id AND "SourceContractId" = @contract
               AND "SourceRoomId" = @room AND "BuildingId" = @building
             FOR UPDATE
            """))
        {
            Add(cmd, "id", requestId);
            Add(cmd, "contract", contractId);
            Add(cmd, "room", sourceRoomId);
            Add(cmd, "building", buildingId);
            if (await cmd.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }
        return await db.RoomChangeRequests
            .SingleOrDefaultAsync(x => x.Id == requestId &&
                x.SourceContractId == contractId &&
                x.SourceRoomId == sourceRoomId &&
                x.BuildingId == buildingId) ?? throw Denied();
    }

    private static async Task<Guid> LockContractRoomAsync(
        BuildingManagementDbContext db, Guid id)
    {
        await using var cmd = Command(db, """
            SELECT "RoomId" FROM "BuildingManagementContracts"
             WHERE "Id" = @id AND "IsDeleted" = FALSE FOR UPDATE
            """);
        Add(cmd, "id", id);
        return await cmd.ExecuteScalarAsync() is Guid value
            ? value : throw Denied();
    }

    private static async Task<Guid> LockRoomBuildingAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId" FROM "BuildingManagementRooms" r
             JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
             JOIN "Buildings" b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR SHARE OF f, b
            """);
        Add(cmd, "id", roomId);
        return await cmd.ExecuteScalarAsync() is Guid value
            ? value : throw Denied();
    }

    private static async Task<(Guid Building, RoomStatus Status)> LockRoomStatusAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId", r."Status"
              FROM "BuildingManagementRooms" r
              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
              JOIN "Buildings" b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR UPDATE OF r
            """);
        Add(cmd, "id", roomId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw Denied();
        return (reader.GetGuid(0), (RoomStatus)reader.GetInt32(1));
    }

    private static DbCommand Command(BuildingManagementDbContext db, string sql)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(DbCommand command, string name, Guid value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static SliceBRoomChangeRequestRead AsRead(RoomChangeRequest r)
        => new(r.Id, r.SourceContractId, r.SourceRoomId, r.TargetRoomId,
            r.BuildingId, r.RequestedDate, r.Reason, r.Status,
            r.RequestedByUserId, r.CreatedAtUtc,
            r.ReviewedByUserId, r.ReviewedAtUtc, r.ReviewNotes);

    private static AbpAuthorizationException Denied()
        => new("Room change requires isolated staging Contracts.Manage and current building-scoped membership.");
}

public sealed record SliceBRoomChangeSubmitInput(
    Guid TargetRoomId, DateOnly RequestedDate, string? Reason);

public sealed record SliceBRoomChangeReviewInput(string? ReviewNotes);

public sealed record SliceBRoomChangeRequestRead(
    Guid Id, Guid SourceContractId, Guid SourceRoomId, Guid TargetRoomId,
    Guid BuildingId, DateOnly RequestedDate, string Reason,
    RoomChangeRequestStatus Status, Guid RequestedByUserId, DateTime CreatedAtUtc,
    Guid? ReviewedByUserId, DateTime? ReviewedAtUtc, string? ReviewNotes);

/// <summary>Always transfer-blocked: OI-07 and signed financial evidence are not implemented.</summary>
public sealed record SliceBRoomChangeLiveReadiness(
    Guid RequestId, bool CanContinuePolicyReview, bool TransferExecutionBlocked,
    string[] Blockers);

/// <summary>
/// Read-only historical party roster; source snapshot only. IsTransferConsentVerified
/// stays FALSE until a separately approved transfer policy and signoffs exist.
/// </summary>
public sealed record SliceBRoomChangeRosterRead(
    Guid Id, Guid RoomChangeRequestId, Guid SourceContractId,
    Guid SourceRoomId, Guid BuildingId, Guid CapturedByUserId,
    DateTime CapturedAtUtc, int RosterCount, string RosterSha256,
    RoomChangeRosterMember[] Tenants, bool IsTransferConsentVerified);

/// <summary>Explicit documentary options only; not signed transfer authorization.</summary>
public sealed record SliceBRoomChangeBusinessPolicyInput(
    RoomChangeContractTreatment ContractTreatment,
    RoomChangePricingTreatment PricingTreatment,
    RoomChangeDepositTreatment DepositTreatment,
    RoomChangeTenantTreatment TenantTreatment,
    string? ContractPolicyReference, string? PricingPolicyReference,
    string? DepositPolicyReference, string? TenantConsentReference);

public sealed record SliceBRoomChangeBusinessPolicyRead(
    Guid Id, Guid RequestId, Guid SourceContractId,
    Guid BuildingId, Guid RosterSnapshotId, string RosterSha256,
    RoomChangeContractTreatment ContractTreatment,
    RoomChangePricingTreatment PricingTreatment,
    RoomChangeDepositTreatment DepositTreatment,
    RoomChangeTenantTreatment TenantTreatment,
    string ContractPolicyReference, string PricingPolicyReference,
    string DepositPolicyReference, string TenantConsentReference,
    Guid ProposedByUserId, DateTime ProposedAtUtc, string EvidenceSha256,
    bool IsApproved, bool IsExecuted);

/// <summary>Independent explicit root review, not transfer authorization.</summary>
public sealed record SliceBRoomChangeBusinessPolicyReviewInput(
    bool Accepted, string? SignedReviewReference, string? Reason);

public sealed record SliceBRoomChangeBusinessPolicyReviewRead(
    Guid Id, Guid PolicyId, Guid RequestId, Guid BuildingId,
    string ProposalSha256, Guid ReviewedByUserId, DateTime ReviewedAtUtc,
    bool Accepted, string SignedReviewReference, string Reason,
    bool IsTransferExecuted);

/// <summary>Explicit choices only; no default charge-day or rounding policy.</summary>
public sealed record SliceBRoomChangeDayPricingRuleInput(
    decimal TargetMonthlyRentVnd,
    RoomChangeTransferDayChargeOwner TransferDayOwner,
    RoomChangeRentRoundingRule Rounding);

/// <summary>Reviewed policy-linked candidate only; never an issued charge.</summary>
public sealed record SliceBRoomChangeDayPricingRuleRead(
    Guid Id, Guid RequestId, Guid SourceContractId,
    Guid SourceRoomId, Guid TargetRoomId, Guid BuildingId,
    DateOnly TransferDate, RoomChangeTransferDayChargeOwner TransferDayOwner,
    RoomChangeRentRoundingRule Rounding, int CalendarDays,
    int SourceChargeableDays, int TargetChargeableDays,
    decimal SourceMonthlyRentVnd, decimal TargetMonthlyRentVnd,
    decimal ProposedSourceRentVnd, decimal ProposedTargetRentVnd,
    string SignedPricingRuleReference, Guid PreparedByUserId,
    DateTime PreparedAtUtc, string EvidenceSha256,
    bool IsOfficialInvoicePosting, bool CanExecuteRoomChange);

/// <summary>Only external document references; all meter values are server-read.</summary>
public sealed record SliceBRoomChangeHandoverInput(
    string? InspectionEvidenceReference, string? SignedHandoverReference);

public sealed record SliceBRoomChangeHandoverRead(
    Guid Id, Guid RequestId, Guid SourceContractId, Guid SourceRoomId,
    DateOnly HandoverDate,
    Guid ElectricityMeterId, Guid ElectricityReadingId, decimal ElectricityFinalReading,
    Guid WaterMeterId, Guid WaterReadingId, decimal WaterFinalReading,
    string InspectionEvidenceReference, string SignedHandoverReference,
    Guid AttestedByUserId, DateTime AttestedAtUtc, string EvidenceSha256,
    bool AreInvoiceAndDepositReconciled, bool IsTransferExecuted);

/// <summary>Read-only and always blocked until atomic 2H writer exists.</summary>
public sealed record SliceBRoomChangeExecutionReadiness(
    Guid RequestId, bool CanExecuteTransfer, string[] Blockers,
    bool IsExternalBankVerification);

/// <summary>Root-chosen explicit safe lane; not client-supplied ledger totals.</summary>
public sealed record SliceBRoomChangeCompletionInput(
    decimal TargetMonthlyRent, RoomStatus SourceRoomDisposition,
    string? DispositionEvidenceReference, string? CompletionReference);

public sealed record SliceBRoomChangeCompletionRead(
    Guid Id, Guid RequestId, Guid SourceContractId, Guid TargetContractId,
    Guid SourceRoomId, Guid TargetRoomId,
    decimal CarriedDepositAmount, RoomStatus SourceRoomDisposition,
    string EvidenceSha256, bool IsCompleted);

// Financial archive observation is deliberately independent of any execution
// approval. A positive D03/D04 sum cannot authorize contract mutation.
public sealed record SliceBRoomChangeFinancialJournalRead(
    Guid RequestId, Guid SourceContractId, Guid BuildingId,
    int ApprovedCorrectionPostingCount, decimal SignedCorrectionNetVnd,
    int OriginalTopUpPostingCount, decimal ReviewedTopUpTotalVnd,
    bool IsActualOutboundBankRefundVerified,
    bool IsMidMonthContractCommitEnabled,
    bool CanExecuteTransfer);


public sealed record SliceBRoomChangeArchiveRead(
    Guid Id,Guid RequestId,RoomChangeEvidenceArchiveKind Kind,
    Guid SourceEvidenceId,string SourceEvidenceSha256,string RecordSha256,
    bool IsExternalSignatureAuthenticated,bool CanExecuteTransfer);

public sealed record SliceBRoomChangeThreePartyArchiveInput(
    Guid TenantRepresentativeId,
    string? OwnerSignedReference,string? StaffSignedReference,
    string? TenantSignedReference,string? AssetDefectReviewReference);

public sealed record SliceBRoomChangeAdjustmentCategoryInput(
    ContractDepositOffsetCategory Category,decimal SignedDeltaVnd,
    string SignedCategoryEvidenceReference);

public sealed record SliceBRoomChangeD03ClassificationInput(
    Guid AdjustmentId,string? SignedClassificationReference,
    SliceBRoomChangeAdjustmentCategoryInput[] Lines);

public sealed record SliceBRoomChangeD03PostingRead(
    Guid Id,Guid RequestId,Guid AdjustmentId,
    Guid OfficialReceivableId,decimal SignedAmountVnd,
    bool IsExternalBankPayment,bool CanExecuteTransfer);
