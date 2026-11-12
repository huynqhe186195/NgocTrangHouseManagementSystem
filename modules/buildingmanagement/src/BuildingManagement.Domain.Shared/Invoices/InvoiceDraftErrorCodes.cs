namespace BuildingManagement.Invoices;

/// <summary>
/// Draft preparation only. No invoice issuance or debt recognition exists.
/// </summary>
public static class InvoiceDraftErrorCodes
{
    public const string NotFound = "BuildingManagement:InvoiceDraft:NotFound";
    public const string AlreadyExists = "BuildingManagement:InvoiceDraft:AlreadyExists";
    public const string CandidateNotReady = "BuildingManagement:InvoiceDraft:CandidateNotReady";
}
