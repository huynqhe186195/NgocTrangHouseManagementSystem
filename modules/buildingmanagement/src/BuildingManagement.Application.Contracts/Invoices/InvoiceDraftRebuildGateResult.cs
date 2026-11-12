namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only preflight outcome for a possible future versioned draft
/// correction. It is NOT a workflow approval or issuance authorization.
/// </summary>
public enum InvoiceDraftRebuildGateStatus
{
    ReadyForRevisionDesign = 0,
    AlreadyCurrent = 1,
    ReasonRequired = 2,
    SavedDraftInvalid = 3,
    DifferentBillingScope = 4,
    CurrentCandidateNotReady = 5
}

public sealed record InvoiceDraftRebuildGateResult(
    InvoiceDraftRebuildGateStatus Status,
    decimal? ProposedUtilityTotal = null,
    string? NormalizedReason = null)
{
    public bool CanPrepareRevision =>
        Status == InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign;
}
