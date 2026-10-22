namespace BuildingManagement.MoveOuts;

public static class MoveOutErrorCodes
{
    public const string NotFound = "BuildingManagement:MoveOut:NotFound";
    public const string ContractNotActive = "BuildingManagement:MoveOut:ContractNotActive";
    public const string InvalidMoveOutDate = "BuildingManagement:MoveOut:InvalidMoveOutDate";
    public const string InsufficientNotice = "BuildingManagement:MoveOut:InsufficientNotice";
    public const string OpenRequestAlreadyExists = "BuildingManagement:MoveOut:OpenRequestAlreadyExists";
    public const string CannotApprove = "BuildingManagement:MoveOut:CannotApprove";
    public const string CannotReject = "BuildingManagement:MoveOut:CannotReject";
    public const string CannotCancel = "BuildingManagement:MoveOut:CannotCancel";
    public const string RejectionReasonRequired = "BuildingManagement:MoveOut:RejectionReasonRequired";
    public const string TextTooLong = "BuildingManagement:MoveOut:TextTooLong";
}
