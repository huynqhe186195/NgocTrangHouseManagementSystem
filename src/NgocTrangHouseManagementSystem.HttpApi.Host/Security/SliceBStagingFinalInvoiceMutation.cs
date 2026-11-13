using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using BuildingManagement.RoomChanges;
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
    private readonly ContractDepositOfficialOffsetReadAppService _offsetReader;
    private readonly MonthlyInvoiceBasePreviewAppService _monthlyPreview;
    private readonly IInvoiceDraftStorageAppService _utilityDrafts;
    // Optional isolated-test clock; live registrations retain System time.
    private readonly TimeProvider _clock;

    public SliceBStagingFinalInvoiceMutation(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser user,
        SliceAStagingIdentityCommitLease identity,
        FinalInvoiceIssuePreflightAppService preflight,
        FinalInvoiceIssueEvidenceDriftAppService drift,
        FinalInvoiceSourceProvenanceAppService provenance,
        ContractDepositLedgerReviewAppService depositAudit,
        ContractDepositOfficialOffsetReadAppService offsetReader,
        MonthlyInvoiceBasePreviewAppService monthlyPreview,
        IInvoiceDraftStorageAppService utilityDrafts,
        TimeProvider? clock = null)
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
        _offsetReader = offsetReader;
        _monthlyPreview = monthlyPreview;
        _utilityDrafts = utilityDrafts;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Monthly (non-move-out) recurring invoice CANDIDATE, staged only.
    /// Re-reads live utility/rent sources under the authenticated building
    /// fence and RepeatableRead UOW; freezes the original draft, rate and
    /// physical meter-source values into a durable checksum-protected row.
    /// This does NOT issue an invoice, recognize debt or accept payment.
    /// </summary>
    public Task<SliceBStagedMonthlyInvoiceCandidateRead>
        PrepareMonthlyCandidateAsync(
            Guid contractId, Guid draftId,
            MonthlyInvoiceFinancialTermsProposal terms)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (draftId == Guid.Empty || terms is null ||
                    terms.AuxiliaryFeeReviewerId != actor ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.RepeatableRead)
                    throw Denied();

                // Protect original meters, replacement closings, rates and
                // the saved draft until the entire staging UoW commits.
                await LockOriginalInvoiceSourcesAsync(db);
                await using (var command = Command(db, """
                    SELECT "Id" FROM "BuildingManagementInvoices"
                     WHERE "Id" = @draft AND "ContractId" = @contract
                       AND "RoomId" = @room AND "Status" = 0
                     FOR UPDATE
                    """))
                {
                    Add(command, "draft", draftId);
                    Add(command, "contract", contractId);
                    Add(command, "room", roomId);
                    if (await command.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                var saved = await _utilityDrafts.GetAsync(draftId);
                if (saved.ContractId != contractId ||
                    saved.RoomId != roomId ||
                    saved.Status != InvoiceStatus.Draft ||
                    saved.Items.Count != 2 ||
                    saved.ProvisionalUtilityTotal is null ||
                    !await _utilityDrafts.HasCurrentSourceSnapshotAsync(draftId))
                    throw Denied();

                var preview = await _monthlyPreview.GetAsync(
                    contractId, saved.BillingYear, saved.BillingMonth);
                var evaluated = MonthlyInvoiceFinancialTermsEvaluator.Assess(
                    preview, terms);
                if (evaluated.Blockers.Any(x =>
                        x != "MonthlyInvoiceIssuanceNotAuthorized") ||
                    evaluated.Blockers.Count != 1 ||
                    evaluated.VerifiedBaseAmount is null ||
                    evaluated.ReviewedAuxiliaryAmount is not decimal auxiliary ||
                    evaluated.ReviewedGrandTotal is not decimal total ||
                    evaluated.DocumentedDueDate is not DateOnly due)
                    throw Denied();

                var localToday = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                if (due < localToday ||
                    await db.MonthlyInvoiceCandidates.AsNoTracking().AnyAsync(
                        x => x.DraftInvoiceId == draftId ||
                        (x.RoomId == roomId &&
                         x.BillingYear == saved.BillingYear &&
                         x.BillingMonth == saved.BillingMonth)) ||
                    await db.IssuedInvoiceLedger.AsNoTracking().AnyAsync(
                        x => x.RoomId == roomId &&
                        x.BillingYear == saved.BillingYear &&
                        x.BillingMonth == saved.BillingMonth))
                    throw Denied();

                var originalLines = saved.Items.Select(line =>
                {
                    if (line.UtilityRateId is not Guid rateId ||
                        rateId == Guid.Empty ||
                        line.RateStatus != "SingleRateWholeMonth" ||
                        line.UnitPrice is not decimal price ||
                        line.ProvisionalAmount is not decimal amount ||
                        line.CoverageIssues.Count != 0 ||
                        line.Sources.Count == 0)
                        throw Denied();

                    return new MonthlyInvoiceCandidateUtilityLine(
                        line.UtilityType, rateId, line.Quantity, price,
                        amount, line.Sources.Select(source =>
                        {
                            if (!Enum.TryParse<MeterUsageSourceType>(
                                    source.SourceType, out var kind) ||
                                !Enum.IsDefined(kind))
                                throw Denied();
                            return new MonthlyInvoiceCandidateMeterSource(
                                source.SourceId, source.UtilityMeterId, kind,
                                source.RecordedDate, source.PreviousReading,
                                source.CurrentReading);
                        }).ToArray());
                }).ToArray();

                // Service charge references are reviewed evidence, NOT
                // independently verified billing rules. Even this durable
                // archive is never an official receivable or final bill.
                var evidence = new MonthlyInvoiceCandidateSnapshot(
                    draftId, contractId, roomId, building,
                    saved.BillingYear, saved.BillingMonth,
                    actor, DateTime.UtcNow, due,
                    terms.DueDatePolicyReference!,
                    preview.KnownFullMonthRentAmount,
                    preview.ProvisionalElectricityAmount!.Value,
                    preview.ProvisionalWaterAmount!.Value,
                    terms.AuxiliaryFeeDecision,
                    terms.AuxiliaryFeePolicyReference!,
                    terms.AuxiliaryCharges!.ToArray(),
                    auxiliary, total, originalLines);

                MonthlyInvoiceCandidateLedgerRecord archived;
                try
                {
                    archived = MonthlyInvoiceCandidateLedgerRecord.Capture(
                        Guid.NewGuid(), evidence);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    ArgumentException or OverflowException)
                {
                    throw Denied();
                }

                db.MonthlyInvoiceCandidates.Add(archived);
                return new SliceBStagedMonthlyInvoiceCandidateRead(
                    archived.Id, draftId, contractId,
                    archived.BillingYear, archived.BillingMonth,
                    archived.TotalAmount, archived.DueDate,
                    archived.FrozenEvidenceSha256,
                    IsOfficialReceivable: false);
            });

    /// <summary>
    /// Independently accept or reject the immutable monthly candidate.
    /// The reviewer MUST be a different building-scoped operator from
    /// the preparer; rejection is durable and cannot issue.
    /// </summary>
    public Task<SliceBStagedMonthlyReviewRead> ReviewMonthlyCandidateAsync(
        Guid contractId, Guid candidateId, bool accept, string reason)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (candidateId == Guid.Empty)
                    throw Denied();
                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementMonthlyInvoiceCandidates"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR SHARE
                    """))
                {
                    Add(cmd, "id", candidateId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                var archived = await db.MonthlyInvoiceCandidates
                    .AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidateId);
                if (archived is null ||
                    await db.MonthlyInvoiceReviews.AsNoTracking()
                        .AnyAsync(x => x.CandidateId == candidateId))
                    throw Denied();
                MonthlyInvoiceIndependentReviewRecord record;
                try
                {
                    record = MonthlyInvoiceIndependentReviewRecord.Capture(
                        Guid.NewGuid(), archived, actor,
                        DateTime.UtcNow, accept, reason);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                {
                    throw Denied();
                }
                db.MonthlyInvoiceReviews.Add(record);
                return new SliceBStagedMonthlyReviewRead(
                    record.Id, record.CandidateId, record.ContractId,
                    record.Accepted, IsOfficialReceivable: false);
            });

    /// <summary>
    /// Root-only, independently approved MONTHLY issuance on isolated
    /// staging. Fresh original rent/rates/meter evidence is REQUIRED here,
    /// not only at candidate preparation. The issued event is immutable
    /// and cross-ledger unique, but it is NOT an official posted debt.
    /// </summary>
    public Task<SliceBStagedMonthlyIssuedRead> IssueMonthlyAsync(
        Guid contractId, Guid candidateId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (candidateId == Guid.Empty ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.RepeatableRead)
                    throw Denied();
                await LockOriginalInvoiceSourcesAsync(db);

                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementMonthlyInvoiceCandidates"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR SHARE
                    """))
                {
                    Add(cmd, "id", candidateId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                var archived = await db.MonthlyInvoiceCandidates
                    .AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidateId);
                if (archived is null)
                    throw Denied();

                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementInvoices"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "Status" = 0
                     FOR UPDATE
                    """))
                {
                    Add(cmd, "id", archived.DraftInvoiceId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                var review = await db.MonthlyInvoiceReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.CandidateId == candidateId && x.Accepted);
                if (review is null || actor == review.ReviewedByUserId ||
                    await db.MonthlyIssuedInvoices.AsNoTracking().AnyAsync(x =>
                        x.CandidateId == candidateId ||
                        (x.RoomId == roomId &&
                         x.BillingYear == archived.BillingYear &&
                         x.BillingMonth == archived.BillingMonth)) ||
                    await db.IssuedInvoiceLedger.AsNoTracking().AnyAsync(x =>
                        x.RoomId == roomId &&
                        x.BillingYear == archived.BillingYear &&
                        x.BillingMonth == archived.BillingMonth))
                    throw Denied();

                try
                {
                    var frozen = archived.VerifyStoredEvidence();
                    review.VerifyAgainst(archived);
                    if (!await _utilityDrafts.HasCurrentSourceSnapshotAsync(
                            archived.DraftInvoiceId))
                        throw Denied();
                    var live = await _monthlyPreview.GetAsync(
                        contractId, archived.BillingYear, archived.BillingMonth);
                    if (live.ProvisionalRentAndUtilitiesAmount is null ||
                        live.KnownFullMonthRentAmount != frozen.RentAmount ||
                        live.ProvisionalElectricityAmount != frozen.ElectricityAmount ||
                        live.ProvisionalWaterAmount != frozen.WaterAmount ||
                        live.ProvisionalRentAndUtilitiesAmount != checked(
                            frozen.RentAmount + frozen.ElectricityAmount +
                            frozen.WaterAmount))
                        throw Denied();

                    var id = Guid.NewGuid();
                    var issued = MonthlyIssuedInvoiceRecord.Issue(
                        id, "NTM-" + id.ToString("N"), actor, DateTime.UtcNow,
                        archived, review);

                    // D03: every service line on the issued invoice must be
                    // priced by the original room-specific tariff version and
                    // by independently verified occupancy / meter evidence,
                    // then frozen. A superseded rate or an unverified headcount
                    // refuses the issuance rather than billing a guess.
                    await VerifyOfficialServicePricingAsync(db, archived, issued);

                    db.MonthlyIssuedInvoices.Add(issued);
                    return new SliceBStagedMonthlyIssuedRead(
                        issued.Id, issued.CandidateId, issued.ContractId,
                        issued.InvoiceNumber, issued.TotalAmount,
                        issued.DueDate, issued.IsOfficialReceivable);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                {
                    throw Denied();
                }
            });

    /// <summary>
    /// D03 gate: re-reads the ISSUED invoice's service evidence from the
    /// server-side sources only — the frozen room-service bindings, the
    /// complete scoped tariff history and the current contract residency —
    /// and refuses issuance when any line cannot be priced from an approved
    /// effective rate with independently verified quantity.
    /// </summary>
    private static async Task VerifyOfficialServicePricingAsync(
        BuildingManagementDbContext db,
        MonthlyInvoiceCandidateLedgerRecord archived,
        MonthlyIssuedInvoiceRecord issued)
    {
        var bindings = await db.RoomServiceInvoiceSourceBindings.AsNoTracking()
            .Where(x => x.MonthlyCandidateId == archived.Id).ToArrayAsync();

        var serviceIds = bindings.Select(x => x.ServiceId).Distinct().ToArray();
        var history = serviceIds.Length == 0
            ? Array.Empty<RoomScopedServiceTariffVersion>()
            : await db.RoomScopedServiceTariffVersions.AsNoTracking()
                .Where(x => x.RoomId == archived.RoomId &&
                            serviceIds.Contains(x.ServiceId)).ToArrayAsync();

        var residency = await db.Set<ContractTenant>().AsNoTracking()
            .Where(x => x.ContractId == archived.ContractId)
            .Select(x => new { x.TenantId, x.Role })
            .ToArrayAsync();

        OfficialServicePricingSchedule.Bind(
                issued, archived, bindings, history,
                residency.Select(x => new RoomChangeRosterMember(
                    x.TenantId, x.Role)).ToArray())
            .VerifySchedule(archived);
    }

    /// <summary>
    /// Root-only, original-proof anchored credit/debit proposal.
    /// A proposal alone NEVER changes the staged invoice balance.
    /// </summary>
    public Task<SliceBStagedMonthlyAdjustmentRead> ProposeMonthlyAdjustmentAsync(
        Guid contractId, Guid monthlyIssuedId,
        SliceBMonthlyAdjustmentInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (monthlyIssuedId == Guid.Empty || input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.RepeatableRead)
                    throw Denied();
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == monthlyIssuedId && x.ContractId == contractId &&
                        x.BuildingId == building && x.RoomId == roomId);
                if (issued is null)
                    throw Denied();
                var archive = await db.MonthlyInvoiceCandidates.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == issued.CandidateId);
                var review = await db.MonthlyInvoiceReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == issued.ReviewId);
                if (archive is null || review is null)
                    throw Denied();
                try { issued.VerifyAgainst(archive, review); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                // One physical bank/contract correction evidence may only
                // support one proposal per issued invoice, case-insensitive.
                var evidenceRef = input.SourceEvidenceReference?.Trim();
                if (string.IsNullOrWhiteSpace(evidenceRef) ||
                    await db.MonthlyInvoiceAdjustments.AsNoTracking().AnyAsync(x =>
                        x.IssuedInvoiceId == issued.Id &&
                        x.SourceEvidenceReference.ToLower() ==
                            evidenceRef.ToLowerInvariant()))
                    throw Denied();
                MonthlyInvoiceAdjustmentRecord proposal;
                try
                {
                    proposal = MonthlyInvoiceAdjustmentRecord.Propose(
                        Guid.NewGuid(), issued, actor, DateTime.UtcNow,
                        input.SignedDeltaAmount, input.Reason,
                        input.SourceEvidenceReference);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    ArgumentException or OverflowException)
                { throw Denied(); }
                db.MonthlyInvoiceAdjustments.Add(proposal);
                return new SliceBStagedMonthlyAdjustmentRead(
                    proposal.Id, issued.Id, proposal.SignedDeltaAmount,
                    proposal.IsCreditNote, proposal.IsDebitNote,
                    IsOfficialDebtPosted: false);
            });

    /// <summary>
    /// Second authenticated operator reviews proposal and original source.
    /// Amount is protected by the issued row lock through commit, the same
    /// lock as receipt posting; credit cannot erase captured payment funds.
    /// </summary>
    public Task<SliceBStagedMonthlyAdjustmentDecisionRead>
        ReviewMonthlyAdjustmentAsync(
            Guid contractId, Guid adjustmentId,
            SliceBMonthlyAdjustmentDecisionInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (adjustmentId == Guid.Empty || input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                var proposal = await db.MonthlyInvoiceAdjustments.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == adjustmentId &&
                        x.ContractId == contractId &&
                        x.BuildingId == building);
                if (proposal is null)
                    throw Denied();
                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR UPDATE
                    """))
                {
                    Add(cmd, "id", proposal.IssuedInvoiceId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleAsync(x => x.Id == proposal.IssuedInvoiceId);
                if (await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                    .AnyAsync(x => x.AdjustmentId == adjustmentId))
                    throw Denied();
                MonthlyInvoiceAdjustmentDecisionRecord decision;
                try
                {
                    decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
                        Guid.NewGuid(), proposal, issued, actor,
                        DateTime.UtcNow, input.Accept, input.Reason);
                    if (decision.Accepted)
                    {
                        var totalApproved = await db.MonthlyInvoiceAdjustmentDecisions
                            .AsNoTracking().Where(x =>
                                x.IssuedInvoiceId == issued.Id && x.Accepted)
                            .SumAsync(x => x.ApprovedSignedDeltaAmount);
                        var journalAmount = await db.MonthlyInvoicePaymentAllocations
                            .AsNoTracking().Where(x => x.IssuedInvoiceId == issued.Id)
                            .SumAsync(x => x.Amount);
                        var adjusted = checked(issued.TotalAmount +
                            totalApproved + decision.ApprovedSignedDeltaAmount);
                        if (adjusted <= 0m || adjusted < journalAmount)
                            throw Denied();
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                { throw Denied(); }
                db.MonthlyInvoiceAdjustmentDecisions.Add(decision);
                return new SliceBStagedMonthlyAdjustmentDecisionRead(
                    decision.Id, proposal.Id, issued.Id, decision.Accepted,
                    decision.ApprovedSignedDeltaAmount,
                    IsOfficialDebtPosted: false);
            });

    /// <summary>
    /// Manual bank evidence for one independently reviewed MONTHLY invoice.
    /// Original Payment and monthly allocation journal commit atomically.
    /// Identical HTTP retries return original IDs; contradictory bank refs
    /// are rejected. A bank attestation is NOT external bank verification.
    /// </summary>
    public Task<SliceBStagedMonthlyReceiptRead> RecordMonthlyReceiptAsync(
        Guid contractId, Guid monthlyIssuedId,
        SliceBFinalInvoiceManualReceiptInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (monthlyIssuedId == Guid.Empty || input is null ||
                    // Financial sum and insertion must share a SERIALIZABLE
                    // snapshot so concurrent credit approvals and receipts
                    // cannot both commit against stale outstanding balances.
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable ||
                    input.TenantId == Guid.Empty || input.Amount <= 0m ||
                    decimal.Truncate(input.Amount) != input.Amount ||
                    input.PaidAtUtc == default ||
                    input.PaidAtUtc.Kind != DateTimeKind.Utc ||
                    input.PaidAtUtc > _clock.GetUtcNow().UtcDateTime ||
                    string.IsNullOrWhiteSpace(input.TransactionReference) ||
                    input.TransactionReference.Trim().Length is < 8 or > 128 ||
                    string.IsNullOrWhiteSpace(input.BankEvidenceReference) ||
                    input.BankEvidenceReference.Trim().Length is < 8 or > 200)
                    throw Denied();

                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR UPDATE
                    """))
                {
                    Add(cmd, "id", monthlyIssuedId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == monthlyIssuedId);
                if (issued is null || issued.ContractId != contractId ||
                    issued.RoomId != roomId ||
                    issued.BuildingId != building)
                    throw Denied();

                // Tenant attribution and source lock cannot be delegated
                // to an untrusted tenant ID in the request payload.
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

                var paidAt = input.PaidAtUtc.AddTicks(
                    -(input.PaidAtUtc.Ticks % 10L));
                if (paidAt < issued.IssuedAtUtc)
                    throw Denied();
                var reference = input.TransactionReference.Trim();
                var proof = "BANK-PROOF: " + input.BankEvidenceReference.Trim();

                // Any payment purpose may already claim this bank transfer.
                // The normalized invoice-only unique index cannot catch an
                // older deposit whose reference differs in case/whitespace.
                // Reject such cross-purpose reuse before writing a receipt.
                var matches = await db.Payments.IgnoreQueryFilters()
                    .AsNoTracking().Where(x =>
                        x.TransactionReference != null &&
                        x.TransactionReference.Trim().ToLower() ==
                            reference.ToLowerInvariant())
                    .Take(2).ToArrayAsync();
                if (matches.Length > 1) throw Denied();
                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    if (existing.IsDeleted ||
                        existing.Purpose != PaymentPurpose.InvoicePayment ||
                        existing.Method != PaymentMethod.BankTransfer ||
                        existing.Channel != PaymentChannel.Manual ||
                        existing.Status != PaymentStatus.Completed ||
                        existing.ContractId != contractId ||
                        existing.InvoiceId != monthlyIssuedId ||
                        existing.TenantId != input.TenantId ||
                        existing.Amount != input.Amount ||
                        existing.PaidAt != paidAt ||
                        !StringComparer.Ordinal.Equals(existing.Notes, proof))
                        throw Denied();
                    var journal = await db.MonthlyInvoicePaymentAllocations
                        .AsNoTracking().SingleOrDefaultAsync(x =>
                            x.PaymentId == existing.Id);
                    if (journal is null ||
                        journal.IssuedInvoiceId != issued.Id ||
                        journal.ContractId != contractId)
                        throw Denied();
                    journal.VerifySource(existing);
                    return new SliceBStagedMonthlyReceiptRead(
                        existing.Id, journal.Id, issued.Id, existing.Amount,
                        IsOfficialReceivableSettled: false);
                }

                var already = await db.MonthlyInvoicePaymentAllocations
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == issued.Id)
                    .SumAsync(x => x.Amount);
                var proposals = await db.MonthlyInvoiceAdjustments.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == issued.Id).ToArrayAsync();
                var decisions = await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == issued.Id).ToArrayAsync();
                MonthlyInvoiceAdjustmentSummary adjusted;
                try
                {
                    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                        _clock.GetUtcNow().UtcDateTime,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                    adjusted = MonthlyInvoiceAdjustmentEvaluator.Assess(
                        issued, proposals, decisions, today, already);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                { throw Denied(); }
                var official = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Kind == OfficialInvoiceKind.Monthly &&
                        x.IssuedInvoiceId == issued.Id &&
                        x.ContractId == contractId);
                var hasOffset = await db.DebtOffsetPostings.AsNoTracking()
                    .AnyAsync(x => x.ContractId == contractId);
                if (hasOffset && official is null)
                    throw Denied();
                var internalApplied = official is null ? 0m :
                    await PostedInternalOffsetForInvoiceAsync(db,
                        contractId, official.Id);
                if (checked(already + input.Amount + internalApplied) >
                    adjusted.AdjustedAmount)
                    throw Denied();

                var receiptId = Guid.NewGuid();
                var receipt = new Payment(
                    receiptId, "MONTH-" + receiptId.ToString("N"),
                    input.TenantId, PaymentPurpose.InvoicePayment,
                    PaymentMethod.BankTransfer, PaymentChannel.Manual,
                    input.Amount, contractId: contractId,
                    invoiceId: monthlyIssuedId,
                    transactionReference: reference, notes: proof);
                receipt.Complete(paidAt, reference);
                db.Payments.Add(receipt);
                await db.SaveChangesAsync();
                var entry = db.Entry(receipt);
                if (entry.State != EntityState.Unchanged)
                    throw Denied();
                entry.State = EntityState.Detached;

                var allocation = MonthlyInvoicePaymentAllocationRecord.Capture(
                    Guid.NewGuid(), issued, receipt, actor, _clock.GetUtcNow().UtcDateTime);
                db.MonthlyInvoicePaymentAllocations.Add(allocation);
                return new SliceBStagedMonthlyReceiptRead(
                    receipt.Id, allocation.Id, issued.Id, receipt.Amount,
                    IsOfficialReceivableSettled: false);
            });

    /// <summary>
    /// Scoped read-side balance for an already-issued MONTHLY staging bill.
    /// Re-audits the original monthly archive, independent review, every
    /// original receipt (including soft-deleted), and its allocation journal.
    /// Historical status uses BOTH the payment date and journal date, in
    /// Asia/Ho_Chi_Minh; this is NOT posted debt or bank verification.
    /// </summary>
    public Task<SliceBStagedMonthlyBalanceRead> GetMonthlyBalanceAsync(
        Guid contractId, Guid monthlyIssuedId, DateOnly? asOfDate)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (monthlyIssuedId == Guid.Empty ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.RepeatableRead)
                    throw Denied();
                var now = DateTime.UtcNow;
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
                var today = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(now, tz));
                var asOf = asOfDate ?? today;
                if (asOf == default || asOf > today)
                    throw Denied();

                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == monthlyIssuedId &&
                        x.ContractId == contractId &&
                        x.RoomId == roomId &&
                        x.BuildingId == building);
                if (issued is null ||
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                        issued.IssuedAtUtc, tz)) > asOf)
                    throw Denied();
                var archive = await db.MonthlyInvoiceCandidates.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == issued.CandidateId);
                var review = await db.MonthlyInvoiceReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == issued.ReviewId);
                if (archive is null || review is null)
                    throw Denied();
                try { issued.VerifyAgainst(archive, review); }
                catch (Exception ex) when (ex is InvalidDataException or
                    ArgumentException or InvalidOperationException)
                { throw Denied(); }

                var journals = await db.MonthlyInvoicePaymentAllocations
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == issued.Id)
                    .ToArrayAsync();
                var receipts = await db.Payments.IgnoreQueryFilters()
                    .AsNoTracking().Where(x =>
                        x.InvoiceId == issued.Id &&
                        x.Purpose == PaymentPurpose.InvoicePayment)
                    .ToArrayAsync();
                if (journals.Length != receipts.Length ||
                    receipts.Select(x => x.Id).Distinct().Count() != receipts.Length ||
                    journals.Select(x => x.PaymentId).Distinct().Count() != journals.Length)
                    throw Denied();
                var byId = receipts.ToDictionary(x => x.Id);
                try
                {
                    foreach (var journal in journals)
                    {
                        if (!byId.TryGetValue(journal.PaymentId, out var payment))
                            throw Denied();
                        journal.VerifySource(payment);
                    }
                    var eligiblePayments = journals
                        .Where(x => DateOnly.FromDateTime(
                            TimeZoneInfo.ConvertTimeFromUtc(
                                x.RecordedAtUtc, tz)) <= asOf)
                        .Select(x => byId[x.PaymentId]).ToArray();
                    var original = IssuedInvoicePaymentReconciler.Assess(
                        issued.Id, issued.ContractId, issued.TotalAmount,
                        issued.DueDate, asOf, eligiblePayments);
                    var proposals = await db.MonthlyInvoiceAdjustments
                        .AsNoTracking().Where(x => x.IssuedInvoiceId == issued.Id)
                        .ToArrayAsync();
                    var decisions = await db.MonthlyInvoiceAdjustmentDecisions
                        .AsNoTracking().Where(x => x.IssuedInvoiceId == issued.Id)
                        .ToArrayAsync();
                    var adjustments = MonthlyInvoiceAdjustmentEvaluator.Assess(
                        issued, proposals, decisions, asOf,
                        original.CompletedPaymentsAmount);
                    var corrected = IssuedInvoicePaymentReconciler.Assess(
                        issued.Id, issued.ContractId, adjustments.AdjustedAmount,
                        issued.DueDate, asOf, eligiblePayments);
                    if (corrected.HasUnresolvedOverpayment)
                        throw Denied();
                    return new SliceBStagedMonthlyBalanceRead(
                        issued.Id, contractId, building,
                        issued.InvoiceNumber, issued.DueDate, asOf,
                        original.IssuedAmount, corrected.CompletedPaymentsAmount,
                        original.OutstandingBeforeAdjustments,
                        corrected.CompletedPaymentCount, corrected.Status,
                        IsOfficialDebtPosted: false,
                        BankTransferIndependentlyVerified: false,
                        adjustments.ApprovedSignedAdjustment,
                        adjustments.AdjustedAmount,
                        corrected.OutstandingBeforeAdjustments,
                        adjustments.ApprovedAdjustmentCount);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                {
                    throw Denied();
                }
            });

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

                // The invoice row was locked FOR UPDATE above and the
                // tenant link was validated against the current contract.
                // A network retry of the EXACT same bank evidence must
                // return the immutable original receipt/journal instead
                // of charging the tenant a second time. The global unique
                // TransactionReference index remains the final DB fence.
                // PostgreSQL timestamptz has microsecond precision,
                // while .NET DateTime carries 100-nanosecond ticks.
                // Canonicalize BEFORE both persistence and replay checks
                // or an unchanged request can be mistaken for a new bank
                // payment after EF materializes the stored receipt.
                var normalizedPaidAtUtc = input.PaidAtUtc.AddTicks(
                    -(input.PaidAtUtc.Ticks % 10L));
                var normalizedReference = input.TransactionReference.Trim();
                var normalizedProof = "BANK-PROOF: " +
                    input.BankEvidenceReference.Trim();
                var matchingPayments = await db.Payments
                    .IgnoreQueryFilters().AsNoTracking()
                    .Where(p => p.TransactionReference != null &&
                        p.TransactionReference.Trim().ToLower() ==
                            normalizedReference.ToLowerInvariant())
                    .Take(2).ToArrayAsync();
                if (matchingPayments.Length > 1) throw Denied();
                // A matching reservation/contract deposit can NEVER also be
                // treated as a final invoice receipt, even if case differs.
                var alreadyRecorded = matchingPayments.SingleOrDefault();
                if (alreadyRecorded is not null)
                {
                    if (alreadyRecorded.IsDeleted ||
                        alreadyRecorded.Purpose != PaymentPurpose.InvoicePayment ||
                        alreadyRecorded.Method != PaymentMethod.BankTransfer ||
                        alreadyRecorded.Channel != PaymentChannel.Manual ||
                        alreadyRecorded.Status != PaymentStatus.Completed ||
                        alreadyRecorded.TenantId != input.TenantId ||
                        alreadyRecorded.ContractId != contractId ||
                        alreadyRecorded.InvoiceId != issuedInvoiceId ||
                        alreadyRecorded.Amount != input.Amount ||
                        alreadyRecorded.PaidAt != normalizedPaidAtUtc ||
                        !StringComparer.Ordinal.Equals(
                            alreadyRecorded.Notes, normalizedProof) ||
                        !StringComparer.OrdinalIgnoreCase.Equals(
                            alreadyRecorded.TransactionReference?.Trim(),
                            normalizedReference))
                        throw Denied();

                    var recordedJournal = await db.InvoicePaymentAllocationJournal
                        .AsNoTracking().SingleOrDefaultAsync(x =>
                            x.PaymentId == alreadyRecorded.Id);
                    if (recordedJournal is null ||
                        recordedJournal.IssuedInvoiceId != issuedInvoiceId ||
                        recordedJournal.ContractId != contractId)
                        throw Denied();

                    try
                    {
                        recordedJournal.VerifySourceReceipt(alreadyRecorded);
                    }
                    catch (InvalidOperationException)
                    {
                        throw Denied();
                    }
                    return new SliceBStagedInvoiceReceiptRead(
                        alreadyRecorded.Id, recordedJournal.Id, ledger.Id,
                        alreadyRecorded.Amount);
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
                    transactionReference: normalizedReference,
                    notes: normalizedProof);
                receipt.Complete(normalizedPaidAtUtc, normalizedReference);
                db.Payments.Add(receipt);
                // FK needs its source receipt before immutable journal
                // INSERT; both SaveChanges occur in SAME enclosing UoW.
                await db.SaveChangesAsync();
                // In ABP, the final scoped write fence can touch tracked
                // audited entity metadata again at commit. Once journaled,
                // the source Payment is deliberately immutable in SQL.
                // Detach ONLY the already-saved, unchanged receipt before
                // staging its immutable allocation, preserving the same
                // transaction while preventing a later metadata UPDATE.
                var receiptEntry = db.Entry(receipt);
                if (receiptEntry.State != EntityState.Unchanged)
                    throw Denied();
                receiptEntry.State = EntityState.Detached;

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
    /// Phase 2G.4: the OWNER explicitly approves an auditable deposit
    /// refund/forfeit plan. When OI-04 has posted, the exact signed
    /// internal credit is deducted once before approving residual refund.
    /// This action NEVER transfers money or ends the contract.
    /// </summary>
    public Task<SliceBDepositSettlementDecisionRead> ApproveDepositSettlementAsync(
        Guid contractId, Guid requestId, SliceBDepositSettlementApprovalInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                var request = await LockRequestAsync(
                    db, requestId, contractId, roomId);
                var contract = await db.Contracts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == contractId &&
                        x.RoomId == roomId && !x.IsDeleted);
                if (contract is null || contract.Status != ContractStatus.Active)
                    throw Denied();

                var invoices = await db.IssuedInvoiceLedger.AsNoTracking()
                    .Where(x => x.ContractId == contractId &&
                        x.RoomId == roomId).ToArrayAsync();
                var final = invoices.SingleOrDefault(x =>
                    x.MoveOutRequestId == requestId &&
                    x.BuildingId == building);
                if (final is null)
                    throw Denied();
                await using (var cmd = Command(db, """
                    SELECT "Id" FROM "BuildingManagementIssuedInvoiceLedger"
                     WHERE "Id" = @id AND "ContractId" = @contract
                       AND "RoomId" = @room AND "BuildingId" = @building
                     FOR SHARE
                    """))
                {
                    Add(cmd, "id", final.Id);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                try
                {
                    var archive = await db.FinalInvoiceIssueEvidence.AsNoTracking()
                        .SingleAsync(x => x.Id == final.StagedEvidenceId);
                    var review = await db.InvoiceIssuanceReviews.AsNoTracking()
                        .SingleAsync(x => x.Id == final.ReviewTicketId);
                    final.VerifyStoredTerms(archive, review);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                // Official debt must cover every issued invoice, including
                // recurring monthly bills. Partial/staged manual evidence
                // cannot be treated as payment of a receivable.
                var postings = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .Where(x => x.ContractId == contractId &&
                        x.RoomId == roomId &&
                        x.BuildingId == building).ToArrayAsync();
                var monthlyCount = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .CountAsync(x => x.ContractId == contractId &&
                        x.RoomId == roomId);
                if (postings.Length == 0 ||
                    postings.Length != invoices.Length + monthlyCount ||
                    !postings.Any(x => x.Kind == OfficialInvoiceKind.Final &&
                        x.IssuedInvoiceId == final.Id))
                    throw Denied();
                var asOf = OfficialAsOf(null);
                foreach (var posted in postings)
                {
                    var balance = await AssessOfficialBalanceAsync(
                        db, posted, asOf);
                    if (balance.OutstandingAmount != 0m ||
                        balance.PendingOrRejectedPaymentCount != 0)
                        throw Denied();
                }

                await RequireNoBlockedDebtDisputeAsync(db, contractId);
                var depositAudit = await _depositAudit.GetAsync(requestId);
                if (depositAudit.VerifiedIncomingAllocationTotal is not decimal held ||
                    held <= 0m || held != contract.DepositAmount ||
                    depositAudit.RecordedCompletedOutgoingTotal != 0m ||
                    depositAudit.Blockers.Any(x => x is not
                        "FinalInvoiceDebtNotReconciled" and
                        not "DepositDeductionPolicyNotApproved" and
                        not "RefundAuthorizationAndHandoverMissing"))
                    throw Denied();
                var postedOffset = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building);
                ContractDepositPostOffsetCloseoutReadiness? offsetReadiness = null;
                if (postedOffset is not null)
                {
                    var signedApproval = await db.DebtOffsetApprovals.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == postedOffset.ApprovalId)
                        ?? throw Denied();
                    try { postedOffset.VerifyStoredPosting(signedApproval); }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    offsetReadiness = await AssessPostOffsetInsideScopeAsync(
                        db, contractId, requestId, building, roomId, null);
                    if (!offsetReadiness.CanApproveDepositDisposition ||
                        offsetReadiness.PostingId != postedOffset.Id)
                        throw Denied();
                }
                ContractDepositSettlementDecisionRecord candidate;
                try
                {
                    candidate = ContractDepositSettlementDecisionRecord.Approve(
                        Guid.NewGuid(), contractId, requestId, final.Id,
                        building, actor, DateTime.UtcNow, final.IssuedAtUtc,
                        held, 0m, input.ForfeitAmount,
                        input.DamageReason, input.DamageEvidenceReference,
                        input.DepositPolicyReference,
                        postedOffset?.DebitHeldDepositVnd ?? 0m);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    ArgumentException or OverflowException)
                { throw Denied(); }
                var previous = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId ||
                        x.MoveOutRequestId == requestId);
                if (previous is not null)
                {
                    try
                    {
                        previous.VerifyStoredDecision();
                        previous.RequireForCloseout(
                            contractId, requestId, final.Id, building, held,
                            input.ForfeitAmount, input.DamageReason,
                            input.DamageEvidenceReference,
                            input.DepositPolicyReference,
                            postedOffset?.DebitHeldDepositVnd ?? 0m);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    return DepositDecisionRead(previous);
                }
                if (await db.DepositCloseoutApprovals.AsNoTracking()
                    .AnyAsync(x => x.ContractId == contractId ||
                        x.MoveOutRequestId == requestId))
                    throw Denied();
                db.DepositSettlementDecisions.Add(candidate);
                return DepositDecisionRead(candidate);
            });

    public Task<SliceBDepositSettlementDecisionRead> GetDepositSettlementAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (requestId == Guid.Empty) throw Denied();
                var decision = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                try { decision.VerifyStoredDecision(); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                var completed = await db.DepositCloseoutApprovals.AsNoTracking()
                    .AnyAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.RefundSettlementId.HasValue);
                return DepositDecisionRead(decision, completed);
            }, permitEndedContract: true);

    private static SliceBDepositSettlementDecisionRead DepositDecisionRead(
        ContractDepositSettlementDecisionRecord decision,
        bool refundExecuted = false)
        => new(decision.Id, decision.ContractId, decision.MoveOutRequestId,
            decision.FinalIssuedInvoiceId, decision.VerifiedHeldAmount,
            decision.ApprovedForfeitAmount, decision.ApprovedRefundAmount,
            decision.ApprovedDebtOffsetAmount, decision.ApprovedAtUtc,
            IsRefundExecuted: refundExecuted, IsAutomaticDebtOffset: false);



    /// <summary>
    /// A disputed official receivable is frozen out of deposit offsets.
    /// No application or client Boolean may certify it as legitimate.
    /// </summary>
    public Task<Guid> OpenDepositDebtDisputeAsync(
        Guid contractId, Guid requestId, SliceBDebtDisputeInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (input is null || input.PostingId == Guid.Empty ||
                    !ValidProof(input.Reason) || input.Reason!.Trim().Length < 12 ||
                    !ValidProof(input.EvidenceReference) ||
                    input.EvidenceReference!.Trim().Length < 12 ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                if (!await db.OfficialInvoiceReceivables.AsNoTracking()
                    .AnyAsync(x => x.Id == input.PostingId &&
                        x.ContractId == contractId && x.BuildingId == building) ||
                    await db.DebtOffsetPostings.AsNoTracking()
                    .AnyAsync(x => x.ContractId == contractId))
                    throw Denied();
                var id = Guid.NewGuid();
                await using var cmd=Command(db, """
                    INSERT INTO "BuildingManagementContractDepositDebtDisputes"
                      ("Id","ContractId","MoveOutRequestId","BuildingId",
                       "OfficialPostingId","OpenedByUserId","OpenedAtUtc",
                       "Reason","EvidenceReference")
                    VALUES (@id,@contract,@request,@building,@posting,
                            @actor,clock_timestamp(),@reason,@evidence)
                    RETURNING "Id"
                    """);
                AddValue(cmd,"id",id); AddValue(cmd,"contract",contractId);
                AddValue(cmd,"request",requestId); AddValue(cmd,"building",building);
                AddValue(cmd,"posting",input.PostingId); AddValue(cmd,"actor",actor);
                // PostgreSQL owns the signed audit instant: trigger checks
                // against clock_timestamp(); avoid .NET/PG clock skew.
                AddValue(cmd,"reason",input.Reason.Trim());
                AddValue(cmd,"evidence",input.EvidenceReference.Trim());
                return await cmd.ExecuteScalarAsync() is Guid created
                    ? created : throw Denied();
            });

    /// <summary>
    /// Owner accepts an independently supported charge or rejects it.
    /// Rejection keeps offsets closed until the underlying invoice debt is
    /// separately corrected; a resolution is not itself a payment.
    /// </summary>
    public Task<Guid> ResolveDepositDebtDisputeAsync(
        Guid contractId, Guid requestId, Guid disputeId,
        SliceBDebtDisputeResolutionInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (disputeId == Guid.Empty || input is null ||
                    !ValidProof(input.SignedOwnerResolutionReference) ||
                    input.SignedOwnerResolutionReference!.Trim().Length < 12 ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                await using(var find=Command(db, """
                    SELECT "Id" FROM "BuildingManagementContractDepositDebtDisputes"
                     WHERE "Id"=@id AND "ContractId"=@contract
                       AND "MoveOutRequestId"=@request AND "BuildingId"=@building
                     FOR SHARE
                    """))
                {
                    AddValue(find,"id",disputeId); AddValue(find,"contract",contractId);
                    AddValue(find,"request",requestId); AddValue(find,"building",building);
                    if (await find.ExecuteScalarAsync() is not Guid) throw Denied();
                }
                var id=Guid.NewGuid();
                await using var cmd=Command(db, """
                    INSERT INTO "BuildingManagementContractDepositDebtDisputeResolutions"
                      ("Id","DisputeId","ContractId","OwnerId","ResolvedAtUtc",
                       "DebtAccepted","SignedOwnerResolutionReference")
                    VALUES (@id,@dispute,@contract,@owner,clock_timestamp(),@accepted,@evidence)
                    RETURNING "Id"
                    """);
                AddValue(cmd,"id",id); AddValue(cmd,"dispute",disputeId);
                AddValue(cmd,"contract",contractId); AddValue(cmd,"owner",actor);
                // PostgreSQL owns the signed audit instant: trigger checks
                // against clock_timestamp(); avoid .NET/PG clock skew.
                AddValue(cmd,"accepted",input.DebtAccepted);
                AddValue(cmd,"evidence",input.SignedOwnerResolutionReference.Trim());
                return await cmd.ExecuteScalarAsync() is Guid created
                    ? created : throw Denied();
            });

    public Task<SliceBDebtDisputeStatusRead> GetDepositDebtDisputeStatusAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                await using var cmd=Command(db, """
                    SELECT
                      count(*) FILTER (WHERE r."Id" IS NULL),
                      count(*) FILTER (WHERE r."Id" IS NOT NULL AND
                                           r."DebtAccepted"=FALSE),
                      count(*) FILTER (WHERE r."DebtAccepted"=TRUE)
                    FROM "BuildingManagementContractDepositDebtDisputes" d
                    LEFT JOIN "BuildingManagementContractDepositDebtDisputeResolutions" r
                      ON r."DisputeId"=d."Id"
                    WHERE d."ContractId"=@contract AND
                          d."MoveOutRequestId"=@request AND
                          d."BuildingId"=@building
                    """);
                AddValue(cmd,"contract",contractId); AddValue(cmd,"request",requestId);
                AddValue(cmd,"building",building);
                await using var reader=await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw Denied();
                var open=reader.GetInt64(0);var rejected=reader.GetInt64(1);
                var accepted=reader.GetInt64(2);
                return new SliceBDebtDisputeStatusRead(
                    contractId,requestId,open,rejected,accepted,
                    open==0 && rejected==0);
            });

    private static async Task RequireNoBlockedDebtDisputeAsync(
        BuildingManagementDbContext db, Guid contractId)
    {
        await using var cmd=Command(db, """
            SELECT EXISTS(
              SELECT 1 FROM "BuildingManagementContractDepositDebtDisputes" d
              LEFT JOIN "BuildingManagementContractDepositDebtDisputeResolutions" r
                ON r."DisputeId"=d."Id"
              WHERE d."ContractId"=@contract AND
                (r."Id" IS NULL OR r."DebtAccepted"=FALSE))
            """);
        AddValue(cmd,"contract",contractId);
        if (await cmd.ExecuteScalarAsync() is true) throw Denied();
    }

    /// <summary>
    /// A signed Owner payee instruction must exist BEFORE the reported bank
    /// transfer. Matching the immutable decision scope and approved amount
    /// prevents a second or stale instruction from authorizing other funds.
    /// The row share-lock is held through the surrounding serializable UoW.
    /// </summary>
    private static async Task RequireApprovedRefundDestinationForPaymentAsync(
        BuildingManagementDbContext db,
        ContractDepositSettlementDecisionRecord decision,
        DateTime paidAtUtc)
    {
        if (paidAtUtc.Kind != DateTimeKind.Utc ||
            decision.ApprovedRefundAmount <= 0m)
            throw Denied();
        await using var cmd = Command(db, """
            SELECT "ApprovedAtUtc"
              FROM "BuildingManagementContractDepositRefundInstructions"
             WHERE "ContractId"=@contract AND "MoveOutRequestId"=@request
               AND "DecisionId"=@decision AND "BuildingId"=@building
               AND "OwnerId"=@owner AND "ApprovedRefundAmountVnd"=@amount
             FOR SHARE
            """);
        AddValue(cmd, "contract", decision.ContractId);
        AddValue(cmd, "request", decision.MoveOutRequestId);
        AddValue(cmd, "decision", decision.Id);
        AddValue(cmd, "building", decision.BuildingId);
        AddValue(cmd, "owner", decision.ApprovedByUserId);
        AddValue(cmd, "amount", decision.ApprovedRefundAmount);
        if (await cmd.ExecuteScalarAsync() is not DateTime approvedAtUtc ||
            approvedAtUtc.Kind != DateTimeKind.Utc ||
            paidAtUtc < approvedAtUtc)
            throw Denied();
    }

    /// <summary>
    /// Owner signs the destination and a Vietnam holiday-calendar reference.
    /// This is a bank instruction ONLY; it never initiates a transfer.
    /// </summary>
    public Task<Guid> ApproveRefundDestinationAsync(
        Guid contractId, Guid requestId, SliceBRefundDestinationApprovalInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable ||
                    !ContractDepositRefundOperationsPolicy.ValidRecipient(
                        input.BeneficiaryName, input.BankName,
                        input.AccountNumber, input.SignedDestinationReference) ||
                    !ValidProof(input.HolidayCalendarReference) ||
                    input.StatutoryHolidays is null ||
                    input.StatutoryHolidays.Count > 80 ||
                    input.StatutoryHolidays.Distinct().Count() !=
                        input.StatutoryHolidays.Count)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                var signed = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId && x.BuildingId == building)
                    ?? throw Denied();
                try { signed.VerifyStoredDecision(); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                if (signed.ApprovedByUserId != actor ||
                    signed.ApprovedRefundAmount <= 0m)
                    throw Denied();
                var approved = DateTime.UtcNow;
                // The refund SLA starts at approved final reconciliation,
                // NOT at a later recipient-instruction time. A delayed Owner
                // instruction cannot move the contractual deadline forward.
                var vnDate = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(signed.ApprovedAtUtc,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                if (input.StatutoryHolidays.Any(x =>
                    x < vnDate || x > vnDate.AddDays(30)))
                    throw Denied();
                var due = ContractDepositRefundOperationsPolicy.DueOn(
                    vnDate, input.StatutoryHolidays);
                // Preserve the exact Owner-approved holiday set; the DB
                // independently recomputes the seven-business-day deadline.
                if (due > vnDate.AddDays(30))
                    throw Denied();
                var holidaysJson = JsonSerializer.Serialize(
                    input.StatutoryHolidays.OrderBy(x => x));
                var name = input.BeneficiaryName!.Trim();
                var bank = input.BankName!.Trim();
                var account = input.AccountNumber!.Trim();
                var signedRef = input.SignedDestinationReference!.Trim();
                var calendar = input.HolidayCalendarReference!.Trim();
                await using (var insert = Command(db, """
                    INSERT INTO "BuildingManagementContractDepositRefundInstructions"
                      ("Id","ContractId","MoveOutRequestId","DecisionId",
                       "BuildingId","OwnerId","ApprovedAtUtc","ApprovedRefundAmountVnd",
                       "PayeeName","PayeeBank","PayeeAccount",
                       "SignedDestinationReference","HolidayCalendarReference",
                        "StatutoryHolidaysJson","DueOn")
                    VALUES (@id,@contract,@request,@decision,@building,@owner,
                      @at,@amount,@name,@bank,@account,@signed,@calendar,@holidays,@due)
                    ON CONFLICT ("ContractId") DO NOTHING
                    RETURNING "Id"
                    """))
                {
                    var id = Guid.NewGuid();
                    AddValue(insert,"id",id);
                    AddValue(insert,"contract",contractId);
                    AddValue(insert,"request",requestId);
                    AddValue(insert,"decision",signed.Id);
                    AddValue(insert,"building",building);
                    AddValue(insert,"owner",actor);
                    AddValue(insert,"at",approved);
                    AddValue(insert,"amount",signed.ApprovedRefundAmount);
                    AddValue(insert,"name",name);
                    AddValue(insert,"bank",bank);
                    AddValue(insert,"account",account);
                    AddValue(insert,"signed",signedRef);
                    AddValue(insert,"calendar",calendar);
                    AddValue(insert,"holidays",holidaysJson);
                    AddValue(insert,"due",due);
                    if (await insert.ExecuteScalarAsync() is Guid created)
                        return created;
                }
                // Idempotence is allowed ONLY for the same previously signed
                // destination and calendar. Never silently redirect money.
                await using var replay = Command(db, """
                    SELECT "Id" FROM "BuildingManagementContractDepositRefundInstructions"
                     WHERE "ContractId"=@contract AND "MoveOutRequestId"=@request
                       AND "DecisionId"=@decision AND "OwnerId"=@owner
                       AND "ApprovedRefundAmountVnd"=@amount
                       AND "PayeeName"=@name AND "PayeeBank"=@bank
                       AND "PayeeAccount"=@account
                       AND "SignedDestinationReference"=@signed
                       AND "HolidayCalendarReference"=@calendar
                        AND "StatutoryHolidaysJson"=@holidays
                    """);
                AddValue(replay,"contract",contractId);
                AddValue(replay,"request",requestId);
                AddValue(replay,"decision",signed.Id);
                AddValue(replay,"owner",actor);
                AddValue(replay,"amount",signed.ApprovedRefundAmount);
                AddValue(replay,"name",name);
                AddValue(replay,"bank",bank);
                AddValue(replay,"account",account);
                AddValue(replay,"signed",signedRef);
                AddValue(replay,"calendar",calendar);
                AddValue(replay,"holidays",holidaysJson);
                AddValue(replay,"due",due);
                return await replay.ExecuteScalarAsync() is Guid found
                    ? found : throw Denied();
            });

    /// <summary>
    /// Records a failed bank transfer as a distinct immutable claim; failed
    /// references cannot later be presented as independently paid refunds.
    /// </summary>
    public Task<Guid> RecordRefundTransferFailureAsync(
        Guid contractId, Guid requestId, SliceBRefundTransferFailureInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable ||
                    !ValidProof(input.BankReference) ||
                    !ValidProof(input.Reason) ||
                    !ValidProof(input.BankFailureEvidenceReference))
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                Guid instructionId;
                await using (var find = Command(db, """
                    SELECT "Id" FROM "BuildingManagementContractDepositRefundInstructions"
                     WHERE "ContractId"=@contract AND "MoveOutRequestId"=@request
                       AND "BuildingId"=@building AND "OwnerId"=@owner FOR SHARE
                    """))
                {
                    AddValue(find,"contract",contractId);
                    AddValue(find,"request",requestId);
                    AddValue(find,"building",building);
                    AddValue(find,"owner",actor);
                    instructionId = await find.ExecuteScalarAsync() is Guid id
                        ? id : throw Denied();
                }
                var eventId = Guid.NewGuid();
                await using var insert = Command(db, """
                    INSERT INTO "BuildingManagementContractDepositRefundFailedTransfers"
                      ("Id","InstructionId","ContractId","OwnerId",
                       "Reference","FailedAtUtc","Reason","BankEvidenceReference")
                    VALUES (@id,@instruction,@contract,@owner,@bank,@at,@reason,@evidence)
                    RETURNING "Id"
                    """);
                AddValue(insert,"id",eventId);
                AddValue(insert,"instruction",instructionId);
                AddValue(insert,"contract",contractId);
                AddValue(insert,"owner",actor);
                AddValue(insert,"bank",input.BankReference!.Trim());
                AddValue(insert,"at",DateTime.UtcNow);
                AddValue(insert,"reason",input.Reason!.Trim());
                AddValue(insert,"evidence",input.BankFailureEvidenceReference!.Trim());
                return await insert.ExecuteScalarAsync() is Guid inserted
                    ? inserted : throw Denied();
            });

    /// <summary>Read-only status. Never expose the beneficiary bank account.</summary>
    public Task<SliceBRefundOperatingStatusRead> GetRefundOperatingStatusAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var decision = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId && x.BuildingId == building)
                    ?? throw Denied();
                try { decision.VerifyStoredDecision(); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                DateOnly? due = null;
                await using (var query = Command(db, """
                    SELECT "DueOn" FROM "BuildingManagementContractDepositRefundInstructions"
                     WHERE "ContractId"=@contract AND "MoveOutRequestId"=@request
                       AND "DecisionId"=@decision
                    """))
                {
                    AddValue(query,"contract",contractId);
                    AddValue(query,"request",requestId);
                    AddValue(query,"decision",decision.Id);
                    if (await query.ExecuteScalarAsync() is DateOnly d)
                        due = d;
                }
                long failed;
                await using (var query = Command(db, """
                    SELECT count(*) FROM "BuildingManagementContractDepositRefundFailedTransfers"
                     WHERE "ContractId"=@contract
                    """))
                {
                    AddValue(query,"contract",contractId);
                    failed = Convert.ToInt64(await query.ExecuteScalarAsync());
                }
                // A historical review alone is not proof of an authorized
                // refund. Only the matching Owner instruction, amount and
                // payment timestamp may yield RefundVerified.
                bool verified;
                await using (var check = Command(db, """
                    SELECT EXISTS(
                      SELECT 1
                        FROM "BuildingManagementContractDepositRefundBankReviews" r
                        JOIN "BuildingManagementContractDepositRefundInstructions" i
                          ON i."ContractId"=r."ContractId"
                         AND i."MoveOutRequestId"=r."MoveOutRequestId"
                         AND i."DecisionId"=r."DepositDecisionId"
                       WHERE r."ContractId"=@contract
                         AND r."MoveOutRequestId"=@request
                         AND r."DepositDecisionId"=@decision
                         AND r."VerifiedRefundAmountVnd"=@amount
                         AND i."ApprovedRefundAmountVnd"=@amount
                         AND r."PaidAtUtc">=i."ApprovedAtUtc")
                    """))
                {
                    AddValue(check,"contract",contractId);
                    AddValue(check,"request",requestId);
                    AddValue(check,"decision",decision.Id);
                    AddValue(check,"amount",decision.ApprovedRefundAmount);
                    verified = await check.ExecuteScalarAsync() is true;
                }
                var today = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                var status = ContractDepositRefundOperationsPolicy.Status(
                    decision.ApprovedRefundAmount, due.HasValue, verified,
                    failed > 0, due, today);
                return new SliceBRefundOperatingStatusRead(
                    contractId, requestId, decision.ApprovedRefundAmount,
                    due, status, failed, verified);
            }, permitEndedContract: true);

    /// <summary>
    /// A building-scoped operator OTHER THAN the signing root Owner
    /// independently reviews EVERY already executed bank refund in staging,
    /// including the full-refund case with no preceding debt offset.
    /// No bank transfer is initiated. Only persisted signed decision values
    /// determine the amount; user input is solely documentary evidence.
    /// </summary>
    public Task<Guid> ReviewPostOffsetRefundAsync(
        Guid contractId, Guid requestId,
        SliceBPostOffsetRefundReviewInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (requestId == Guid.Empty || input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                var decision = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                var posted = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId);
                if (decision.ApprovedDebtOffsetAmount !=
                        (posted?.DebitHeldDepositVnd ?? 0m) ||
                    decision.ApprovedRefundAmount <= 0m ||
                    actor == decision.ApprovedByUserId ||
                    await db.DepositCloseoutApprovals.AsNoTracking().AnyAsync(x =>
                        x.ContractId == contractId))
                    throw Denied();
                await RequireApprovedRefundDestinationForPaymentAsync(
                    db, decision, input.PaidAtUtc);
                var prior = await db.DepositRefundBankReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ContractId == contractId ||
                        x.MoveOutRequestId == requestId);
                ContractDepositRefundBankReviewRecord signed;
                try
                {
                    signed = ContractDepositRefundBankReviewRecord.Attest(
                        Guid.NewGuid(), decision, actor, DateTime.UtcNow,
                        input.PaidAtUtc,
                        input.TransactionReference ?? string.Empty,
                        input.BankStatementEvidenceReference ?? string.Empty,
                        input.SignedIndependentReviewReference ?? string.Empty);
                    if (prior is not null)
                    {
                        prior.Verify(decision);
                        prior.RequireForCloseout(decision,
                            decision.ApprovedByUserId, input.PaidAtUtc,
                            input.TransactionReference ?? string.Empty,
                            input.BankStatementEvidenceReference ?? string.Empty);
                        if (prior.SignedReviewReference !=
                                input.SignedIndependentReviewReference?.Trim())
                            throw Denied();
                        return prior.Id;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                { throw Denied(); }
                db.DepositRefundBankReviews.Add(signed);
                return signed.Id;
            });

    /// <summary>
    /// Explicit root-only, isolated staging final handover. A MANUAL refund
    /// bank reference is a human attestation, not automatic bank verification.
    /// Every positive refund needs independent bank-statement review first.
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
                if (db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable ||
                    input is null ||
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

                var postedOffset = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building);
                ContractDepositPostOffsetCloseoutReadiness? offsetReadiness = null;
                if (postedOffset is not null)
                {
                    var signedApproval = await db.DebtOffsetApprovals.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == postedOffset.ApprovalId)
                        ?? throw Denied();
                    try { postedOffset.VerifyStoredPosting(signedApproval); }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    offsetReadiness = await AssessPostOffsetInsideScopeAsync(
                        db, contractId, requestId, building, roomId, null);
                    if (!offsetReadiness.CanApproveDepositDisposition ||
                        offsetReadiness.PostingId != postedOffset.Id)
                        throw Denied();
                }
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

                // A Reserved booking or Signed future contract is a
                // genuine next-tenant commitment, not a reason to refuse
                // an otherwise clean handover. PendingPayment alone is NOT.
                // Room UPDATE lock serializes normal reservation/contract
                // transitions with this disposition until the UoW commits.
                if (await db.Contracts.AsNoTracking().AnyAsync(x =>
                        x.Id != contractId && x.RoomId == roomId &&
                        !x.IsDeleted && x.Status == ContractStatus.Active))
                    throw Denied();
                var reservationFacts = await db.RoomReservations.AsNoTracking()
                    .Where(x => x.RoomId == roomId && !x.IsDeleted &&
                        (x.Status == RoomReservationStatus.Reserved ||
                         x.Status == RoomReservationStatus.PendingPayment))
                    .Select(x => new MoveOutRoomReservationFact(x.Id, x.Status))
                    .ToArrayAsync();
                var signedFacts = await db.Contracts.AsNoTracking()
                    .Where(x => x.RoomId == roomId && x.Id != contractId &&
                        !x.IsDeleted && x.Status == ContractStatus.Signed)
                    .Select(x => new MoveOutRoomSignedContractFact(x.Id, x.Status))
                    .ToArrayAsync();

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

                // Once the contract has an official receivable, never
                // complete move-out using only staging receipt totals.
                // Lock every posting, each original monthly issue and
                // independently reviewed payment aggregate to the commit.
                var official = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .Where(x => x.ContractId == contractId)
                    .OrderBy(x => x.Id).ToArrayAsync();
                if (official.Length > 0)
                {
                    var monthlyCount = await db.MonthlyIssuedInvoices.AsNoTracking()
                        .CountAsync(x => x.ContractId == contractId);
                    if (official.Length != invoices.Length + monthlyCount)
                        throw Denied();
                    var asOf = OfficialAsOf(null);
                    foreach (var posting in official)
                    {
                        if (posting.BuildingId != building ||
                            posting.RoomId != roomId)
                            throw Denied();
                        await using (var lockPosting = Command(db, """
                            SELECT "Id" FROM "BuildingManagementOfficialInvoiceReceivables"
                             WHERE "Id" = @id FOR UPDATE
                            """))
                        {
                            Add(lockPosting, "id", posting.Id);
                            if (await lockPosting.ExecuteScalarAsync() is not Guid)
                                throw Denied();
                        }
                        if (posting.Kind == OfficialInvoiceKind.Monthly)
                        {
                            await using var lockMonthly = Command(db, """
                                SELECT "Id" FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                                 WHERE "Id" = @id FOR UPDATE
                                """);
                            Add(lockMonthly, "id", posting.IssuedInvoiceId);
                            if (await lockMonthly.ExecuteScalarAsync() is not Guid)
                                throw Denied();
                        }
                        var balance = await AssessOfficialBalanceAsync(
                            db, posting, asOf);
                        if (balance.OutstandingAmount != 0m)
                            throw Denied();
                    }
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
                await RequireNoBlockedDebtDisputeAsync(db, contractId);
                var depositAudit = await _depositAudit.GetAsync(requestId);
                if (depositAudit.VerifiedIncomingAllocationTotal is not decimal held ||
                    held <= 0m || held != contract.DepositAmount ||
                    input.ForfeitAmount > held)
                    throw Denied();

                // The separately approved Phase 2G.4 decision is an
                // absolute precondition, not something Complete may invent.
                var decision = await db.DepositSettlementDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId) ?? throw Denied();
                await using (var lockDecision = Command(db, """
                    SELECT "Id" FROM "BuildingManagementContractDepositSettlementDecisions"
                     WHERE "Id" = @id FOR SHARE
                    """))
                {
                    Add(lockDecision, "id", decision.Id);
                    if (await lockDecision.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                try
                {
                    decision.RequireForCloseout(contractId, requestId, final.Id,
                        building, held, input.ForfeitAmount,
                        input.DamageReason, input.DamageEvidenceReference,
                        input.DepositPolicyReference,
                         postedOffset?.DebitHeldDepositVnd ?? 0m);
                    // The owner decision must precede an actual refund;
                    // neither a backdated manual proof nor another refund
                    // can be used to retroactively approve money sent.
                    if (input.RefundPaidAtUtc.HasValue &&
                        input.RefundPaidAtUtc.Value < decision.ApprovedAtUtc)
                        throw Denied();
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                decimal refundAmount = held -
                    (postedOffset?.DebitHeldDepositVnd ?? 0m) -
                    input.ForfeitAmount;
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

                if (refundAmount > 0m)
                {
                    // Also protect previously recorded bank reviews: no
                    // closeout unless the Owner instruction predates payment.
                    await RequireApprovedRefundDestinationForPaymentAsync(
                        db, decision, input.RefundPaidAtUtc!.Value);
                    var reviewedRefund = await db.DepositRefundBankReviews
                        .AsNoTracking().SingleOrDefaultAsync(x =>
                            x.ContractId == contractId &&
                            x.MoveOutRequestId == requestId &&
                            x.BuildingId == building) ?? throw Denied();
                    try
                    {
                        reviewedRefund.RequireForCloseout(decision, actor,
                            input.RefundPaidAtUtc!.Value,
                            input.RefundTransactionReference ?? string.Empty,
                            input.RefundBankEvidenceReference ?? string.Empty);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                }
                if (refundAmount == 0m &&
                    await db.DepositRefundBankReviews.AsNoTracking().AnyAsync(x =>
                        x.ContractId == contractId))
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
                        input.RefundPaidAtUtc, offsetReadiness);

                // Phase 2G.5: immutable, separately auditable room
                // release, bound to the SAME owner approval and timestamp.
                // Do not silently cancel a confirmed future reservation:
                // damaged + booked and multiple bookings remain HOLD.
                MoveOutRoomDispositionRecord roomRelease;
                try
                {
                    roomRelease = MoveOutRoomDispositionRecord.Complete(
                        Guid.NewGuid(), contractId, requestId, roomId,
                        building, approval.Id, actor, now, room.Status,
                        input.ForfeitAmount, reservationFacts, signedFacts,
                        input.RequiresMaintenance,
                        input.ForfeitAmount > 0m
                            ? input.DamageReason : input.MaintenanceReason,
                        input.ForfeitAmount > 0m
                            ? input.DamageEvidenceReference :
                              input.MaintenanceEvidenceReference,
                        input.HandoverEvidenceReference);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    InvalidDataException or ArgumentException or OverflowException)
                { throw Denied(); }

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
                room.ChangeStatus(roomRelease.ResultRoomStatus);
                db.MoveOutRoomDispositions.Add(roomRelease);

                return new SliceBStagedMoveOutCloseoutRead(
                    request.Id, contract.Id, room.Id, approval.Id,
                    final.Id, held, refundAmount, input.ForfeitAmount,
                    room.Status, roomRelease.Id,
                    roomRelease.FutureReservationId.HasValue ||
                    roomRelease.FutureSignedContractId.HasValue);
            });

    /// <summary>
    /// Immutable post-closeout handover history. Never reconstruct a
    /// historical status from the room's later occupancy changes.
    /// </summary>
    public Task<SliceBMoveOutRoomDispositionRead> GetRoomDispositionAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var disposition = await db.MoveOutRoomDispositions
                    .AsNoTracking().SingleOrDefaultAsync(x =>
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.RoomId == roomId && x.BuildingId == building)
                    ?? throw Denied();
                try { disposition.VerifyStoredDisposition(); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                if (!await db.DepositCloseoutApprovals.AsNoTracking()
                    .AnyAsync(x => x.Id == disposition.CloseoutApprovalId &&
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId))
                    throw Denied();
                return new SliceBMoveOutRoomDispositionRead(
                    disposition.Id, disposition.ContractId,
                    disposition.MoveOutRequestId, disposition.RoomId,
                    disposition.ResultRoomStatus, disposition.CompletedAtUtc,
                    disposition.RequiresMaintenance,
                    disposition.FutureReservationId.HasValue ||
                    disposition.FutureSignedContractId.HasValue,
                    disposition.DispositionSha256);
            }, permitEndedContract: true);

    /// <summary>
    /// Explicit root authorization converts one independently reviewed,
    /// immutable issued bill (monthly OR final) into a posted receivable.
    /// Idempotent retries preserve the original posting identity.
    /// </summary>
    public Task<OfficialInvoicePostingRead> PostOfficialReceivableAsync(
        Guid contractId, Guid issuedInvoiceId, OfficialInvoiceKind kind)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (issuedInvoiceId == Guid.Empty || !Enum.IsDefined(kind) ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                var table = kind == OfficialInvoiceKind.Monthly
                    ? "\"BuildingManagementMonthlyIssuedInvoiceLedger\""
                    : "\"BuildingManagementIssuedInvoiceLedger\"";
                await using (var cmd = Command(db,
                    "SELECT \"Id\" FROM " + table +
                    " WHERE \"Id\" = @id AND \"ContractId\" = @contract" +
                    " AND \"RoomId\" = @room AND \"BuildingId\" = @building FOR UPDATE"))
                {
                    Add(cmd, "id", issuedInvoiceId);
                    Add(cmd, "contract", contractId);
                    Add(cmd, "room", roomId);
                    Add(cmd, "building", building);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                OfficialInvoiceReceivablePostingRecord posted;
                try
                {
                    if (kind == OfficialInvoiceKind.Monthly)
                    {
                        var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                            .SingleAsync(x => x.Id == issuedInvoiceId);
                        var source = await db.MonthlyInvoiceCandidates.AsNoTracking()
                            .SingleAsync(x => x.Id == issued.CandidateId);
                        var review = await db.MonthlyInvoiceReviews.AsNoTracking()
                            .SingleAsync(x => x.Id == issued.ReviewId);
                        posted = OfficialInvoiceReceivablePostingRecord.PostMonthly(
                            Guid.NewGuid(), issued, source, review, actor, DateTime.UtcNow);
                    }
                    else
                    {
                        var issued = await db.IssuedInvoiceLedger.AsNoTracking()
                            .SingleAsync(x => x.Id == issuedInvoiceId);
                        var source = await db.FinalInvoiceIssueEvidence.AsNoTracking()
                            .SingleAsync(x => x.Id == issued.StagedEvidenceId);
                        var review = await db.InvoiceIssuanceReviews.AsNoTracking()
                            .SingleAsync(x => x.Id == issued.ReviewTicketId);
                        posted = OfficialInvoiceReceivablePostingRecord.PostFinal(
                            Guid.NewGuid(), issued, source, review, actor, DateTime.UtcNow);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or OverflowException)
                { throw Denied(); }

                var existing = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Kind == kind &&
                        x.IssuedInvoiceId == issuedInvoiceId);
                if (existing is not null)
                {
                    if (existing.ContractId != posted.ContractId ||
                        existing.RoomId != posted.RoomId ||
                        existing.BuildingId != posted.BuildingId ||
                        existing.BillingYear != posted.BillingYear ||
                        existing.BillingMonth != posted.BillingMonth ||
                        existing.OriginalAmount != posted.OriginalAmount ||
                        existing.DueDate != posted.DueDate ||
                        existing.SourceEvidenceSha256 != posted.SourceEvidenceSha256)
                        throw Denied();
                    posted = existing;
                }
                else
                {
                    db.OfficialInvoiceReceivables.Add(posted);
                }
                return new OfficialInvoicePostingRead(posted.Id,
                    posted.IssuedInvoiceId, posted.ContractId, posted.Kind,
                    posted.OriginalAmount, posted.DueDate,
                    IsOfficialDebtPosted: true);
            });

    /// <summary>
    /// A different authenticated operator audits the original completed
    /// manual transfer, its immutable allocation, and a bank-statement
    /// reference before it can reduce OFFICIAL debt. External bank APIs
    /// are neither called nor claimed verified.
    /// </summary>
    public Task<OfficialInvoiceReceiptReviewRead> ReviewOfficialReceiptAsync(
        Guid contractId, Guid postingId, Guid allocationId,
        OfficialInvoiceReceiptReviewInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (postingId == Guid.Empty || allocationId == Guid.Empty ||
                    input is null ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                var posting = await LockOfficialPostingAsync(
                    db, contractId, postingId, building, roomId);
                var evidence = await LoadOfficialReceiptEvidenceAsync(
                    db, posting, allocationId);
                OfficialInvoiceReceiptReviewRecord candidate;
                try
                {
                    candidate = OfficialInvoiceReceiptReviewRecord.Review(
                        Guid.NewGuid(), posting, evidence, actor, _clock.GetUtcNow().UtcDateTime,
                        input.Accept, input.BankStatementEvidenceReference,
                        input.Reason);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                var already = await db.OfficialInvoiceReceiptReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ReceiptAllocationId == allocationId &&
                        x.ReceivablePostingId == posting.Id);
                if (already is not null)
                {
                    if (already.ReviewedByUserId != actor ||
                        already.Accepted != input.Accept ||
                        already.BankStatementEvidenceReference !=
                            candidate.BankStatementEvidenceReference ||
                        already.ReviewReason != candidate.ReviewReason)
                        throw Denied();
                    already.Verify(posting, evidence);
                    return new OfficialInvoiceReceiptReviewRead(
                        already.Id, posting.Id, already.ReceiptAllocationId,
                        already.Accepted, already.Amount,
                        IsExternallyVerifiedBankTransfer: false);
                }

                // Same SERIALIZABLE invoice snapshot as PostgreSQL trigger.
                // A competing receipt approval/credit cannot both commit
                // if their aggregate sums would violate the posted total.
                if (candidate.Accepted)
                {
                    var alreadyVerified = await db.OfficialInvoiceReceiptReviews
                        .AsNoTracking().Where(x =>
                            x.ReceivablePostingId == posting.Id && x.Accepted)
                        .SumAsync(x => x.Amount);
                    var delta = posting.Kind == OfficialInvoiceKind.Monthly
                        ? await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                            .Where(x => x.IssuedInvoiceId ==
                                posting.IssuedInvoiceId && x.Accepted)
                            .SumAsync(x => x.ApprovedSignedDeltaAmount)
                        : 0m;
                    var internalApplied = await PostedInternalOffsetForInvoiceAsync(
                        db, contractId, posting.Id);
                    // D04 next-month H4 rent credit is a noncash offset
                    // against this SAME issued receivable. The historic
                    // receipt reviewer predates D04: without this preflight
                    // it would rely only on a later SQL AFTER INSERT guard,
                    // surfacing an avoidable SQL exception to the caller.
                    decimal d04RentCredit = 0m;
                    if (posting.Kind == OfficialInvoiceKind.Monthly)
                    {
                        await using var credit = Command(db, """
                            SELECT COALESCE(SUM("AmountVnd"),0)
                              FROM "BuildingManagementRoomChangeH4SettlementPostings"
                             WHERE "Kind"=2 AND "IssuedInvoiceId"=@issued
                            """);
                        Add(credit, "issued", posting.IssuedInvoiceId);
                        d04RentCredit = Convert.ToDecimal(
                            await credit.ExecuteScalarAsync());
                    }
                    if (checked(alreadyVerified + candidate.Amount +
                            internalApplied + d04RentCredit) >
                        checked(posting.OriginalAmount + delta))
                        throw Denied();
                }
                db.OfficialInvoiceReceiptReviews.Add(candidate);
                return new OfficialInvoiceReceiptReviewRead(
                    candidate.Id, posting.Id, candidate.ReceiptAllocationId,
                    candidate.Accepted, candidate.Amount,
                    IsExternallyVerifiedBankTransfer: false);
            });

    /// <summary>
    /// Phase 2G.4: persist the COMPLETE signed per-fee split of ONE already
    /// bank-reviewed monthly receipt. Owner-only, isolated staging, 2-person
    /// evidence separation and SERIALIZABLE. This is NOT a deposit offset.
    /// All rows are written in one transaction: PostgreSQL deferred commit
    /// rejects an incomplete set, while the posting row lock serializes races.
    /// </summary>
    public Task<SliceBMonthlyReceiptLineSplitRead> AllocateMonthlyReceiptLinesAsync(
        Guid contractId, Guid postingId, Guid receiptReviewId,
        SliceBMonthlyReceiptLineSplitInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (postingId == Guid.Empty || receiptReviewId == Guid.Empty ||
                    input?.Lines is null ||
                    input.Lines.Count is < 1 or > 4 ||
                    !ValidProof(input.SignedAllocationReference) ||
                    input.SignedAllocationReference.Trim().Length < 12 ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();

                var categories = input.Lines.Select(x => x.Category).ToArray();
                if (categories.Distinct().Count() != categories.Length ||
                    categories.Any(x => x is not
                        ContractDepositOffsetCategory.UnpaidRent and
                        not ContractDepositOffsetCategory.Electricity and
                        not ContractDepositOffsetCategory.Water and
                        not ContractDepositOffsetCategory.ApprovedService) ||
                    input.Lines.Any(x => x.AmountVnd <= 0m ||
                        decimal.Truncate(x.AmountVnd) != x.AmountVnd))
                    throw Denied();

                var posting = await LockOfficialPostingAsync(
                    db, contractId, postingId, building, roomId);
                if (posting.Kind != OfficialInvoiceKind.Monthly)
                    throw Denied();

                await using (var cmd = Command(db, """
                    SELECT "Id"
                      FROM "BuildingManagementOfficialInvoiceReceiptReviews"
                     WHERE "Id" = @review
                       AND "ReceivablePostingId" = @posting
                       AND "ContractId" = @contract FOR UPDATE
                    """))
                {
                    Add(cmd, "review", receiptReviewId);
                    Add(cmd, "posting", postingId);
                    Add(cmd, "contract", contractId);
                    if (await cmd.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                var bankReview = await db.OfficialInvoiceReceiptReviews
                    .AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == receiptReviewId &&
                        x.ReceivablePostingId == postingId &&
                        x.ContractId == contractId) ?? throw Denied();
                if (!bankReview.Accepted || actor == bankReview.ReviewedByUserId)
                    throw Denied();
                var original = await LoadOfficialReceiptEvidenceAsync(
                    db, posting, bankReview.ReceiptAllocationId);
                try { bankReview.Verify(posting, original); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                if (checked(input.Lines.Sum(x => x.AmountVnd)) !=
                    bankReview.Amount)
                    throw Denied();

                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == posting.IssuedInvoiceId &&
                        x.ContractId == contractId) ?? throw Denied();
                var source = await db.MonthlyInvoiceCandidates.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == issued.CandidateId &&
                        x.ContractId == contractId) ?? throw Denied();
                var independent = await db.MonthlyInvoiceReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Id == issued.ReviewId) ?? throw Denied();
                try { posting.VerifyMonthly(issued, source, independent); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                // An adjusted invoice needs a separately reviewed correction
                // allocation. Never silently distribute that delta among
                // the four ORIGINAL categories.
                if (await db.MonthlyInvoiceAdjustments.AsNoTracking()
                        .AnyAsync(x => x.IssuedInvoiceId == issued.Id) ||
                    await db.MonthlyInvoiceAdjustmentDecisions.AsNoTracking()
                        .AnyAsync(x => x.IssuedInvoiceId == issued.Id) ||
                    await db.DepositSettlementDecisions.AsNoTracking()
                        .AnyAsync(x => x.ContractId == contractId) ||
                    await db.DepositCloseoutApprovals.AsNoTracking()
                        .AnyAsync(x => x.ContractId == contractId))
                    throw Denied();

                var prior = await db.OfficialMonthlyReceiptLineAllocations
                    .AsNoTracking()
                    .Where(x => x.ReceivablePostingId == postingId)
                    .ToArrayAsync();
                var already = prior.Where(x =>
                    x.ReceiptReviewId == receiptReviewId).ToArray();
                if (already.Length != 0)
                {
                    // Safe idempotent replay for precisely the SAME
                    // authenticated actor, documentation and charge amounts.
                    if (already.Length != input.Lines.Count ||
                        already.Any(x => x.AllocatedByUserId != actor ||
                            x.SignedAllocationReference !=
                                input.SignedAllocationReference.Trim() ||
                            !input.Lines.Any(line =>
                                line.Category == x.Category &&
                                line.AmountVnd == x.Amount)))
                        throw Denied();
                    foreach (var row in already)
                        row.VerifyAgainst(posting, original, bankReview);
                    return new SliceBMonthlyReceiptLineSplitRead(
                        postingId, receiptReviewId, already.Length,
                        already.Sum(x => x.Amount),
                        IsBankExternallyVerified: false,
                        CanPostDepositOffset: false);
                }

                var archived = source.VerifyStoredEvidence();
                foreach (var line in input.Lines)
                {
                    decimal ceiling = line.Category switch
                    {
                        ContractDepositOffsetCategory.UnpaidRent =>
                            archived.RentAmount,
                        ContractDepositOffsetCategory.Electricity =>
                            archived.ElectricityAmount,
                        ContractDepositOffsetCategory.Water =>
                            archived.WaterAmount,
                        ContractDepositOffsetCategory.ApprovedService =>
                            archived.AuxiliaryAmount,
                        _ => throw Denied()
                    };
                    var alreadyCredited = await
                        PostedInternalOffsetForInvoiceAsync(
                            db, contractId, posting.Id, line.Category);
                    if (checked(prior.Where(x => x.Category == line.Category)
                            .Sum(x => x.Amount) + line.AmountVnd +
                            alreadyCredited) > ceiling)
                        throw Denied();
                }

                var now = DateTime.UtcNow;
                var rows = new List<OfficialMonthlyReceiptLineAllocationRecord>();
                foreach (var line in input.Lines)
                {
                    try
                    {
                        rows.Add(OfficialMonthlyReceiptLineAllocationRecord.Capture(
                            Guid.NewGuid(), posting, original, bankReview,
                            line.Category, line.AmountVnd, actor, now,
                            input.SignedAllocationReference));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or
                        ArgumentException or InvalidDataException or OverflowException)
                    { throw Denied(); }
                }

                db.OfficialMonthlyReceiptLineAllocations.AddRange(rows);
                return new SliceBMonthlyReceiptLineSplitRead(
                    postingId, receiptReviewId, rows.Count,
                    rows.Sum(x => x.Amount),
                    IsBankExternallyVerified: false,
                    CanPostDepositOffset: false);
            });

    /// <summary>
    /// Separate staff finance review of CURRENT original posted invoice
    /// claims. Only server-loaded evidence is signed, never request money.
    /// This is not an Owner approval, offset posting, or bank transfer.
    /// </summary>
    public Task<SliceBOffsetDebtReviewRead> ReviewDebtOffsetEvidenceAsync(
        Guid contractId, Guid requestId, SliceBOffsetDebtReviewInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                if (requestId == Guid.Empty || input is null ||
                    !ValidProof(input.SignedReviewReference) ||
                    input.SignedReviewReference.Trim().Length < 12 ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                var asOf = OfficialAsOf(input.AsOfDate);
                var proof = await _offsetReader.GetAsync(requestId, asOf);
                if (!proof.EvidenceReviewable ||
                    proof.ContractId != contractId ||
                    proof.MoveOutRequestId != requestId ||
                    proof.VerifiedHeldVndForReview is not decimal held ||
                    proof.OfficialUnpaidMonthlyClaims.Count == 0)
                    throw Denied();

                var previous = await db.DebtOffsetReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId);
                if (previous is not null)
                {
                    if (previous.MoveOutRequestId != requestId ||
                        previous.ReviewedByUserId != actor ||
                        previous.ReviewedAsOfDate != asOf ||
                        previous.SignedReviewReference != input.SignedReviewReference.Trim())
                        throw Denied();
                    try {
                        previous.RequireCurrentEvidence(contractId, requestId,
                            building, held, proof.OfficialUnpaidMonthlyClaims);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    return new SliceBOffsetDebtReviewRead(previous.Id, requestId,
                        previous.VerifiedOutstandingAmount,
                        IsIndependentDebtReview: true,
                        CanPostDepositOffset: false);
                }

                ContractDepositDebtReviewRecord review;
                try
                {
                    review = ContractDepositDebtReviewRecord.Capture(
                        Guid.NewGuid(), contractId, requestId, building,
                        actor, asOf, DateTime.UtcNow, held,
                        proof.OfficialUnpaidMonthlyClaims,
                        input.SignedReviewReference);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    InvalidDataException or ArgumentException or OverflowException)
                { throw Denied(); }
                db.DebtOffsetReviews.Add(review);
                return new SliceBOffsetDebtReviewRead(review.Id, requestId,
                    review.VerifiedOutstandingAmount,
                    IsIndependentDebtReview: true,
                    CanPostDepositOffset: false);
            });

    /// <summary>
    /// Root-only Owner authorization; exact prior independent review and
    /// all original bank/charge evidence must still match at SERIALIZABLE.
    /// No settlement, refund, invoice amendment or contract termination.
    /// </summary>
    public Task<SliceBOwnerDebtOffsetApprovalRead> ApproveDebtOffsetAsync(
        Guid contractId, Guid requestId, Guid reviewId,
        SliceBOwnerDebtOffsetApprovalInput input)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (requestId == Guid.Empty || reviewId == Guid.Empty ||
                    input is null ||
                    !ValidProof(input.SignedPolicyReference) ||
                    input.SignedPolicyReference.Trim().Length < 12 ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                var approvedReview = await db.DebtOffsetReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == reviewId &&
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                if (approvedReview.ReviewedByUserId == actor)
                    throw Denied();
                // Do NOT permit a caller to choose a stale review date or a
                // fabricated debt/held amount via an HTTP request body.
                var current = await _offsetReader.GetAsync(requestId,
                    OfficialAsOf(null));
                if (!current.EvidenceReviewable ||
                    current.ContractId != contractId ||
                    current.VerifiedHeldVndForReview is not decimal held ||
                    current.OfficialUnpaidMonthlyClaims.Count == 0)
                    throw Denied();
                try
                {
                    approvedReview.RequireCurrentEvidence(contractId,
                        requestId, building, held,
                        current.OfficialUnpaidMonthlyClaims);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }

                var older = await db.DebtOffsetApprovals.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId);
                if (older is not null)
                {
                    if (older.MoveOutRequestId != requestId ||
                        older.ApprovedByOwnerId != actor ||
                        older.IndependentDebtReviewerId !=
                            approvedReview.ReviewedByUserId ||
                        older.IndependentReviewReference !=
                            approvedReview.SignedReviewReference ||
                        older.SignedPolicyReference !=
                            input.SignedPolicyReference.Trim() ||
                        !older.ReadOwnerTierAllocations().OrderBy(x => x.ClaimId)
                            .SequenceEqual((input.OwnerTierAllocations ??
                                Array.Empty<ContractDepositOwnerTierAllocation>())
                                .OrderBy(x => x.ClaimId)))
                        throw Denied();
                    try {
                        older.RequireCurrentEvidence(contractId,requestId,
                            building,held,current.OfficialUnpaidMonthlyClaims);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    return new SliceBOwnerDebtOffsetApprovalRead(older.Id,
                        contractId,older.ApprovedOffsetAmount,
                        older.ExpectedRemainingDeposit,older.ExpectedUncoveredDebt,
                        IsMoneyPosted:false,CanCompleteMoveOut:false);
                }

                ContractDepositDebtOffsetApprovalRecord approval;
                try
                {
                    approval = ContractDepositDebtOffsetApprovalRecord.Approve(
                        Guid.NewGuid(),contractId,requestId,building,actor,
                        approvedReview.ReviewedByUserId,DateTime.UtcNow,held,
                        input.SignedPolicyReference,
                        approvedReview.SignedReviewReference,
                        current.OfficialUnpaidMonthlyClaims,
                        input.OwnerTierAllocations);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    InvalidDataException or ArgumentException or OverflowException)
                { throw Denied(); }

                db.DebtOffsetApprovals.Add(approval);
                return new SliceBOwnerDebtOffsetApprovalRead(approval.Id,
                    contractId,approval.ApprovedOffsetAmount,
                    approval.ExpectedRemainingDeposit,
                    approval.ExpectedUncoveredDebt,
                    IsMoneyPosted:false,CanCompleteMoveOut:false);
            });

    /// <summary>
    /// OI-04 real INTERNAL offset: one immutable balanced posting in the
    /// SAME serializable isolated Building transaction. Root Identity and
    /// per-building membership are held through commit. No client amounts,
    /// no payment receipt, no refund and no move-out completion.
    /// </summary>
    public Task<SliceBDebtOffsetPostingRead> PostApprovedDebtOffsetAsync(
        Guid contractId, Guid requestId, Guid approvalId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: true,
            async (db, actor, building, roomId) =>
            {
                if (requestId == Guid.Empty || approvalId == Guid.Empty ||
                    db.Database.CurrentTransaction?.GetDbTransaction()
                        .IsolationLevel != System.Data.IsolationLevel.Serializable)
                    throw Denied();
                await LockRequestAsync(db, requestId, contractId, roomId);
                var approval = await db.DebtOffsetApprovals.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == approvalId &&
                        x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                if (approval.ApprovedByOwnerId != actor)
                    throw Denied();
                var previous = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId);
                if (previous is not null)
                {
                    if (previous.ApprovalId != approvalId ||
                        previous.MoveOutRequestId != requestId)
                        throw Denied();
                    try { previous.VerifyStoredPosting(approval); }
                    catch (Exception ex) when (ex is InvalidDataException or
                        InvalidOperationException or ArgumentException)
                    { throw Denied(); }
                    return OffsetPostedRead(previous);
                }
                // Both preflight and PostgreSQL INSERT reverify the SAME
                // cash-backed invoice evidence, signed allocation, contract,
                // payment source and competing dispositions.
                var current = await _offsetReader.GetAsync(requestId,
                    OfficialAsOf(null));
                if (!current.EvidenceReviewable ||
                    current.ContractId != contractId ||
                    current.MoveOutRequestId != requestId ||
                    current.VerifiedHeldVndForReview is not decimal held ||
                    current.OfficialUnpaidMonthlyClaims.Count == 0 ||
                    await db.DepositSettlementDecisions.AsNoTracking()
                        .AnyAsync(x => x.ContractId == contractId) ||
                    await db.DepositCloseoutApprovals.AsNoTracking()
                        .AnyAsync(x => x.ContractId == contractId))
                    throw Denied();
                var review = await db.DebtOffsetReviews.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.ReviewedByUserId == approval.IndependentDebtReviewerId)
                    ?? throw Denied();
                try
                {
                    review.RequireCurrentEvidence(contractId, requestId,
                        building, held, current.OfficialUnpaidMonthlyClaims);
                    approval.RequireCurrentEvidence(contractId, requestId,
                        building, held, current.OfficialUnpaidMonthlyClaims);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                ContractDepositDebtOffsetPostingRecord posted;
                try
                {
                    posted = ContractDepositDebtOffsetPostingRecord.Post(
                        Guid.NewGuid(), actor, DateTime.UtcNow, approval,
                        held, current.OfficialUnpaidMonthlyClaims);
                }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException or
                    OverflowException)
                { throw Denied(); }
                db.DebtOffsetPostings.Add(posted);
                // A single append-only row contains BOTH financial legs.
                // If PostgreSQL rejects any invariant at COMMIT, both roll back.
                return OffsetPostedRead(posted);
            });

    public Task<SliceBDebtOffsetPostingRead> GetPostedDebtOffsetAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var posted = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                var approval = await db.DebtOffsetApprovals.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == posted.ApprovalId)
                    ?? throw Denied();
                try { posted.VerifyStoredPosting(approval); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                return OffsetPostedRead(posted);
            });

    /// <summary>
    /// Read the entire audited bank/deposit debt equation after OI-04.
    /// Never mark move-out complete, fabricate external bank verification or
    /// silently release residual held cọc. No HTTP-provided money amounts.
    /// </summary>
    public Task<ContractDepositPostOffsetCloseoutReadiness>
        GetPostOffsetCloseoutReadinessAsync(Guid contractId, Guid requestId,
            DateOnly? asOfDate)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            (db, actor, building, roomId) =>
                AssessPostOffsetInsideScopeAsync(db, contractId, requestId,
                    building, roomId, asOfDate));

    private async Task<ContractDepositPostOffsetCloseoutReadiness>
        AssessPostOffsetInsideScopeAsync(BuildingManagementDbContext db,
            Guid contractId, Guid requestId, Guid building, Guid roomId,
            DateOnly? asOfDate)
    {
                if (requestId == Guid.Empty) throw Denied();
                var post = await db.DebtOffsetPostings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId &&
                        x.BuildingId == building) ?? throw Denied();
                var approval = await db.DebtOffsetApprovals.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == post.ApprovalId &&
                        x.BuildingId == building) ?? throw Denied();
                try { post.VerifyStoredPosting(approval); }
                catch (Exception ex) when (ex is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                { throw Denied(); }
                var date = OfficialAsOf(asOfDate);
                var invoices = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .Where(x => x.ContractId == contractId &&
                        x.BuildingId == building && x.RoomId == roomId)
                    .ToArrayAsync();
                var balances = new List<OfficialInvoiceReceivableBalance>();
                foreach (var invoice in invoices)
                    balances.Add(await AssessOfficialBalanceAsync(
                        db, invoice, date));
                var finalCount = await db.IssuedInvoiceLedger.AsNoTracking()
                    .CountAsync(x => x.ContractId == contractId &&
                        x.MoveOutRequestId == requestId);
                var monthlyCount = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .CountAsync(x => x.ContractId == contractId);
                var checklist = await db.MoveOutChecklistTasks.AsNoTracking()
                    .Where(x => x.MoveOutRequestId == requestId).ToArrayAsync();
                var checklistComplete = checklist.Length == 5 &&
                    checklist.Select(x => x.Type).Distinct().Count() == 5
                    ? checklist.Count(x => x.IsCompleted) : 0;
                // A bank review alone cannot classify a monthly payment
                // that follows the frozen owner offset. Verify that EVERY
                // accepted monthly receipt has its own exact signed split;
                // prevent "paid" closeout while an allocation is missing.
                var monthlyPostingIds = invoices
                    .Where(x => x.Kind == OfficialInvoiceKind.Monthly)
                    .Select(x => x.Id).ToArray();
                var acceptedMonthly = await db.OfficialInvoiceReceiptReviews
                    .AsNoTracking().Where(x =>
                        monthlyPostingIds.Contains(x.ReceivablePostingId) &&
                        x.Accepted).ToArrayAsync();
                var reviewedIds = acceptedMonthly.Select(x => x.Id).ToArray();
                var allocations = await db.OfficialMonthlyReceiptLineAllocations
                    .AsNoTracking().Where(x =>
                        reviewedIds.Contains(x.ReceiptReviewId))
                    .ToArrayAsync();
                var allSplitsComplete = acceptedMonthly.All(review =>
                {
                    var source = allocations.Where(x =>
                        x.ReceiptReviewId == review.Id).ToArray();
                    return source.Length != 0 &&
                        source.Select(x => x.Category).Distinct().Count() ==
                            source.Length &&
                        source.Sum(x => x.Amount) == review.Amount;
                });
                var deposit = await _depositAudit.GetAsync(requestId);
                return ContractDepositPostOffsetCloseoutEvaluator.Assess(
                    post, approval, deposit, date, balances, finalCount,
                    finalCount + monthlyCount, checklistComplete, 5,
                    allSplitsComplete);
    }

    private static async Task<decimal> PostedInternalOffsetForInvoiceAsync(
        BuildingManagementDbContext db, Guid contractId, Guid postingId,
        ContractDepositOffsetCategory? category = null)
    {
        var posting = await db.DebtOffsetPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ContractId == contractId);
        if (posting is null) return 0m;
        var approval = await db.DebtOffsetApprovals.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == posting.ApprovalId)
            ?? throw Denied();
        try
        {
            var lines = posting.ReadVerifiedLines(approval);
            return lines.Where(line =>
                    (!category.HasValue || line.Category == category.Value) &&
                    line.ClaimId == ContractDepositOfficialMonthlyClaimBridge
                        .ClaimIdFor(postingId, line.Category))
                .Sum(line => line.ProposedAppliedVnd);
        }
        catch (Exception ex) when (ex is InvalidDataException or
            InvalidOperationException or ArgumentException or OverflowException)
        { throw Denied(); }
    }

    private static SliceBDebtOffsetPostingRead OffsetPostedRead(
        ContractDepositDebtOffsetPostingRecord p)
        => new(p.Id, p.ApprovalId, p.ContractId, p.MoveOutRequestId,
            p.DebitHeldDepositVnd, p.CreditOfficialDebtVnd,
            p.RemainingHeldDepositVnd, p.RemainingOfficialDebtVnd,
            p.PostingSha256, true, false, false);

    public Task<OfficialInvoiceReceivableBalance> GetOfficialBalanceAsync(
        Guid contractId, Guid postingId, DateOnly? asOfDate)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var asOf = OfficialAsOf(asOfDate);
                var posted = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == postingId &&
                        x.ContractId == contractId && x.RoomId == roomId &&
                        x.BuildingId == building) ?? throw Denied();
                return await AssessOfficialBalanceAsync(db, posted, asOf);
            }, permitEndedContract: true);

    /// <summary>
    /// Contract-wide official debt. An issued invoice missing a posting
    /// makes the portfolio INCOMPLETE, never an invented zero balance.
    /// </summary>
    public Task<OfficialContractDebtPortfolioRead> GetOfficialDebtPortfolioAsync(
        Guid contractId, DateOnly? asOfDate)
        => ExecuteScopedAsync(contractId, requireBootstrapOwner: false,
            async (db, actor, building, roomId) =>
            {
                var asOf = OfficialAsOf(asOfDate);
                var postings = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .Where(x => x.ContractId == contractId &&
                        x.RoomId == roomId && x.BuildingId == building)
                    .OrderBy(x => x.BillingYear).ThenBy(x => x.BillingMonth)
                    .ToArrayAsync();
                var issuedCount =
                    await db.MonthlyIssuedInvoices.AsNoTracking().CountAsync(x =>
                        x.ContractId == contractId && x.RoomId == roomId) +
                    await db.IssuedInvoiceLedger.AsNoTracking().CountAsync(x =>
                        x.ContractId == contractId && x.RoomId == roomId);
                if (issuedCount == 0 || issuedCount != postings.Length ||
                    postings.Length > 250)
                    throw Denied();
                var rows = new OfficialInvoiceReceivableBalance[postings.Length];
                decimal amount = 0m, receipts = 0m, outstanding = 0m;
                for (var i = 0; i < postings.Length; i++)
                {
                    rows[i] = await AssessOfficialBalanceAsync(
                        db, postings[i], asOf);
                    amount = checked(amount + rows[i].AdjustedAmount);
                    receipts = checked(receipts +
                        rows[i].IndependentlyReviewedManualPayments);
                    outstanding = checked(outstanding + rows[i].OutstandingAmount);
                }
                return new OfficialContractDebtPortfolioRead(contractId,
                    building, asOf, rows.Length, amount, receipts, outstanding,
                    rows.Count(x => x.Status == IssuedInvoicePaymentStatus.Overdue),
                    rows, IsOfficialDebtPosted: true,
                    IsExternallyVerifiedBankTransfer: false)
                {
                    InternalDepositOffsetVnd = rows.Sum(x => x.InternalDepositOffsetVnd)
                };
            }, permitEndedContract: true);

    private static DateOnly OfficialAsOf(DateOnly? requested)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
        var asOf = requested ?? today;
        return asOf == default || asOf > today ? throw Denied() : asOf;
    }

    private static async Task<OfficialInvoiceReceivablePostingRecord>
        LockOfficialPostingAsync(BuildingManagementDbContext db,
            Guid contractId, Guid postingId, Guid buildingId, Guid roomId)
    {
        await using (var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementOfficialInvoiceReceivables"
             WHERE "Id" = @id AND "ContractId" = @contract
               AND "RoomId" = @room AND "BuildingId" = @building FOR UPDATE
            """))
        {
            Add(cmd, "id", postingId);
            Add(cmd, "contract", contractId);
            Add(cmd, "room", roomId);
            Add(cmd, "building", buildingId);
            if (await cmd.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }
        return await db.OfficialInvoiceReceivables.AsNoTracking()
            .SingleAsync(x => x.Id == postingId);
    }

    private static async Task<OfficialInvoiceReceiptEvidence>
        LoadOfficialReceiptEvidenceAsync(BuildingManagementDbContext db,
            OfficialInvoiceReceivablePostingRecord posting, Guid allocationId)
    {
        try
        {
            if (posting.Kind == OfficialInvoiceKind.Monthly)
            {
                var journal = await db.MonthlyInvoicePaymentAllocations
                    .AsNoTracking().SingleAsync(x =>
                        x.Id == allocationId &&
                        x.IssuedInvoiceId == posting.IssuedInvoiceId);
                var receipt = await db.Payments.IgnoreQueryFilters()
                    .AsNoTracking().SingleAsync(x => x.Id == journal.PaymentId);
                return OfficialInvoiceReceiptEvidence.FromMonthly(journal, receipt);
            }
            else
            {
                var journal = await db.InvoicePaymentAllocationJournal
                    .AsNoTracking().SingleAsync(x =>
                        x.Id == allocationId &&
                        x.IssuedInvoiceId == posting.IssuedInvoiceId);
                var receipt = await db.Payments.IgnoreQueryFilters()
                    .AsNoTracking().SingleAsync(x => x.Id == journal.PaymentId);
                return OfficialInvoiceReceiptEvidence.FromFinal(journal, receipt);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or
            InvalidOperationException or ArgumentException)
        { throw Denied(); }
    }

    private static async Task<OfficialInvoiceReceivableBalance>
        AssessOfficialBalanceAsync(BuildingManagementDbContext db,
            OfficialInvoiceReceivablePostingRecord posted, DateOnly asOf)
    {
        if (posted.Id == Guid.Empty ||
            posted.PostedAtUtc.Kind != DateTimeKind.Utc ||
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                posted.PostedAtUtc,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))) > asOf)
            throw Denied();
        try
        {
            decimal signedDelta = 0m;
            if (posted.Kind == OfficialInvoiceKind.Monthly)
            {
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleAsync(x => x.Id == posted.IssuedInvoiceId);
                var archive = await db.MonthlyInvoiceCandidates.AsNoTracking()
                    .SingleAsync(x => x.Id == issued.CandidateId);
                var review = await db.MonthlyInvoiceReviews.AsNoTracking()
                    .SingleAsync(x => x.Id == issued.ReviewId);
                posted.VerifyMonthly(issued, archive, review);
                var proposals = await db.MonthlyInvoiceAdjustments.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == posted.IssuedInvoiceId)
                    .ToArrayAsync();
                var decisions = await db.MonthlyInvoiceAdjustmentDecisions
                    .AsNoTracking().Where(x =>
                        x.IssuedInvoiceId == posted.IssuedInvoiceId)
                    .ToArrayAsync();
                signedDelta = MonthlyInvoiceAdjustmentEvaluator.Assess(
                    issued, proposals, decisions, asOf, 0m).ApprovedSignedAdjustment;
            }
            else
            {
                var issued = await db.IssuedInvoiceLedger.AsNoTracking()
                    .SingleAsync(x => x.Id == posted.IssuedInvoiceId);
                var archive = await db.FinalInvoiceIssueEvidence.AsNoTracking()
                    .SingleAsync(x => x.Id == issued.StagedEvidenceId);
                var review = await db.InvoiceIssuanceReviews.AsNoTracking()
                    .SingleAsync(x => x.Id == issued.ReviewTicketId);
                posted.VerifyFinal(issued, archive, review);
            }

            var journals = posted.Kind == OfficialInvoiceKind.Monthly
                ? (await db.MonthlyInvoicePaymentAllocations.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == posted.IssuedInvoiceId)
                    .ToArrayAsync()).Select(x => (x.Id, x.PaymentId)).ToArray()
                : (await db.InvoicePaymentAllocationJournal.AsNoTracking()
                    .Where(x => x.IssuedInvoiceId == posted.IssuedInvoiceId)
                    .ToArrayAsync()).Select(x => (x.Id, x.PaymentId)).ToArray();
            var originalPayments = await db.Payments.IgnoreQueryFilters()
                .AsNoTracking().Where(x =>
                    x.InvoiceId == posted.IssuedInvoiceId &&
                    x.ContractId == posted.ContractId &&
                    x.Purpose == PaymentPurpose.InvoicePayment).ToArrayAsync();
            if (originalPayments.Length != journals.Length ||
                journals.Select(x => x.PaymentId).Distinct().Count() != journals.Length ||
                originalPayments.Select(x => x.Id).Distinct().Count() !=
                    originalPayments.Length)
                throw Denied();
            var map = originalPayments.ToDictionary(x => x.Id);
            var evidence = new OfficialInvoiceReceiptEvidence[journals.Length];
            for (var i = 0; i < journals.Length; i++)
            {
                if (!map.ContainsKey(journals[i].PaymentId))
                    throw Denied();
                evidence[i] = await LoadOfficialReceiptEvidenceAsync(
                    db, posted, journals[i].Id);
            }
            var reviews = await db.OfficialInvoiceReceiptReviews.AsNoTracking()
                .Where(x => x.ReceivablePostingId == posted.Id)
                .ToArrayAsync();
            var result = OfficialInvoiceReceivableBalanceEvaluator.Assess(
                posted, asOf, signedDelta, evidence, reviews);
            var offset = await db.DebtOffsetPostings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ContractId == posted.ContractId);
            if (offset is not null)
            {
                var approval = await db.DebtOffsetApprovals.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == offset.ApprovalId)
                    ?? throw Denied();
                offset.VerifyStoredPosting(approval);
                var local = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(offset.PostedAtUtc,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                if (local <= asOf)
                {
                    decimal applied = 0m;
                    foreach (var line in offset.ReadVerifiedLines(approval))
                    {
                        var matchesThisInvoice = line.ClaimId ==
                            ContractDepositOfficialMonthlyClaimBridge.ClaimIdFor(
                                posted.Id, line.Category);
                        if (matchesThisInvoice)
                            applied = checked(applied + line.ProposedAppliedVnd);
                    }
                    if (applied > 0m)
                        result = result.WithVerifiedInternalOffset(applied);
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or
            InvalidOperationException or ArgumentException or OverflowException)
        { throw Denied(); }
    }

    private static bool ValidProof(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Trim().Length >= 8 &&
           text.Trim().Length <= 500;

    private async Task<T> ExecuteScopedAsync<T>(
        Guid contractId, bool requireBootstrapOwner,
        Func<BuildingManagementDbContext, Guid, Guid, Guid, Task<T>> work,
        bool permitEndedContract = false)
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
                if (contract is null ||
                    (contract.Status != ContractStatus.Active &&
                     !(permitEndedContract &&
                       contract.Status == ContractStatus.Ended)) ||
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

    private static void AddValue(DbCommand cmd, string name, object value)
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

public sealed record SliceBStagedMonthlyReviewRead(
    Guid ReviewId, Guid CandidateId, Guid ContractId,
    bool Accepted, bool IsOfficialReceivable);

public sealed record SliceBStagedMonthlyIssuedRead(
    Guid IssuedInvoiceId, Guid CandidateId, Guid ContractId,
    string InvoiceNumber, decimal Amount, DateOnly DueDate,
    bool IsOfficialReceivable);

public sealed record SliceBStagedMonthlyBalanceRead(
    Guid IssuedInvoiceId, Guid ContractId, Guid BuildingId,
    string InvoiceNumber, DateOnly DueDate, DateOnly AsOfDate,
    decimal IssuedAmount, decimal CompletedPaymentsAmount,
    decimal OutstandingBeforeAdjustments, int CompletedPaymentCount,
    IssuedInvoicePaymentStatus PaymentStatus,
    bool IsOfficialDebtPosted, bool BankTransferIndependentlyVerified,
    decimal ApprovedSignedAdjustment, decimal AdjustedAmount,
    decimal OutstandingAfterAdjustments, int ApprovedAdjustmentCount);

public sealed class SliceBMonthlyAdjustmentInput
{
    public decimal SignedDeltaAmount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SourceEvidenceReference { get; set; } = string.Empty;
}
public sealed class SliceBMonthlyAdjustmentDecisionInput
{
    public bool Accept { get; set; }
    public string Reason { get; set; } = string.Empty;
}
public sealed record SliceBStagedMonthlyAdjustmentRead(
    Guid AdjustmentId, Guid IssuedInvoiceId,
    decimal SignedDeltaAmount, bool IsCreditNote, bool IsDebitNote,
    bool IsOfficialDebtPosted);
public sealed record SliceBStagedMonthlyAdjustmentDecisionRead(
    Guid DecisionId, Guid AdjustmentId, Guid IssuedInvoiceId,
    bool Accepted, decimal ApprovedSignedDeltaAmount,
    bool IsOfficialDebtPosted);

public sealed record SliceBStagedMonthlyReceiptRead(
    Guid PaymentId, Guid AllocationJournalId, Guid IssuedInvoiceId,
    decimal StagedAmount, bool IsOfficialReceivableSettled);

public sealed record SliceBStagedMonthlyInvoiceCandidateRead(
    Guid CandidateId, Guid DraftInvoiceId, Guid ContractId,
    int BillingYear, int BillingMonth,
    decimal FrozenCandidateAmount, DateOnly DocumentedDueDate,
    string EvidenceSha256, bool IsOfficialReceivable);

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
    public bool RequiresMaintenance { get; set; }
    public string? MaintenanceReason { get; set; }
    public string? MaintenanceEvidenceReference { get; set; }
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
    decimal ForfeitAmount, RoomStatus RoomStatus,
    Guid RoomDispositionId, bool HasFutureCommitment);

public sealed record SliceBMoveOutRoomDispositionRead(
    Guid DispositionId, Guid ContractId, Guid MoveOutRequestId,
    Guid RoomId, RoomStatus ResultRoomStatus, DateTime CompletedAtUtc,
    bool RequiresMaintenance, bool HasFutureCommitment,
    string DispositionSha256);

public sealed record OfficialInvoicePostingRead(
    Guid PostingId, Guid IssuedInvoiceId, Guid ContractId,
    OfficialInvoiceKind Kind, decimal OriginalAmount, DateOnly DueDate,
    bool IsOfficialDebtPosted);

public sealed class OfficialInvoiceReceiptReviewInput
{
    public bool Accept { get; set; }
    public string BankStatementEvidenceReference { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed record OfficialInvoiceReceiptReviewRead(
    Guid ReviewId, Guid ReceivablePostingId, Guid ReceiptAllocationId,
    bool Accepted, decimal Amount, bool IsExternallyVerifiedBankTransfer);

public sealed record OfficialContractDebtPortfolioRead(
    Guid ContractId, Guid BuildingId, DateOnly AsOfDate, int InvoiceCount,
    decimal TotalAdjustedReceivables,
    decimal IndependentlyReviewedManualPayments,
    decimal OutstandingAmount, int OverdueInvoiceCount,
    IReadOnlyList<OfficialInvoiceReceivableBalance> Invoices,
    bool IsOfficialDebtPosted, bool IsExternallyVerifiedBankTransfer)
{
    public decimal InternalDepositOffsetVnd { get; init; }
}

public sealed class SliceBDebtDisputeInput
{
    public Guid PostingId { get; set; }
    public string? Reason { get; set; }
    public string? EvidenceReference { get; set; }
}

public sealed class SliceBDebtDisputeResolutionInput
{
    public bool DebtAccepted { get; set; }
    public string? SignedOwnerResolutionReference { get; set; }
}

public sealed record SliceBDebtDisputeStatusRead(
    Guid ContractId, Guid MoveOutRequestId,
    long OpenCount, long RejectedCount, long AcceptedCount,
    bool CanConsiderDepositOffset);

public sealed class SliceBRefundDestinationApprovalInput
{
    public string? BeneficiaryName { get; set; }
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? SignedDestinationReference { get; set; }
    public string? HolidayCalendarReference { get; set; }
    public IReadOnlyList<DateOnly> StatutoryHolidays { get; set; } =
        Array.Empty<DateOnly>();
}

public sealed class SliceBRefundTransferFailureInput
{
    public string? BankReference { get; set; }
    public string? Reason { get; set; }
    public string? BankFailureEvidenceReference { get; set; }
}

public sealed record SliceBRefundOperatingStatusRead(
    Guid ContractId, Guid MoveOutRequestId, decimal ApprovedRefundVnd,
    DateOnly? DueOn, ContractDepositRefundOperatingStatus Status,
    long FailedTransferCount, bool IsIndependentlyVerified);

public sealed class SliceBDepositSettlementApprovalInput
{
    public decimal ForfeitAmount { get; set; }
    public string? DamageReason { get; set; }
    public string? DamageEvidenceReference { get; set; }
    public string DepositPolicyReference { get; set; } = string.Empty;
}

public sealed record SliceBMonthlyReceiptLineSplitInput(
    string SignedAllocationReference,
    IReadOnlyList<SliceBMonthlyReceiptLinePart> Lines);

public sealed record SliceBMonthlyReceiptLinePart(
    ContractDepositOffsetCategory Category,
    decimal AmountVnd);

public sealed record SliceBMonthlyReceiptLineSplitRead(
    Guid PostingId,
    Guid ReceiptReviewId,
    int ChargeLineCount,
    decimal AllocatedAmountVnd,
    bool IsBankExternallyVerified,
    bool CanPostDepositOffset);

public sealed record SliceBOffsetDebtReviewInput(
    string SignedReviewReference, DateOnly? AsOfDate);

public sealed record SliceBOwnerDebtOffsetApprovalInput(
    string SignedPolicyReference,
    IReadOnlyList<ContractDepositOwnerTierAllocation>? OwnerTierAllocations = null);

public sealed record SliceBOffsetDebtReviewRead(
    Guid ReviewId, Guid MoveOutRequestId,
    decimal VerifiedOutstandingVnd,
    bool IsIndependentDebtReview, bool CanPostDepositOffset);

public sealed record SliceBOwnerDebtOffsetApprovalRead(
    Guid ApprovalId, Guid ContractId,
    decimal ApprovedOffsetVnd,
    decimal ExpectedRemainingDepositVnd,
    decimal ExpectedUncoveredDebtVnd,
    bool IsMoneyPosted, bool CanCompleteMoveOut);

public sealed record SliceBDebtOffsetPostingRead(
    Guid PostingId, Guid ApprovalId, Guid ContractId, Guid MoveOutRequestId,
    decimal DebitHeldDepositVnd, decimal CreditOfficialDebtVnd,
    decimal RemainingHeldDepositVnd, decimal RemainingOfficialDebtVnd,
    string PostingSha256, bool IsInternalOffsetPosted,
    bool IsBankRefundExecuted, bool CanCompleteMoveOut);

public sealed record SliceBDepositSettlementDecisionRead(
    Guid DecisionId, Guid ContractId, Guid MoveOutRequestId,
    Guid FinalIssuedInvoiceId, decimal VerifiedHeldAmount,
    decimal ApprovedForfeitAmount, decimal ApprovedRefundAmount,
    decimal ApprovedDebtOffsetAmount, DateTime ApprovedAtUtc,
    bool IsRefundExecuted, bool IsAutomaticDebtOffset);
