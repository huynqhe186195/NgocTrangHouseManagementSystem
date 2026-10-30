using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Rooms;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Isolated-staging-only, explicitly registered read adapter for Slice B.
/// The old unscoped ContractAppService HTTP actions stay deny-all.
/// Returned rows are filtered via a live membership JOIN/EXISTS in SQL.
/// </summary>
public sealed class SliceBStagingContractQuery
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly ICurrentUser _currentUser;
    private readonly SliceAStagingIdentityCommitLease _identityLease;
    private readonly MoveOutCompletionReadinessAppService _readiness;
    private readonly FinalInvoiceIssuePreflightAppService _invoicePreflight;
    private readonly ContractDepositLedgerReviewAppService _depositLedger;
    private readonly MonthlyInvoiceBasePreviewAppService _monthlyPreview;

    public SliceBStagingContractQuery(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy access,
        ICurrentUser currentUser,
        SliceAStagingIdentityCommitLease identityLease,
        MoveOutCompletionReadinessAppService readiness,
        FinalInvoiceIssuePreflightAppService invoicePreflight,
        ContractDepositLedgerReviewAppService depositLedger,
        MonthlyInvoiceBasePreviewAppService monthlyPreview)
    {
        _provider = provider;
        _access = access;
        _currentUser = currentUser;
        _identityLease = identityLease;
        _readiness = readiness;
        _invoicePreflight = invoicePreflight;
        _depositLedger = depositLedger;
        _monthlyPreview = monthlyPreview;
    }

    public async Task<IReadOnlyList<SliceBScopedContractRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100)
            throw Denied();
        var actor = RequireActor();
        await _access.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        var rows = AuthorizedContracts(await _provider.GetDbContextAsync(), actor);
        return await rows.Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceBScopedContractRead> GetAsync(Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        return await AuthorizedContracts(
                await _provider.GetDbContextAsync(), actor, contractId)
            .SingleOrDefaultAsync()
            ?? throw Denied();
    }

    /// <summary>
    /// Live scoped ContractDeposit receipts only, without private evidence.
    /// The payment and the membership must join to the SAME contract graph.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedDepositPaymentRead>>
        GetContractDepositPaymentsAsync(Guid contractId)
    {
        if (contractId == Guid.Empty) throw Denied();
        await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        return await (
            from payment in db.Payments.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on payment.ContractId equals (Guid?)contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where payment.ContractId == contractId &&
                payment.Purpose == PaymentPurpose.ContractDeposit &&
                !payment.IsDeleted && !contract.IsDeleted &&
                !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            orderby payment.CreationTime descending, payment.Id
            select new SliceBScopedDepositPaymentRead(
                payment.Id, payment.Amount, payment.Status, payment.PaidAt,
                payment.Purpose))
            .Take(100).ToListAsync();
    }

    /// <summary>
    /// Return only relation metadata, never global Tenant PII. The contract
    /// scope is checked before the query, and membership is revalidated
    /// within the SQL relation query itself.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedContractTenantLink>> GetTenantsAsync(
        Guid contractId)
    {
        var scoped = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        return await (
            from link in db.ContractTenants.AsNoTracking()
            where link.ContractId == contractId && !link.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == scoped.BuildingId &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= DateTime.UtcNow &&
                    (grant.ExpiresAtUtc == null ||
                     grant.ExpiresAtUtc > DateTime.UtcNow))
            orderby link.TenantId
            select new SliceBScopedContractTenantLink(link.TenantId, link.Role))
            .ToListAsync();
    }

    /// <summary>
    /// A move-out request inherits contract scope. Join its current
    /// Contract→Room→Floor→Building path AND the live operator grant in SQL
    /// before returning any result; do not expose free-form private notes.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedMoveOutRequestRead>> GetMoveOutRequestsAsync(
        Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        return await (
            from request in db.MoveOutRequests.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.ContractId == contractId &&
                !request.IsDeleted && !contract.IsDeleted &&
                !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                request.RoomId == room.Id &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            orderby request.CreationTime descending, request.Id
            select new SliceBScopedMoveOutRequestRead(
                request.Id, request.ContractId, request.RoomId,
                building.Id, request.Status, request.RequestedMoveOutDate,
                request.ApprovedMoveOutDate, request.CreationTime))
            .ToListAsync();
    }

    /// <summary>
    /// The checklist must be scoped using the request's current Contract,
    /// Room and Building, not by trusting an arbitrary request ID.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedMoveOutChecklistRead>> GetMoveOutChecklistAsync(
        Guid contractId, Guid requestId)
    {
        if (contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);

        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var authorizedRequest = await (
            from request in db.MoveOutRequests.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.Id == requestId && request.ContractId == contractId &&
                request.RoomId == room.Id &&
                !request.IsDeleted && !contract.IsDeleted && !room.IsDeleted &&
                !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            select request.Id).AnyAsync();
        if (!authorizedRequest)
            throw Denied();

        // Check the grant again in the SAME SELECT that reads checklist
        // data. A membership revoke between preflight and this query must
        // never turn a stale preflight into disclosure.
        return await (
            from task in db.MoveOutChecklistTasks.AsNoTracking()
            join request in db.MoveOutRequests.AsNoTracking()
                on task.MoveOutRequestId equals request.Id
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.Id == requestId && request.ContractId == contractId &&
                request.RoomId == room.Id &&
                !request.IsDeleted && !contract.IsDeleted &&
                !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            orderby task.Type
            select new SliceBScopedMoveOutChecklistRead(
                task.Id, task.MoveOutRequestId, task.Type,
                task.IsCompleted, task.CompletedAt))
            .ToListAsync();
    }

    /// <summary>
    /// Read-only move-out closing preflight. Lock request/contract/room
    /// graph and membership FOR SHARE through the transactional read UoW,
    /// so revocation or room transfer cannot overtake the subsequent
    /// cross-module evidence assessment.
    /// </summary>
    public async Task<MoveOutCompletionReadinessDto> GetMoveOutReadinessAsync(
        Guid contractId, Guid requestId)
    {
        if (contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        if (!db.Database.IsNpgsql())
            throw Denied();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                SELECT req."Id"
                  FROM "BuildingManagementMoveOutRequests" AS req
                  JOIN "BuildingManagementContracts" AS contract
                    ON contract."Id" = req."ContractId"
                  JOIN "BuildingManagementRooms" AS room
                    ON room."Id" = contract."RoomId"
                  JOIN "BuildingManagementFloors" AS floor
                    ON floor."Id" = room."FloorId"
                  JOIN "Buildings" AS building
                    ON building."Id" = floor."BuildingId"
                  JOIN "BuildingManagementOperatorBuildingGrants" AS membership
                    ON membership."BuildingId" = building."Id"
                 WHERE req."Id" = @request
                   AND req."ContractId" = @contract
                   AND req."RoomId" = contract."RoomId"
                   AND membership."OperatorUserId" = @actor
                   AND membership."RevokedAtUtc" IS NULL
                   AND membership."GrantedAtUtc" <= NOW()
                   AND (membership."ExpiresAtUtc" IS NULL OR
                        membership."ExpiresAtUtc" > NOW())
                   AND NOT req."IsDeleted" AND NOT contract."IsDeleted"
                   AND NOT room."IsDeleted" AND NOT floor."IsDeleted"
                   AND NOT building."IsDeleted"
                 FOR SHARE OF req, contract, room, floor, building, membership
                """;
            foreach (var (key, value) in new[]
                { ("request", requestId), ("contract", contractId),
                  ("actor", actor) })
            {
                var parameter = cmd.CreateParameter();
                parameter.ParameterName = key;
                parameter.Value = value;
                cmd.Parameters.Add(parameter);
            }
            if (await cmd.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }

        // This evaluator deliberately denies final completion until invoice
        // issuance, debt clearance, and deposit disposition actually exist.
        var report = await _readiness.GetAsync(requestId);
        if (report.ContractId != contractId ||
            report.MoveOutRequestId != requestId ||
            report.CanCompleteMoveOut)
            throw Denied();
        return report;
    }

    /// <summary>
    /// A single transactional, scoped view of BOTH final utility billing
    /// evidence and contract deposit journal anomalies. This never issues
    /// an invoice, posts a receivable, reconciles debt or refunds cash.
    /// GetMoveOutReadinessAsync holds the live request/contract/membership
    /// graph locks and Identity permission lease through this same UoW.
    /// </summary>
    public async Task<SliceBScopedMoveOutFinancialRead> GetMoveOutFinancialReadinessAsync(
        Guid contractId, Guid requestId)
    {
        var closeout = await GetMoveOutReadinessAsync(contractId, requestId);
        // Phase 2G.3's STAGED evidence/review tables have no live PG
        // migration. Do NOT query FinalInvoiceReleaseReadinessAppService,
        // which requires those absent relations. The persisted draft/utility
        // preflight and deposit settlement journal ARE available for review.
        var invoice = await _invoicePreflight.GetAsync(requestId);
        var deposit = await _depositLedger.GetAsync(requestId);
        if (invoice.ContractId != contractId ||
            invoice.MoveOutRequestId != requestId ||
            deposit.ContractId != contractId ||
            deposit.MoveOutRequestId != requestId ||
            closeout.ContractId != contractId ||
            closeout.MoveOutRequestId != requestId ||
            closeout.CanCompleteMoveOut ||
            invoice.CanIssue ||
            invoice.IsReceivablePosted ||
            invoice.IsDebtCalculated ||
            deposit.CanRefund ||
            deposit.CanOffsetDebt ||
            deposit.IsSettlementApproved)
            throw Denied();

        var invoiceBlockers = invoice.Blockers.ToList();
        invoiceBlockers.Add("StagedInvoiceEvidencePersistenceNotMigrated");
        invoiceBlockers.Add("IndependentInvoiceReviewPersistenceNotMigrated");
        invoiceBlockers.Add("FinalInvoiceIssuanceNotImplemented");

        return new SliceBScopedMoveOutFinancialRead(
            requestId, contractId, invoice.UtilityDraftId,
            invoice.KnownBaseChargeTotal,
            deposit.ObservedCompletedContractDepositReceiptsTotal,
            deposit.VerifiedIncomingAllocationTotal,
            deposit.RecordedCompletedOutgoingTotal,
            false, false, false,
            invoiceBlockers, deposit.Blockers.ToArray(),
            closeout.Blockers.ToArray(),
            false, false, false, false);
    }

    /// <summary>
    /// Final handover history is an immutable, already-committed financial
    /// event, NOT a preflight prediction. Keep live Contracts.Read, Identity
    /// and building grant checks in the same transactional read, and return
    /// only a non-PII summary (never bank references or damage notes).
    /// </summary>
    public async Task<SliceBScopedSignedCloseoutRead> GetSignedCloseoutAsync(
        Guid contractId, Guid requestId)
    {
        if (contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        var scoped = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        if (!db.Database.IsNpgsql())
            throw Denied();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval."Id", approval."IssuedInvoiceId",
                   approval."ApprovedAtUtc", approval."VerifiedHeldAmount",
                   approval."ApprovedRefundAmount",
                   approval."ApprovedForfeitAmount", approval."ApprovalSha256",
                   contract."Status", req."Status", room."Status"
              FROM "BuildingManagementContractDepositCloseoutApprovals" approval
              JOIN "BuildingManagementMoveOutRequests" req
                ON req."Id" = approval."MoveOutRequestId"
              JOIN "BuildingManagementContracts" contract
                ON contract."Id" = approval."ContractId"
              JOIN "BuildingManagementRooms" room
                ON room."Id" = contract."RoomId"
              JOIN "BuildingManagementFloors" floor
                ON floor."Id" = room."FloorId"
              JOIN "Buildings" building
                ON building."Id" = floor."BuildingId"
              JOIN "BuildingManagementOperatorBuildingGrants" membership
                ON membership."BuildingId" = building."Id"
             WHERE approval."ContractId" = @contract
               AND approval."MoveOutRequestId" = @request
               AND approval."BuildingId" = @building
               AND req."ContractId" = contract."Id"
               AND req."RoomId" = contract."RoomId"
               AND membership."OperatorUserId" = @actor
               AND membership."RevokedAtUtc" IS NULL
               AND membership."GrantedAtUtc" <= NOW()
               AND (membership."ExpiresAtUtc" IS NULL OR
                    membership."ExpiresAtUtc" > NOW())
               AND NOT req."IsDeleted" AND NOT contract."IsDeleted"
               AND NOT room."IsDeleted" AND NOT floor."IsDeleted"
               AND NOT building."IsDeleted"
             FOR SHARE OF approval, req, contract, room, floor, building, membership
            """;
        foreach (var (key, value) in new[]
        {
            ("contract", contractId), ("request", requestId),
            ("building", scoped.BuildingId), ("actor", actor)
        })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw Denied();

        var approvedId = reader.GetGuid(0);
        var issuedId = reader.GetGuid(1);
        var approvedAt = reader.GetDateTime(2);
        var deposit = reader.GetDecimal(3);
        var refund = reader.GetDecimal(4);
        var forfeit = reader.GetDecimal(5);
        var sha = reader.GetString(6);
        if ((ContractStatus)reader.GetInt32(7) != ContractStatus.Ended ||
            (MoveOutRequestStatus)reader.GetInt32(8) != MoveOutRequestStatus.Completed ||
            deposit <= 0m || refund < 0m || forfeit < 0m ||
            refund + forfeit != deposit || sha.Length != 64)
            throw Denied();
        // The current room may be reserved, occupied by a NEW tenant or
        // in maintenance months after this signed historical closeout.
        // Never use its mutable current status to deny or rewrite archival
        // evidence; distinguish recorded closeout disposition from now.
        var roomAtCloseout = forfeit > 0m
            ? RoomStatus.Maintenance : RoomStatus.Available;
        return new SliceBScopedSignedCloseoutRead(
            approvedId, contractId, requestId, issuedId,
            approvedAt, deposit, refund, forfeit,
            roomAtCloseout, (RoomStatus)reader.GetInt32(9), sha,
            RefundManuallyAttested: refund > 0m,
            BankTransferIndependentlyVerified: false);
    }

    /// <summary>
    /// Phase 2G.3 staging-only, current-contract issued-invoice balance.
    /// This is a verifiable OBSERVED receipt ledger, NOT a declaration that
    /// official receivables, bank payments or deposits have been posted.
    /// Never trust a client-supplied BuildingId, TenantId or amount.
    /// </summary>
    public async Task<SliceBScopedIssuedInvoiceBalanceRead> GetIssuedInvoiceBalanceAsync(
        Guid contractId, Guid issuedInvoiceId)
    {
        if (contractId == Guid.Empty || issuedInvoiceId == Guid.Empty)
            throw Denied();
        var authorizedContract = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;

        var invoice = await (
            from issued in db.IssuedInvoiceLedger.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on issued.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where issued.Id == issuedInvoiceId &&
                issued.ContractId == contractId &&
                issued.RoomId == room.Id &&
                issued.BuildingId == building.Id &&
                issued.BuildingId == authorizedContract.BuildingId &&
                !contract.IsDeleted && !room.IsDeleted &&
                !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            select issued).SingleOrDefaultAsync() ?? throw Denied();

        // An indexed total alone is not proof: verify the signed invoice
        // against its persisted source archive and independent review.
        var archive = await db.FinalInvoiceIssueEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invoice.StagedEvidenceId)
            ?? throw Denied();
        var review = await db.InvoiceIssuanceReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invoice.ReviewTicketId)
            ?? throw Denied();
        try
        {
            invoice.VerifyStoredTerms(archive, review);
        }
        catch (Exception error) when (error is InvalidOperationException or
            ArgumentException)
        {
            throw Denied();
        }

        // Include every invoice receipt for this contract, not merely
        // journal rows linked to the requested invoice. Otherwise a
        // completed but unjournaled receipt can be silently omitted.
        var journals = await db.InvoicePaymentAllocationJournal.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.ContractId == contractId &&
                x.Purpose == PaymentPurpose.InvoicePayment)
            .ToArrayAsync();
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            journals, payments, contractId);
        if (audit.Blockers.Any(x =>
            x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing"))
            throw Denied();

        var asOfVietnam = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
        IssuedInvoicePaymentSummary summary;
        try
        {
            summary = IssuedInvoicePaymentReconciler.Assess(
                invoice.Id, contractId, invoice.TotalAmount, invoice.DueDate,
                asOfVietnam,
                payments.Where(x => x.InvoiceId == invoice.Id).ToArray());
        }
        catch (Exception error) when (error is InvalidOperationException or
            ArgumentException or OverflowException)
        {
            throw Denied();
        }
        if (summary.UnresolvedOverpayment != 0m ||
            journals.Where(x => x.IssuedInvoiceId == invoice.Id)
                .Sum(x => x.Amount) != summary.CompletedPaymentsAmount)
            throw Denied();

        // A membership removal between the initial SELECT and the final
        // aggregation must also deny the result. Identity lease is held
        // for the enclosing transactional HTTP UnitOfWork.
        if (!await db.OperatorBuildingGrants.AsNoTracking().AnyAsync(grant =>
            grant.OperatorUserId == actor &&
            grant.BuildingId == authorizedContract.BuildingId &&
            grant.RevokedAtUtc == null &&
            grant.GrantedAtUtc <= DateTime.UtcNow &&
            (grant.ExpiresAtUtc == null ||
             grant.ExpiresAtUtc > DateTime.UtcNow)))
            throw Denied();

        return new SliceBScopedIssuedInvoiceBalanceRead(
            invoice.Id, contractId, authorizedContract.BuildingId,
            invoice.InvoiceNumber, invoice.DueDate, invoice.TotalAmount,
            summary.CompletedPaymentsAmount,
            summary.OutstandingBeforeAdjustments,
            summary.CompletedPaymentCount, summary.Status,
            IsObservedStagingLedger: true,
            IsOfficialDebtPosted: false,
            BankTransferIndependentlyVerified: false);
    }

    /// <summary>
    /// Phase 2G.3 contract-level OBSERVED debt portfolio on isolated staging.
    /// All invoices are obtained from the server-side immutable issued ledger
    /// with the live Contract -> Room -> Floor -> Building membership path.
    /// No caller-supplied total, rate, payment ID, tenant ID or building scope.
    /// It never posts debt, modifies payments, offsets deposits, or issues bills.
    /// </summary>
    public async Task<SliceBScopedContractIssuedDebtRead>
        GetIssuedDebtPortfolioAsync(Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var scoped = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        // Hold exact current contract/building/membership rows until commit.
        await LockInvoicePreviewScopeAsync(db, scoped, actor);

        var invoices = await db.IssuedInvoiceLedger.AsNoTracking()
            .Where(x => x.ContractId == contractId &&
                x.RoomId == scoped.RoomId &&
                x.BuildingId == scoped.BuildingId)
            .OrderBy(x => x.DueDate).ThenBy(x => x.Id)
            .Take(251).ToArrayAsync();
        // A summary must be complete; never silently truncate owed money.
        if (invoices.Length > 250)
            throw Denied();
        var invoiceIds = invoices.Select(x => x.Id).ToHashSet();
        var journals = await db.InvoicePaymentAllocationJournal.AsNoTracking()
            .Where(x => x.ContractId == contractId).ToArrayAsync();
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.ContractId == contractId &&
                x.Purpose == PaymentPurpose.InvoicePayment)
            .ToArrayAsync();

        // Fail closed for stale/cross-contract allocations and completed
        // payments without a matching immutable invoice and journal.
        if (journals.Any(x => !invoiceIds.Contains(x.IssuedInvoiceId)) ||
            payments.Any(x => x.Status == PaymentStatus.Completed &&
                (!x.InvoiceId.HasValue ||
                 !invoiceIds.Contains(x.InvoiceId.Value))))
            throw Denied();
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            journals, payments, contractId);
        if (audit.Blockers.Any(x =>
            x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing"))
            throw Denied();

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(now,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
        var details = new List<SliceBScopedIssuedInvoiceBalanceRead>(
            invoices.Length);
        foreach (var invoice in invoices)
        {
            var archive = await db.FinalInvoiceIssueEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == invoice.StagedEvidenceId)
                ?? throw Denied();
            var review = await db.InvoiceIssuanceReviews.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == invoice.ReviewTicketId)
                ?? throw Denied();
            IssuedInvoicePaymentSummary amount;
            try
            {
                invoice.VerifyStoredTerms(archive, review);
                amount = IssuedInvoicePaymentReconciler.Assess(
                    invoice.Id, contractId, invoice.TotalAmount, invoice.DueDate,
                    today, payments.Where(x => x.InvoiceId == invoice.Id).ToArray());
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                ArgumentException or OverflowException)
            {
                throw Denied();
            }

            if (amount.UnresolvedOverpayment != 0m ||
                journals.Where(x => x.IssuedInvoiceId == invoice.Id)
                    .Sum(x => x.Amount) != amount.CompletedPaymentsAmount)
                throw Denied();

            details.Add(new SliceBScopedIssuedInvoiceBalanceRead(
                invoice.Id, contractId, scoped.BuildingId,
                invoice.InvoiceNumber, invoice.DueDate, invoice.TotalAmount,
                amount.CompletedPaymentsAmount,
                amount.OutstandingBeforeAdjustments,
                amount.CompletedPaymentCount, amount.Status,
                IsObservedStagingLedger: true,
                IsOfficialDebtPosted: false,
                BankTransferIndependentlyVerified: false));
        }

        // The original, live authorization is still required at the end.
        // The SHARE lock above prevents grant revocation until commit.
        if (!await db.OperatorBuildingGrants.AsNoTracking().AnyAsync(grant =>
            grant.OperatorUserId == actor &&
            grant.BuildingId == scoped.BuildingId &&
            grant.RevokedAtUtc == null &&
            grant.GrantedAtUtc <= now &&
            (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now)))
            throw Denied();

        return new SliceBScopedContractIssuedDebtRead(
            contractId, scoped.BuildingId, details.Count,
            details.Sum(x => x.IssuedAmount),
            details.Sum(x => x.ObservedCompletedPaymentsAmount),
            details.Sum(x => x.ObservedOutstandingBeforeAdjustments),
            details.Count(x =>
                x.ObservedPaymentStatus == IssuedInvoicePaymentStatus.Overdue),
            details,
            IsOfficialDebtPosted: false,
            BankTransferIndependentlyVerified: false);
    }

    /// <summary>
    /// Full-month rent plus physically evidenced utility amounts only.
    /// Service fees are intentionally unknown, not implicitly VND 0.
    /// The HTTP action owns a REPEATABLE READ UnitOfWork and live Identity
    /// permission lease. This read never generates or issues an Invoice.
    /// </summary>
    public async Task<MonthlyInvoiceBasePreviewDto> GetMonthlyInvoicePreviewAsync(
        Guid contractId, int billingYear, int billingMonth)
    {
        if (contractId == Guid.Empty ||
            billingYear is < 1 or > 9999 || billingMonth is < 1 or > 12)
            throw Denied();
        var scoped = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        await LockInvoicePreviewScopeAsync(db, scoped, actor);
        var report = await _monthlyPreview.GetAsync(
            contractId, billingYear, billingMonth);
        if (report.ContractId != scoped.Id || report.RoomId != scoped.RoomId)
            throw Denied();
        return report;
    }

    private static async Task LockInvoicePreviewScopeAsync(
        BuildingManagementDbContext db,
        SliceBScopedContractRead scoped, Guid actor)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT contract."Id"
                  FROM "BuildingManagementContracts" contract
                  JOIN "BuildingManagementRooms" room
                    ON room."Id" = contract."RoomId"
                  JOIN "BuildingManagementFloors" floor
                    ON floor."Id" = room."FloorId"
                  JOIN "Buildings" building
                    ON building."Id" = floor."BuildingId"
                  JOIN "BuildingManagementOperatorBuildingGrants" membership
                    ON membership."BuildingId" = building."Id"
                 WHERE contract."Id" = @contract
                   AND building."Id" = @building
                   AND membership."OperatorUserId" = @actor
                   AND membership."RevokedAtUtc" IS NULL
                   AND membership."GrantedAtUtc" <= NOW()
                   AND (membership."ExpiresAtUtc" IS NULL OR
                        membership."ExpiresAtUtc" > NOW())
                   AND NOT contract."IsDeleted" AND NOT room."IsDeleted"
                   AND NOT floor."IsDeleted" AND NOT building."IsDeleted"
                 FOR SHARE OF contract, room, floor, building, membership
                """;
            foreach (var (key, value) in new[]
            {
                ("contract", scoped.Id),
                ("building", scoped.BuildingId),
                ("actor", actor)
            })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = key;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            if (await command.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }


    }

    private Guid RequireActor()
    {
        if (!_currentUser.IsAuthenticated || _currentUser.Id is not Guid id ||
            id == Guid.Empty)
            throw Denied();
        return id;
    }

    private static IQueryable<SliceBScopedContractRead> AuthorizedContracts(
        BuildingManagementDbContext db, Guid actor,
        Guid? onlyContractId = null)
    {
        var now = DateTime.UtcNow;
        return from contract in db.Contracts.AsNoTracking()
               join room in db.Rooms.AsNoTracking()
                   on contract.RoomId equals room.Id
               join floor in db.Floors.AsNoTracking()
                   on room.FloorId equals floor.Id
               join building in db.Buildings.AsNoTracking()
                   on floor.BuildingId equals building.Id
               where (!onlyContractId.HasValue ||
                       contract.Id == onlyContractId.Value) &&
                     !contract.IsDeleted && !room.IsDeleted &&
                     !floor.IsDeleted && !building.IsDeleted &&
                     db.OperatorBuildingGrants.Any(grant =>
                         grant.OperatorUserId == actor &&
                         grant.BuildingId == building.Id &&
                         grant.RevokedAtUtc == null &&
                         grant.GrantedAtUtc <= now &&
                         (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
               orderby contract.ContractNumber, contract.Id
               select new SliceBScopedContractRead(
                   contract.Id, contract.RoomId, building.Id,
                   contract.ContractNumber, contract.Status,
                   contract.MonthlyRent, contract.StartDate, contract.EndDate);
    }

    private static AbpAuthorizationException Denied()
        => new("No active building-scoped Contracts.Read authorization.");
}

/// <summary>
/// Read-only signed finance handover. Approval ID and checksum are archival
/// proof; there is NO bank verification, account number or tenant PII.
/// </summary>
public sealed record SliceBScopedSignedCloseoutRead(
    Guid ApprovalId, Guid ContractId, Guid MoveOutRequestId,
    Guid IssuedInvoiceId, DateTime ApprovedAtUtc,
    decimal VerifiedHeldAmount, decimal RefundAmount,
    decimal ForfeitAmount, RoomStatus RoomStatusAtCloseout,
    RoomStatus CurrentRoomStatus,
    string ApprovalSha256, bool RefundManuallyAttested,
    bool BankTransferIndependentlyVerified);

public sealed record SliceBScopedContractRead(
    Guid Id, Guid RoomId, Guid BuildingId, string ContractNumber,
    ContractStatus Status, decimal MonthlyRent, DateTime StartDate,
    DateTime? EndDate);

public sealed record SliceBScopedContractTenantLink(
    Guid TenantId, ContractTenantRole Role);


/// <summary>Move-out metadata only: no personal reasons or review notes.</summary>
public sealed record SliceBScopedMoveOutRequestRead(
    Guid Id, Guid ContractId, Guid RoomId, Guid BuildingId,
    MoveOutRequestStatus Status, DateOnly RequestedMoveOutDate,
    DateOnly? ApprovedMoveOutDate, DateTime CreationTime);


/// <summary>Checklist progress only; no unrelated tenant identity data.</summary>
public sealed record SliceBScopedMoveOutChecklistRead(
    Guid Id, Guid MoveOutRequestId, MoveOutChecklistTaskType Type,
    bool IsCompleted, DateTime? CompletedAt);


/// <summary>
/// Staging-only accounting review. Amounts are OBSERVED evidence, not
/// approved debt, held cash or refundable balances. No user PII or notes.
/// </summary>
public sealed record SliceBScopedMoveOutFinancialRead(
    Guid MoveOutRequestId,
    Guid ContractId,
    Guid? UtilityDraftInvoiceId,
    decimal? VerifiedBaseAmount,
    decimal? ObservedCompletedContractDepositReceiptsTotal,
    decimal? VerifiedIncomingDepositAllocationTotal,
    decimal? RecordedCompletedOutgoingDepositTotal,
    bool StagedInvoiceEvidenceCurrent,
    bool OriginalInvoiceSourcesVerified,
    bool IndependentInvoiceReviewApplicable,
    IReadOnlyList<string> InvoiceBlockers,
    IReadOnlyList<string> DepositBlockers,
    IReadOnlyList<string> MoveOutBlockers,
    bool CanIssueInvoice,
    bool CanRecognizeDebt,
    bool CanSettleDeposit,
    bool CanCompleteMoveOut);


/// <summary>
/// Minimal deposit receipt status, no bank ref, payment number or tenant PII.
/// </summary>
public sealed record SliceBScopedDepositPaymentRead(
    Guid Id, decimal Amount, PaymentStatus Status, DateTime? PaidAt,
    PaymentPurpose Purpose);

/// <summary>
/// Explicitly observed staging balance; does not constitute an approved
/// production debt posting, bank statement, or deposit offset.
/// No tenant PII, transfer references, or payment numbers are returned.
/// </summary>
public sealed record SliceBScopedIssuedInvoiceBalanceRead(
    Guid IssuedInvoiceId, Guid ContractId, Guid BuildingId,
    string InvoiceNumber, DateOnly DueDate, decimal IssuedAmount,
    decimal ObservedCompletedPaymentsAmount,
    decimal ObservedOutstandingBeforeAdjustments, int CompletedPaymentCount,
    IssuedInvoicePaymentStatus ObservedPaymentStatus,
    bool IsObservedStagingLedger, bool IsOfficialDebtPosted,
    bool BankTransferIndependentlyVerified);

/** The observed, validated issued-invoice debt portfolio for ONE contract. */
public sealed record SliceBScopedContractIssuedDebtRead(
    Guid ContractId, Guid BuildingId, int IssuedInvoiceCount,
    decimal ObservedIssuedAmount, decimal ObservedCompletedPaymentsAmount,
    decimal ObservedOutstandingBeforeAdjustments, int OverdueInvoiceCount,
    IReadOnlyList<SliceBScopedIssuedInvoiceBalanceRead> Invoices,
    bool IsOfficialDebtPosted, bool BankTransferIndependentlyVerified);
