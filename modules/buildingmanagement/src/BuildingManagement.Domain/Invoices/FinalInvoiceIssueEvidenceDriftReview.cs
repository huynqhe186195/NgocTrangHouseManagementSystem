using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;

namespace BuildingManagement.Invoices;

/// <summary>
/// Compares a staged, persisted final-invoice evidence record with the
/// CURRENT persisted draft-source graph. Only consistency is evaluated:
/// this is not authorization, an issued invoice or an outstanding debt.
/// </summary>
public sealed class FinalInvoiceIssueEvidenceDriftReview
{
    public Guid EvidenceRecordId { get; init; }
    public Guid DraftInvoiceId { get; init; }
    public Guid ContractId { get; init; }
    public bool MatchesCurrentDraft { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanIssue => false;
    public bool IsReceivablePosted => false;
}

public static class FinalInvoiceIssueEvidenceDriftEvaluator
{
    public static FinalInvoiceIssueEvidenceDriftReview Assess(
        FinalInvoiceIssueEvidenceRecord archived,
        MoveOutRequest? request,
        Contract? contract,
        IReadOnlyCollection<Invoice> periodDrafts,
        IReadOnlyCollection<InvoiceItem> items,
        IReadOnlyCollection<InvoiceItemMeterSource> meterSources)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(periodDrafts);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(meterSources);

        var result = new FinalInvoiceIssueEvidenceDriftReview
        {
            EvidenceRecordId = archived.Id,
            DraftInvoiceId = archived.DraftInvoiceId,
            ContractId = archived.ContractId
        };

        // An archived digest can match its own JSON while the actual
        // invoice/meter-source tables have changed. Verify archived
        // semantics first, then independently rebuild the CURRENT graph.
        DateTime originalCaptureTimeUtc;
        try
        {
            archived.VerifyStoredEvidence();
            using var json = JsonDocument.Parse(archived.EvidenceJson);
            originalCaptureTimeUtc = json.RootElement
                .GetProperty("CapturedAtUtc").GetDateTime();
            if (originalCaptureTimeUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Expected UTC evidence capture.");
        }
        catch (InvalidDataException)
        {
            result.Blockers.Add("ArchivedEvidenceInvalid");
            return Finish(result);
        }

        if (request is null ||
            request.Id != archived.MoveOutRequestId ||
            request.ContractId != archived.ContractId ||
            request.RoomId != archived.RoomId ||
            request.Status != MoveOutRequestStatus.Approved ||
            request.ApprovedMoveOutDate is not DateOnly date ||
            date.Year != archived.BillingYear ||
            date.Month != archived.BillingMonth ||
            date.Day != DateTime.DaysInMonth(date.Year, date.Month) ||
            contract is null ||
            contract.Id != archived.ContractId ||
            contract.RoomId != archived.RoomId ||
            contract.Status != ContractStatus.Active)
        {
            result.Blockers.Add("MoveOutOrContractScopeChanged");
            return Finish(result);
        }

        if (periodDrafts.Count != 1 ||
            periodDrafts.Single().Id != archived.DraftInvoiceId ||
            periodDrafts.Single().Status != InvoiceStatus.Draft ||
            periodDrafts.Single().ContractId != archived.ContractId ||
            periodDrafts.Single().RoomId != archived.RoomId ||
            periodDrafts.Single().BillingYear != archived.BillingYear ||
            periodDrafts.Single().BillingMonth != archived.BillingMonth)
        {
            result.Blockers.Add("CurrentDraftMissingAmbiguousOrChanged");
            return Finish(result);
        }

        try
        {
            var current = FinalInvoiceEvidencePackage.Capture(
                request, contract, periodDrafts.Single(),
                items, meterSources, originalCaptureTimeUtc);

            if (!string.Equals(current.EvidenceSha256,
                    archived.EvidenceSha256, StringComparison.Ordinal) ||
                !string.Equals(current.EvidenceJson,
                    archived.EvidenceJson, StringComparison.Ordinal))
                result.Blockers.Add("ArchivedEvidenceDiffersFromCurrentDraft");
            else
                result.MatchesCurrentDraft = true;
        }
        catch (InvalidOperationException)
        {
            result.Blockers.Add("CurrentDraftEvidenceInvalid");
        }

        return Finish(result);
    }

    private static FinalInvoiceIssueEvidenceDriftReview Finish(
        FinalInvoiceIssueEvidenceDriftReview result)
    {
        // A valid current snapshot is NEVER an issuance authorization.
        result.Blockers.Add("InvoiceIssuanceAndDebtPostingNotImplemented");
        return result;
    }
}
