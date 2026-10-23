namespace BuildingManagement.Utilities;

public static class MeterReplacementErrorCodes
{
    public const string OldMeterMustBeActive =
        "BuildingManagement:Utilities:ReplacementOldMeterMustBeActive";
    public const string InvalidReplacementDate =
        "BuildingManagement:Utilities:InvalidReplacementDate";
    public const string InvalidClosingReading =
        "BuildingManagement:Utilities:InvalidReplacementClosingReading";
    public const string InvalidNewInitialReading =
        "BuildingManagement:Utilities:InvalidReplacementNewInitialReading";
    public const string ReasonRequired =
        "BuildingManagement:Utilities:ReplacementReasonRequired";
    public const string InvalidReasonOrNotes =
        "BuildingManagement:Utilities:InvalidReplacementReasonOrNotes";
    public const string MeterReferencedByReplacement =
        "BuildingManagement:Utilities:MeterReferencedByReplacement";
    public const string ReadingLockedByReplacement =
        "BuildingManagement:Utilities:ReadingLockedByReplacement";
    public const string MeterIdentityLockedByReplacement =
        "BuildingManagement:Utilities:MeterIdentityLockedByReplacement";
    public const string SoftDeletedHistoryRequiresReview =
        "BuildingManagement:Utilities:ReplacementSoftDeletedHistoryRequiresReview";
    public const string ReplacementNotFound =
        "BuildingManagement:Utilities:MeterReplacementNotFound";
}
