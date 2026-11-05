using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomChanges;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Staging-only H4 completion using actual signed original documents and
/// source receipts; leaves the extra deposit amount or surplus as an
/// immutable outstanding obligation, never a fabricated bank payment.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    public Task<SliceBRoomChangeCompletionRead> CompleteH4MidMonthRoomChangeAsync(
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
                source.DepositAmount <= 0m)
                throw Denied();

            var intent = await db.RoomChangeDepositDispositionIntents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            if (intent.TargetRequiredDepositVnd != input.TargetMonthlyRent ||
                intent.ProposedCarryTargetVnd <= 0m ||
                intent.ClaimedOriginalSourceHeldVnd != source.DepositAmount ||
                intent.AlreadyPostedApprovedDeductionsVnd != 0m)
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
                RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableRule(
                    rule, request.RequestedDate);
                handover.Verify(request,originals.E,originals.Er,
                    originals.W,originals.Wr);
                pricingProof.VerifyStoredSnapshot();
                physicalProof.VerifyStoredSnapshot();
                if (!roster.MatchesCurrentRoster(requestId,contractId,
                    sourceRoom,building,parties) ||
                    policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
                    policy.PricingTreatment != RoomChangePricingTreatment.AgreedDayProration ||
                    policy.DepositTreatment !=
                        (intent.ProposedUnpaidTopUpVnd>0m
                            ? RoomChangeDepositTreatment.CarryWithVerifiedTopUp
                            : RoomChangeDepositTreatment.PartialCarryWithRefund) ||
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

            // Read the original signed intent, not client-supplied cash.
            try
            {
                intent.VerifyFingerprint();
                if (intent.ProposedByOwnerId != actor ||
                    intent.TransferDateVietnam != request.RequestedDate ||
                    intent.TargetContractId == source.Id ||
                    intent.ProposedAtUtc > utc ||
                    intent.TargetRequiredDepositVnd != rule.TargetMonthlyRentVnd)
                    throw Denied();
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or
                InvalidOperationException or ArgumentException)
            { throw Denied(); }

            var money = await VerifyMidMonthSourceRentAsync(
                db,request,sourceRoom,building,rule,
                handover.ElectricityReadingId,handover.WaterReadingId,utc);
            await VerifyTransferDepositAsync(db,source);
            var actualAllocations=await db.DepositSettlements.AsNoTracking()
                .Where(x=>x.TargetContractId==source.Id &&
                    x.Type==DepositSettlementType.AllocateToContractDeposit &&
                    x.Status==DepositSettlementStatus.Completed)
                .OrderBy(x=>x.Id).ToArrayAsync();
            var sourceBytes=string.Join("\n",actualAllocations.Select(x=>
                x.Id.ToString("D")+"|"+x.PaymentId!.Value.ToString("D")+
                "|"+x.Amount.ToString("0",System.Globalization.CultureInfo.InvariantCulture)));
            var sourceProof="SOURCE-ALLOCATION-SHA256-"+
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourceBytes)))
                    .ToLowerInvariant();
            if (actualAllocations.Length==0 ||
                intent.OriginalCashLedgerReference!=sourceProof)
                throw Denied();

            // Every write belongs to the same EF UoW and the SQL deferred
            // checks must all pass at COMMIT; no provisional success outside
            // this atomic transaction can be exposed.
            var newId = intent.TargetContractId;
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
                intent.ProposedCarryTargetVnd,
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
                    actor,utc,intent);
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

            // Both sides of the transfer, the completed original cash
            // carry and the signed mid-month audit are flushed inside one
            // SERIALIZABLE transaction. No external cash is invented.
            await db.SaveChangesAsync();
            var obligationId=Guid.NewGuid();
            var kind=intent.ProposedUnpaidTopUpVnd>0m?1:
                intent.ProposedFutureRentCreditVnd>0m?2:3;
            var amount=kind==1?intent.ProposedUnpaidTopUpVnd:
                kind==2?intent.ProposedFutureRentCreditVnd:
                    intent.ProposedManualRefundVnd;
            if (amount<=0m) throw Denied();
            await using(var cmd=Command(db,"""
                INSERT INTO "BuildingManagementRoomChangeH4Obligations"
                ("Id","CompletionId","IntentId","RequestId",
                 "SourceContractId","TargetContractId",
                 "Kind","AmountVnd","DueDateVietnam",
                 "RecordedByOwnerId","RecordedAtUtc","SignedReference")
                VALUES(@id,@completion,@intent,@request,@source,@target,
                 @kind,@amount,@due::date,@owner,@at,@signed)
                """))
            {
                Add(cmd,"id",obligationId);
                Add(cmd,"completion",completion.Id);
                Add(cmd,"intent",intent.Id);
                Add(cmd,"request",request.Id);
                Add(cmd,"source",source.Id);
                Add(cmd,"target",target.Id);
                Add(cmd,"owner",actor);
                var pKind=cmd.CreateParameter();
                pKind.ParameterName="kind";pKind.Value=kind;
                cmd.Parameters.Add(pKind);
                var pAmount=cmd.CreateParameter();
                pAmount.ParameterName="amount";pAmount.Value=amount;
                cmd.Parameters.Add(pAmount);
                var pDue=cmd.CreateParameter();
                pDue.ParameterName="due";pDue.DbType=System.Data.DbType.Date;
                pDue.Value=(object?)intent.ProposedTopUpDueDateVietnam??
                    DBNull.Value;cmd.Parameters.Add(pDue);
                var pAt=cmd.CreateParameter();
                pAt.ParameterName="at";pAt.Value=completion.CompletedAtUtc;
                cmd.Parameters.Add(pAt);
                var pSigned=cmd.CreateParameter();
                pSigned.ParameterName="signed";
                pSigned.Value=intent.OwnerSignedDispositionReference;
                cmd.Parameters.Add(pSigned);
                await cmd.ExecuteNonQueryAsync();
            }

            return new SliceBRoomChangeCompletionRead(
                completion.Id,request.Id,source.Id,target.Id,
                oldRoom.Id,nextRoom.Id,completion.CarriedDepositVnd,
                completion.SourceRoomDisposition,completion.EvidenceSha256,true);
        });


}
