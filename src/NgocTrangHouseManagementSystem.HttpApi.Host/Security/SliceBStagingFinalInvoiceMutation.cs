using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.RoomReservations;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Development-only, unexposed adapter for a SINGLE scoped final-invoice
/// preparation -> independent design review -> candidate issuance path.
/// The host must explicitly register this ONLY on isolated staging.
/// No production registration or unscoped service is allowed.
/// </summary>
public sealed class SliceBStagingFinalInvoiceMutation
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly ICurrentUser _user;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly FinalInvoiceIssuePreflightAppService _preflight;
    private readonly FinalInvoiceIssueEvidenceDriftAppService _drift;
    private readonly FinalInvoiceSourceProvenanceAppService _provenance;
    private readonly ContractDepositLedgerReviewAppService _depositAudit;

    public SliceBStagingFinalInvoiceMutation(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser user,
        SliceAStagingIdentityCommitLease identity,
        FinalInvoiceIssuePreflightAppService preflight,
        FinalInvoiceIssueEvidenceDriftAppService drift,
        FinalInvoiceSourceProvenanceAppService provenance,
        ContractDepositLedgerReviewAppService depositAudit)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _user = user;
        _identity = identity;
        _preflight = preflight;
        _drift = drift;
        _provenance = provenance;
        _depositAudit = depositAudit;
    }

    /// <summary>
    /// Archive a verified current Draft and source graph. A prepared
    /// archive can NEVER itself be issued or produce a receivable.
    /// </summary>
    public Task<SliceBStagedInvoiceEvidenceRead> PrepareAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var request = await LockRequestAsync(
                    db, requestId, contractId, roomId);
                await LockDraftAsync(db, request);
                var bundle = await _preflight.GetEvidenceBundleAsync(requestId);
                if (bundle.Evidence is null ||
                    HasUnexpectedFinancialBlockers(bundle.Preflight))
                    throw Denied();
                var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
                    Guid.NewGuid(), actor, DateTime.UtcNow,
                    bundle.Evidence, bundle.Preflight);
                db.FinalInvoiceIssueEvidence.Add(archive);
                return new SliceBStagedInvoiceEvidenceRead(
                    archive.Id, archive.DraftInvoiceId, requestId,
                    archive.EvidenceSha256, archive.KnownBaseChargeTotal);
            });

    public Task<Guid> SubmitIndependentReviewAsync(
        Guid contractId, Guid stagedEvidenceId, string? note)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var archive = await RequireArchiveAsync(
                    db, contractId, roomId, stagedEvidenceId);
                var request = await LockRequestAsync(db,
                    archive.MoveOutRequestId, contractId, roomId);
                var draft = await LockDraftAsync(db, request);
                if (draft.Id != archive.DraftInvoiceId ||
                    !await HasCurrentDraftEvidenceAsync(archive.Id))
                    throw Denied();

                var ticket = InvoiceIssuanceReviewTicket.Stage(
                    Guid.NewGuid(), archive, building,
                    actor, DateTime.UtcNow, note);
                db.InvoiceIssuanceReviews.Add(ticket);
                return ticket.Id;
            });

    public Task<Guid> RecordIndependentReviewAsync(
        Guid contractId, Guid reviewTicketId,
        bool acceptedForDesign, string reason)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var ticket = await db.InvoiceIssuanceReviews
                    .SingleOrDefaultAsync(x => x.Id == reviewTicketId);
                if (ticket is null || ticket.ContractId != contractId ||
                    ticket.RoomId != roomId ||
                    ticket.BuildingId != building ||
                    ticket.SubmittedByUserId == actor)
                    throw Denied();

                var archive = await RequireArchiveAsync(
                    db, contractId, roomId, ticket.StagedEvidenceId);
                var request = await LockRequestAsync(db,
                    archive.MoveOutRequestId, contractId, roomId);
                var draft = await LockDraftAsync(db, request);
                if (draft.Id != archive.DraftInvoiceId)
                    throw Denied();

                var preflight = await _preflight.GetAsync(request.Id);
                var drift = await _drift.GetAsync(archive.Id);
                if (acceptedForDesign && !drift.MatchesCurrentDraft)
                    throw Denied();

                var scope = await BuildScopeFactsAsync(
                    db, actor, building, request, draft, archive);
                ticket.RecordReviewForDesign(
                    actor, DateTime.UtcNow, acceptedForDesign,
                    reason, AnchorPreflight(preflight, archive), scope);
                return ticket.Id;
            });

    /// <summary>
    /// Root-only, development-isolated candidate ledger entry.
    /// The caller MUST document the due date and fee decision. A source
    /// review and the physical records are locked until commit; the
    /// database independently enforces uniqueness and immutable history.
    /// This does not distribute refunds or complete a move-out.
    /// </summary>
    public Task<SliceBStagedIssuedInvoiceRead> IssueCandidateAsync(
        Guid contractId, Guid stagedEvidenceId,
        FinalInvoiceFinancialTermsProposal explicitApprovedTerms)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (explicitApprovedTerms is null ||
                    explicitApprovedTerms.AuxiliaryFeeReviewerId != actor ||
                    explicitApprovedTerms.PaymentDueDate is null ||
                    string.IsNullOrWhiteSpace(
                        explicitApprovedTerms.DueDatePolicyReference) ||
                    string.IsNullOrWhiteSpace(
                        explicitApprovedTerms.AuxiliaryFeePolicyReference))
                    throw Denied();

                var archive = await RequireArchiveAsync(
                    db, contractId, roomId, stagedEvidenceId);
                var request = await LockRequestAsync(db,
                    archive.MoveOutRequestId, contractId, roomId);
                var draft = await LockDraftAsync(db, request);
                if (draft.Id != archive.DraftInvoiceId ||
                    explicitApprovedTerms.DraftInvoiceId != draft.Id ||
                    explicitApprovedTerms.ContractId != contractId ||
                    explicitApprovedTerms.RoomId != roomId ||
                    explicitApprovedTerms.EvidenceSha256 != archive.EvidenceSha256)
                    throw Denied();

                var ticket = await db.InvoiceIssuanceReviews
                    .AsNoTracking().SingleOrDefaultAsync(x =>
                        x.StagedEvidenceId == archive.Id &&
                        x.Status == InvoiceIssuanceReviewStatus.ReviewedForDesign);
                if (ticket is null || ticket.BuildingId != building ||
                    ticket.ReviewedByUserId is null ||
                    ticket.ReviewedByUserId == ticket.SubmittedByUserId)
                    throw Denied();

                // Conservative read/write fence on the ORIGINAL meter,
                // tariff, replacement and snapshot tables until commit.
                // This is deliberately a staging-only table lock, not an
                // unreviewed production scaling strategy.
                await LockOriginalInvoiceSourcesAsync(db);
                var preflight = await _preflight.GetAsync(request.Id);
                var drift = await _drift.GetAsync(archive.Id);
                var provenance = await _provenance.GetAsync(archive.Id);
                var scope = await BuildScopeFactsAsync(
                    db, actor, building, request, draft, archive);

                var id = Guid.NewGuid();
                var number = "NTF-" + id.ToString("N");
                var ledger = IssuedInvoiceLedgerRecord.BuildVerifiedCandidate(
                    id, number, actor, DateTime.UtcNow, archive,
                    ticket, preflight, scope, explicitApprovedTerms,
                    drift, provenance);
                db.IssuedInvoiceLedger.Add(ledger);
                return new SliceBStagedIssuedInvoiceRead(
                    ledger.Id, ledger.DraftInvoiceId,
                    ledger.ContractId, ledger.TotalAmount, ledger.DueDate,
                    ledger.InvoiceNumber);
            });

    /// <summary>
    /// A root-authenticated MANUAL bank receipt may be staged only when a
    /// receipt belongs to a tenant of the exact contract and an immutable
    /// issued invoice. Payment + allocation are inserted in ONE transaction:
    /// a conflicting/over-amount journal rollbacks BOTH. The bank evidence
    /// reference is a recorded claim, not electronic bank verification.
    /// Candidate allocation does NOT authorize deposit refund/closeout.
    /// </summary>
    public Task<SliceBStagedInvoiceReceiptRead> RecordManualReceiptAsync(
        Guid contractId, Guid issuedInvoiceId,
        SliceBFinalInvoiceManualReceiptInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (input is null ||
                    input.TenantId == Guid.Empty ||
                    input.Amount <= 0m ||
                    decimal.Truncate(input.Amount) != input.Amount ||
                    input.PaidAtUtc == default ||
                    input.PaidAtUtc.Kind != DateTimeKind.Utc ||
                    input.PaidAtUtc > DateTime.UtcNow ||
                    string.IsNullOrWhiteSpace(input.TransactionReference) ||
                    input.TransactionReference.Trim().Length > 128 ||
                    input.TransactionReference.Trim().Length < 8 ||
                    string.IsNullOrWhiteSpace(input.BankEvidenceReference) ||
                    input.BankEvidenceReference.Trim().Length < 8 ||
                    input.BankEvidenceReference.Trim().Length > 200)
                    throw Denied();

                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementIssuedInvoiceLedger"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR UPDATE
                    """))
                {
                    Add(cmd, "id", issuedInvoiceId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                var ledger = await db.IssuedInvoiceLedger.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == issuedInvoiceId);
                if (ledger is null || ledger.ContractId != contractId ||
                    ledger.RoomId != roomId || ledger.BuildingId != building ||
                    input.PaidAtUtc < ledger.IssuedAtUtc)
                    throw Denied();

                // A bank receipt may never be attributed to a foreign
                // building's tenant or an arbitrary existing TenantId.
                await using (var cmd = Command(db, """
                    SELECT ct."Id" FROM "BuildingManagementContractTenants" ct
                      JOIN "BuildingManagementTenants" t
                        ON t."Id" = ct."TenantId"
                     WHERE ct."ContractId" = @contract
                       AND ct."TenantId" = @tenant
                       AND ct."IsDeleted" = FALSE
                       AND t."IsDeleted" = FALSE
                     FOR SHARE OF ct, t
                    """))
                {
                    Add(cmd, "contract", contractId);
                    Add(cmd, "tenant", input.TenantId);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                var receiptId = Guid.NewGuid();
                var receipt = new Payment(
                    receiptId, "FIN-" + receiptId.ToString("N"),
                    input.TenantId,
                    PaymentPurpose.InvoicePayment,
                    PaymentMethod.BankTransfer,
                    PaymentChannel.Manual,
                    input.Amount, contractId: contractId,
                    invoiceId: issuedInvoiceId,
                    transactionReference: input.TransactionReference.Trim(),
                    notes: "BANK-PROOF: " + input.BankEvidenceReference.Trim());
                receipt.Complete(
                    input.PaidAtUtc, input.TransactionReference.Trim());
                db.Payments.Add(receipt);
                // FK needs its source receipt before immutable journal
                // INSERT; both SaveChanges occur in SAME enclosing UoW.
                await db.SaveChangesAsync();

                var journal = InvoicePaymentAllocationJournalEntry.StageForValidation(
                    Guid.NewGuid(), receipt, issuedInvoiceId, actor,
                    DateTime.UtcNow);
                db.InvoicePaymentAllocationJournal.Add(journal);
                // The PG trigger serializes concurrent allocations on the
                // same issued ledger and refuses an overpayment atomically.
                return new SliceBStagedInvoiceReceiptRead(
                    receipt.Id, journal.Id, ledger.Id, receipt.Amount);
            });

    /// <summary>
    /// Explicit root-only, isolated staging final handover. A MANUAL refund
    /// bank reference is a human attestation, not automatic bank verification.
    /// Never deduct deposit from invoice debt: every issued invoice MUST be
    /// fully matched to already-completed invoice receipts first.
    ///
    /// All money dispositions, immutable approval, 4 non-meter checklists,
    /// Contract.End, MoveOut.Complete and Room release commit in ONE Building
    /// UoW. Any failure rolls back ALL changes.
    /// </summary>
    public Task<SliceBStagedMoveOutCloseoutRead> CompleteMoveOutAsync(
        Guid contractId, Guid requestId,
        SliceBFinalMoveOutCloseoutInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (input is null ||
                    requestId == Guid.Empty ||
                    !input.ConfirmManualRefundPaid ||
                    input.ForfeitAmount < 0m ||
                    decimal.Truncate(input.ForfeitAmount) != input.ForfeitAmount ||
                    !ValidProof(input.InspectionEvidenceReference) ||
                    !ValidProof(input.HandoverEvidenceReference) ||
                    !ValidProof(input.DepositPolicyReference) ||
                    input.RefundPaidAtUtc.HasValue &&
                        (input.RefundPaidAtUtc.Value.Kind != DateTimeKind.Utc ||
                         input.RefundPaidAtUtc.Value > DateTime.UtcNow))
                    throw Denied();

                var requestSource = await LockRequestAsync(
                    db, requestId, contractId, roomId);
                var contract = await db.Contracts
                    .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted);
                var request = await db.MoveOutRequests
                    .SingleOrDefaultAsync(x => x.Id == requestId && !x.IsDeleted);
                var room = await db.Rooms
                    .SingleOrDefaultAsync(x => x.Id == roomId && !x.IsDeleted);
                if (contract is null || request is null || room is null ||
                    contract.Status != ContractStatus.Active ||
                    contract.RoomId != roomId ||
                    request.ContractId != contract.Id ||
                    request.RoomId != room.Id ||
                    request.Status != MoveOutRequestStatus.Approved ||
                    request.ApprovedMoveOutDate is not DateOnly approvedDate ||
                    room.Status != RoomStatus.Occupied ||
                    approvedDate > DateOnly.FromDateTime(
                        TimeZoneInfo.ConvertTimeFromUtc(
                            DateTime.UtcNow,
                            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))))
                    throw Denied();

                // Lock the room row FOR UPDATE and block concurrent new
                // reservations or active contracts while we close it.
                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementRooms"
                     WHERE "Id" = @id AND "IsDeleted" = FALSE FOR UPDATE
                    """))
                {
                    Add(cmd, "id", roomId);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                if (await db.Contracts.AsNoTracking().AnyAsync(x =>
                        x.Id != contractId && x.RoomId == roomId &&
                        !x.IsDeleted && x.Status == ContractStatus.Active) ||
                    await db.RoomReservations.AsNoTracking().AnyAsync(x =>
                        x.RoomId == roomId && !x.IsDeleted &&
                        (x.Status == RoomReservationStatus.PendingPayment ||
                         x.Status == RoomReservationStatus.Reserved)))
                    throw Denied();

                // Exclusive signoff is tied to the pre-existing, verified
                // final meter checklist; this method NEVER signs the meter.
                var tasks = await db.MoveOutChecklistTasks
                    .Where(x => x.MoveOutRequestId == requestId)
                    .ToArrayAsync();
                if (tasks.Length != 5 ||
                    tasks.Select(x => x.Type).Distinct().Count() != 5 ||
                    !Enum.GetValues<MoveOutChecklistTaskType>()
                        .All(t => tasks.Any(x => x.Type == t)) ||
                    !tasks.Single(x =>
                        x.Type == MoveOutChecklistTaskType.FinalMeterReading).IsCompleted ||
                    tasks.Any(x => x.Type != MoveOutChecklistTaskType.FinalMeterReading &&
                                   x.IsCompleted))
                    throw Denied();

                // Every invoice in the contract must have a completed
                // source receipt journal, not merely an aggregate paid sum.
                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementIssuedInvoiceLedger"
                     WHERE "ContractId" = @contract ORDER BY "Id" FOR UPDATE
                    """))
                {
                    Add(cmd, "contract", contractId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) { }
                }

                var invoices = await db.IssuedInvoiceLedger.AsNoTracking()
                    .Where(x => x.ContractId == contractId).ToArrayAsync();
                var final = invoices.SingleOrDefault(x =>
                    x.MoveOutRequestId == requestId && x.RoomId == roomId);
                if (final is null || invoices.Length == 0 ||
                    await db.DepositCloseoutApprovals.AsNoTracking()
                        .AnyAsync(x => x.ContractId == contractId ||
                                       x.MoveOutRequestId == requestId))
                    throw Denied();

                foreach (var invoice in invoices)
                {
                    var evidence = await db.FinalInvoiceIssueEvidence
                        .AsNoTracking().SingleOrDefaultAsync(x =>
                            x.Id == invoice.StagedEvidenceId);
                    var review = await db.InvoiceIssuanceReviews
                        .AsNoTracking().SingleOrDefaultAsync(x =>
                            x.Id == invoice.ReviewTicketId);
                    if (evidence is null || review is null)
                        throw Denied();
                    invoice.VerifyStoredTerms(evidence, review);
                }

                var journals = await db.InvoicePaymentAllocationJournal
                    .AsNoTracking().Where(x => x.ContractId == contractId)
                    .ToArrayAsync();
                var payments = await db.Payments.IgnoreQueryFilters()
                    .AsNoTracking().Where(x =>
                        x.ContractId == contractId &&
                        x.Purpose == PaymentPurpose.InvoicePayment)
                    .ToArrayAsync();
                var allocationAudit = InvoicePaymentAllocationJournalAuditor
                    .Assess(journals, payments, contractId);

                // The contract lock and same-transaction deposit table
                // audit prevent other scoped writers from settling while
                // this explicit root owner closeout is in progress.
                var depositAudit = await _depositAudit.GetAsync(requestId);
                if (depositAudit.VerifiedIncomingAllocationTotal is not decimal held ||
                    held <= 0m || held != contract.DepositAmount ||
                    input.ForfeitAmount > held)
                    throw Denied();

                decimal refundAmount = held - input.ForfeitAmount;
                if (refundAmount > 0m &&
                    (!ValidProof(input.RefundTransactionReference) ||
                     !ValidProof(input.RefundBankEvidenceReference) ||
                     input.RefundPaidAtUtc is null ||
                     input.RefundPaidAtUtc > DateTime.UtcNow))
                    throw Denied();
                if (refundAmount == 0m &&
                    (input.RefundPaidAtUtc.HasValue ||
                     !string.IsNullOrWhiteSpace(input.RefundTransactionReference) ||
                     !string.IsNullOrWhiteSpace(input.RefundBankEvidenceReference)))
                    throw Denied();

                var refundId = refundAmount > 0m ? Guid.NewGuid() : (Guid?)null;
                var forfeitId = input.ForfeitAmount > 0m
                    ? Guid.NewGuid() : (Guid?)null;
                var now = DateTime.UtcNow;

                var approval = ContractDepositCloseoutApprovalRecord
                    .ApproveCandidate(
                        Guid.NewGuid(), actor, now,
                        request, contract, final, invoices, journals,
                        allocationAudit, depositAudit, input.ForfeitAmount,
                        input.DamageReason, input.DamageEvidenceReference,
                        input.InspectionEvidenceReference,
                        input.HandoverEvidenceReference,
                        input.DepositPolicyReference,
                        refundId, forfeitId,
                        input.RefundTransactionReference,
                        input.RefundPaidAtUtc);

                if (refundId.HasValue)
                {
                    var refund = new DepositSettlement(
                        refundId.Value, DepositSettlementType.Refund,
                        refundAmount, sourceContractId: contractId,
                        notes: "MANUAL BANK REF " +
                            input.RefundTransactionReference!.Trim() +
                            " / " + input.RefundBankEvidenceReference!.Trim());
                    refund.Complete(input.RefundPaidAtUtc!.Value);
                    db.DepositSettlements.Add(refund);
                }
                if (forfeitId.HasValue)
                {
                    var forfeit = new DepositSettlement(
                        forfeitId.Value, DepositSettlementType.Forfeit,
                        input.ForfeitAmount, sourceContractId: contractId,
                        notes: input.DamageReason!.Trim() +
                            " / " + input.DamageEvidenceReference!.Trim());
                    forfeit.Complete(now);
                    db.DepositSettlements.Add(forfeit);
                }

                // Persist completed cash dispositions BEFORE immutable
                // approval. The audit layer may mark previously read
                // DepositSettlement rows as modified while the approval
                // and later contract/room mutations are flushed. Detach
                // those already-saved rows so no UPDATE is attempted AFTER
                // the approval trigger has permanently frozen the ledger.
                // Never downgrade the database immutability protection.
                await db.SaveChangesAsync();
                foreach (var tracked in db.ChangeTracker
                    .Entries<DepositSettlement>().ToArray())
                {
                    if (tracked.State != EntityState.Unchanged)
                        throw Denied();
                    tracked.State = EntityState.Detached;
                }
                db.DepositCloseoutApprovals.Add(approval);
                await db.SaveChangesAsync();

                foreach (var task in tasks.Where(x =>
                    x.Type != MoveOutChecklistTaskType.FinalMeterReading))
                {
                    var proof = task.Type switch
                    {
                        MoveOutChecklistTaskType.RoomInspection =>
                            input.InspectionEvidenceReference,
                        MoveOutChecklistTaskType.Handover =>
                            input.HandoverEvidenceReference,
                        MoveOutChecklistTaskType.FinalInvoice =>
                            final.InvoiceNumber,
                        MoveOutChecklistTaskType.DepositSettlement =>
                            approval.ApprovalSha256,
                        _ => throw Denied()
                    };
                    task.MarkCloseoutEvidenceVerified(task.Type, actor, proof, now);
                }

                // All irreversible state changes must happen only after
                // settlement/approval persistence succeeds in same UoW.
                request.Complete(now);
                var reason = contract.EndDate.HasValue &&
                    approvedDate < DateOnly.FromDateTime(contract.EndDate.Value)
                    ? ContractEndReason.EarlyTermination
                    : ContractEndReason.Normal;
                contract.End(now, reason);
                room.ChangeStatus(input.ForfeitAmount > 0m
                    ? RoomStatus.Maintenance : RoomStatus.Available);

                return new SliceBStagedMoveOutCloseoutRead(
                    request.Id, contract.Id, room.Id, approval.Id,
                    final.Id, held, refundAmount, input.ForfeitAmount,
                    room.Status);
            });

    private static bool ValidProof(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Trim().Length >= 8 &&
           text.Trim().Length <= 500;

    private async Task<T> ExecuteScopedAsync<T>(
        Guid contractId, bool requireBootstrapOwner,
        Func<BuildingManagementDbContext, Guid, Guid, Guid, Task<T>> work)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
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
        if (requireBootstrapOwner)
            await _identity.AcquireBootstrapAsync(actor);

        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                var roomId = await LockContractRoomAsync(db, contractId);
                if (await LockRoomBuildingAsync(db, roomId) != building)
                    throw Denied();
                var contract = await db.Contracts.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == contractId && !x.IsDeleted);
                if (contract is null || contract.Status != ContractStatus.Active ||
                    contract.RoomId != roomId)
                    throw Denied();
                return await work(db, actor, building, roomId);
            }, () => _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract, contractId,
                BuildingBusinessCapability.ManageContract));
    }

    private async Task<bool> HasCurrentDraftEvidenceAsync(Guid archiveId)
        => (await _drift.GetAsync(archiveId)).MatchesCurrentDraft;

    private static bool HasUnexpectedFinancialBlockers(
        FinalInvoiceIssuePreflight preflight)
        => preflight.Blockers.Any(x => x is not
            "AuxiliaryFeePolicyUndecided" and
            not "InvoiceDueDatePolicyUndecided" and
            not "IssuerBuildingAuthorizationUnavailable" and
            not "ImmutableIssuedInvoiceLedgerNotImplemented" and
            not "AuditedPaymentAllocationNotImplemented");

    private static FinalInvoiceIssuePreflight AnchorPreflight(
        FinalInvoiceIssuePreflight current,
        FinalInvoiceIssueEvidenceRecord archived)
        => current.AnchorToVerifiedArchivedEvidence(archived);

    private static async Task<FinalInvoiceIssueEvidenceRecord> RequireArchiveAsync(
        BuildingManagementDbContext db, Guid contractId,
        Guid roomId, Guid archiveId)
    {
        if (archiveId == Guid.Empty)
            throw Denied();
        var archived = await db.FinalInvoiceIssueEvidence
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == archiveId);
        if (archived is null || archived.ContractId != contractId ||
            archived.RoomId != roomId)
            throw Denied();
        archived.VerifyStoredEvidence();
        return archived;
    }

    private static async Task<MoveOutRequest> LockRequestAsync(
        BuildingManagementDbContext db, Guid requestId,
        Guid contractId, Guid roomId)
    {
        if (requestId == Guid.Empty)
            throw Denied();
        await using (var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementMoveOutRequests"
             WHERE "Id" = @id AND "ContractId" = @contract
               AND "RoomId" = @room AND "IsDeleted" = FALSE
             FOR SHARE
            """))
        {
            Add(cmd, "id", requestId);
            Add(cmd, "contract", contractId);
            Add(cmd, "room", roomId);
            if (await cmd.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }
        var request = await db.MoveOutRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestId && !x.IsDeleted);
        if (request is null || request.ContractId != contractId ||
            request.RoomId != roomId ||
            request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue)
            throw Denied();
        return request;
    }

    private static async Task<Invoice> LockDraftAsync(
        BuildingManagementDbContext db, MoveOutRequest request)
    {
        var date = request.ApprovedMoveOutDate
            ?? throw Denied();
        var drafts = await db.Invoices.AsNoTracking()
            .Where(x => x.ContractId == request.ContractId &&
                x.RoomId == request.RoomId &&
                x.BillingYear == date.Year &&
                x.BillingMonth == date.Month)
            .ToArrayAsync();
        if (drafts.Length != 1 || drafts[0].Status != InvoiceStatus.Draft)
            throw Denied();

        await using var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementInvoices"
             WHERE "Id" = @id FOR UPDATE
            """);
        Add(cmd, "id", drafts[0].Id);
        if (await cmd.ExecuteScalarAsync() is not Guid)
            throw Denied();
        return drafts[0];
    }

    private static async Task<InvoiceIssuanceScopeFacts> BuildScopeFactsAsync(
        BuildingManagementDbContext db, Guid actor, Guid building,
        MoveOutRequest request, Invoice draft,
        FinalInvoiceIssueEvidenceRecord archived)
    {
        var contract = await db.Contracts.AsNoTracking()
            .SingleAsync(x => x.Id == request.ContractId);
        var room = await db.Rooms.AsNoTracking()
            .SingleAsync(x => x.Id == request.RoomId);
        var floor = await db.Floors.AsNoTracking()
            .SingleAsync(x => x.Id == room.FloorId);
        if (floor.BuildingId != building || room.Id != contract.RoomId)
            throw Denied();
        var issuedExists = await db.IssuedInvoiceLedger.AsNoTracking()
            .AnyAsync(x => x.DraftInvoiceId == draft.Id ||
                (x.RoomId == room.Id &&
                 x.BillingYear == draft.BillingYear &&
                 x.BillingMonth == draft.BillingMonth));
        return new InvoiceIssuanceScopeFacts(
            actor, true,
            request.Id, request.Id, draft.Id, draft.Id,
            draft.ContractId, draft.RoomId, contract.Id, contract.RoomId,
            room.Id, room.FloorId, floor.Id, floor.BuildingId, building,
            new[] { building }, draft.Status, archived.EvidenceSha256,
            issuedExists);
    }

    private static async Task LockOriginalInvoiceSourcesAsync(
        BuildingManagementDbContext db)
    {
        await using var cmd = Command(db, """
            LOCK TABLE "BuildingManagementMeterReadings",
                       "BuildingManagementUtilityMeters",
                       "BuildingManagementUtilityRates",
                       "BuildingManagementMeterReplacements",
                       "BuildingManagementInvoiceItems",
                       "BuildingManagementInvoiceItemMeterSources"
                IN SHARE MODE
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> LockContractRoomAsync(
        BuildingManagementDbContext db, Guid contractId)
    {
        await using var cmd = Command(db, """
            SELECT "RoomId" FROM "BuildingManagementContracts"
             WHERE "Id" = @id AND "IsDeleted" = FALSE
             FOR UPDATE
            """);
        Add(cmd, "id", contractId);
        return await cmd.ExecuteScalarAsync() is Guid id && id != Guid.Empty
            ? id : throw Denied();
    }

    private static async Task<Guid> LockRoomBuildingAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId"
              FROM "BuildingManagementRooms" r
              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
              JOIN "Buildings" b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR SHARE OF r, f, b
            """);
        Add(cmd, "id", roomId);
        return await cmd.ExecuteScalarAsync() is Guid id && id != Guid.Empty
            ? id : throw Denied();
    }

    private static DbCommand Command(BuildingManagementDbContext db, string sql)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, Guid value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private Guid RequireActor()
    {
        if (!_user.IsAuthenticated || _user.Id is not Guid id ||
            id == Guid.Empty)
            throw Denied();
        return id;
    }

    private static AbpAuthorizationException Denied()
        => new("Final invoice requires a live, locked building grant, " +
               "current source evidence, independent review and explicit policy.");
}

public sealed record SliceBStagedInvoiceEvidenceRead(
    Guid Id, Guid DraftInvoiceId, Guid MoveOutRequestId,
    string Sha256, decimal KnownBaseAmount);

public sealed record SliceBStagedIssuedInvoiceRead(
    Guid Id, Guid DraftInvoiceId, Guid ContractId, decimal Amount,
    DateOnly DueDate, string InvoiceNumber);

public sealed class SliceBFinalInvoiceManualReceiptInput
{
    public Guid TenantId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAtUtc { get; set; }
    public string TransactionReference { get; set; } = string.Empty;
    public string BankEvidenceReference { get; set; } = string.Empty;
}

public sealed record SliceBStagedInvoiceReceiptRead(
    Guid PaymentId, Guid AllocationJournalId,
    Guid IssuedInvoiceId, decimal StagedAmount);

public sealed class SliceBFinalMoveOutCloseoutInput
{
    public decimal ForfeitAmount { get; set; }
    public string? DamageReason { get; set; }
    public string? DamageEvidenceReference { get; set; }
    public string InspectionEvidenceReference { get; set; } = string.Empty;
    public string HandoverEvidenceReference { get; set; } = string.Empty;
    public string DepositPolicyReference { get; set; } = string.Empty;
    // This is an explicit statement that the owner has ALREADY made the
    // external bank transfer. It is NOT a payment instruction to the app.
    public bool ConfirmManualRefundPaid { get; set; }
    public DateTime? RefundPaidAtUtc { get; set; }
    public string? RefundTransactionReference { get; set; }
    public string? RefundBankEvidenceReference { get; set; }
}

public sealed record SliceBStagedMoveOutCloseoutRead(
    Guid MoveOutRequestId, Guid ContractId, Guid RoomId,
    Guid CloseoutApprovalId, Guid IssuedInvoiceId,
    decimal VerifiedHeldDeposit, decimal RefundAmount,
    decimal ForfeitAmount, RoomStatus RoomStatus);
