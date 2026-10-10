namespace BuildingManagement;

public static class BuildingManagementErrorCodes
{
    public const string InvalidRoomNumber =
        "BuildingManagement:InvalidRoomNumber";

    public const string RoomNumberDoesNotMatchFloor =
        "BuildingManagement:RoomNumberDoesNotMatchFloor";

    public const string RoomNumberAlreadyExists =
        "BuildingManagement:RoomNumberAlreadyExists";

    public const string FloorNumberAlreadyExists =
        "BuildingManagement:FloorNumberAlreadyExists";

    public const string BuildingNotFound =
        "BuildingManagement:BuildingNotFound";

    public const string BuildingHasFloors =
        "BuildingManagement:BuildingHasFloors";

    public const string FloorHasRooms =
        "BuildingManagement:FloorHasRooms";

    public const string FloorNotFound =
        "BuildingManagement:FloorNotFound";

    public const string RoomNotFound =
        "BuildingManagement:RoomNotFound";
    public const string TenantNotFound =
        "BuildingManagement:TenantNotFound";
    public const string TenantIdentityAlreadyExists =
        "BuildingManagement:TenantIdentityAlreadyExists";
    public const string InvalidTenantIdentityInformation =
        "BuildingManagement:InvalidTenantIdentityInformation";
    public const string ContractNotFound =
    "BuildingManagement:ContractNotFound";

    public const string ContractNumberAlreadyExists =
        "BuildingManagement:ContractNumberAlreadyExists";

    public const string InvalidContractPeriod =
        "BuildingManagement:InvalidContractPeriod";

    public const string InvalidMonthlyRent =
        "BuildingManagement:InvalidMonthlyRent";

    public const string InvalidDepositAmount =
        "BuildingManagement:InvalidDepositAmount";

    public const string TenantAlreadyInContract =
        "BuildingManagement:TenantAlreadyInContract";

    public const string ContractTenantNotFound =
        "BuildingManagement:ContractTenantNotFound";

    public const string ContractAlreadyHasPrimaryTenant =
        "BuildingManagement:ContractAlreadyHasPrimaryTenant";

    public const string InvalidContractTenantRole =
        "BuildingManagement:InvalidContractTenantRole";

    public const string ContractCannotBeActivated =
    "BuildingManagement:ContractCannotBeActivated";

    public const string ContractCannotBeEnded =
        "BuildingManagement:ContractCannotBeEnded";

    public const string ContractCannotBeCancelled =
        "BuildingManagement:ContractCannotBeCancelled";

    public const string ContractRequiresTenant =
        "BuildingManagement:ContractRequiresTenant";

    public const string ContractRequiresPrimaryTenant =
        "BuildingManagement:ContractRequiresPrimaryTenant";

    public const string RoomAlreadyHasActiveContract =
        "BuildingManagement:RoomAlreadyHasActiveContract";

    public const string RoomNotAvailableForRent =
        "BuildingManagement:RoomNotAvailableForRent";

    public const string ContractCanOnlyBeModifiedWhenDraft =
        "BuildingManagement:ContractCanOnlyBeModifiedWhenDraft";

    public const string RoomHasActiveContract =
        "BuildingManagement:RoomHasActiveContract";

    public const string TenantHasActiveContract =
        "BuildingManagement:TenantHasActiveContract";

    public const string TenantHasContractHistory =
        "BuildingManagement:TenantHasContractHistory";
    public const string UtilityRateNotFound =
    "BuildingManagement:UtilityRateNotFound";

    public const string InvalidUtilityType =
        "BuildingManagement:InvalidUtilityType";

    public const string InvalidUtilityUnitPrice =
        "BuildingManagement:InvalidUtilityUnitPrice";

    public const string InvalidUtilityRatePeriod =
        "BuildingManagement:InvalidUtilityRatePeriod";

    public const string UtilityRatePeriodOverlaps =
        "BuildingManagement:UtilityRatePeriodOverlaps";

    public const string UtilityMeterNotFound =
    "BuildingManagement:UtilityMeterNotFound";

    public const string InvalidMeterCode =
        "BuildingManagement:InvalidMeterCode";

    public const string InvalidInitialMeterReading =
        "BuildingManagement:InvalidInitialMeterReading";

    public const string MeterCodeAlreadyExists =
        "BuildingManagement:MeterCodeAlreadyExists";

    public const string ActiveUtilityMeterAlreadyExists =
        "BuildingManagement:ActiveUtilityMeterAlreadyExists";

    public const string UtilityMeterAlreadyInactive =
        "BuildingManagement:UtilityMeterAlreadyInactive";

    public const string RoomHasActiveUtilityMeter =
        "BuildingManagement:RoomHasActiveUtilityMeter";

    public const string MeterReadingNotFound =
        "BuildingManagement:MeterReadingNotFound";

    public const string UtilityMeterInactive =
        "BuildingManagement:UtilityMeterInactive";

    public const string InvalidBillingYear =
        "BuildingManagement:InvalidBillingYear";

    public const string InvalidBillingMonth =
        "BuildingManagement:InvalidBillingMonth";

    public const string InvalidReadingDate =
        "BuildingManagement:InvalidReadingDate";

    public const string InvalidCurrentReading =
        "BuildingManagement:InvalidCurrentReading";

    public const string MeterReadingAlreadyExists =
        "BuildingManagement:MeterReadingAlreadyExists";

    public const string MeterReadingPeriodMustBeAfterLatest =
        "BuildingManagement:MeterReadingPeriodMustBeAfterLatest";

    public const string OnlyLatestMeterReadingCanBeUpdated =
        "BuildingManagement:OnlyLatestMeterReadingCanBeUpdated";

    public const string UtilityMeterHasReadingHistory =
        "BuildingManagement:UtilityMeterHasReadingHistory";

    public const string ContractCannotBeSigned =
        "BuildingManagement:ContractCannotBeSigned";

    public const string ContractCannotBeRenewed =
        "BuildingManagement:ContractCannotBeRenewed";

    public const string ContractAlreadyRenewed =
        "BuildingManagement:ContractAlreadyRenewed";

    public const string InvalidRenewalContractPeriod =
        "BuildingManagement:InvalidRenewalContractPeriod";

    public const string RoomHasOverlappingCommittedContract =
        "BuildingManagement:RoomHasOverlappingCommittedContract";

    public const string ContractRenewalHoldNotFound =
        "BuildingManagement:ContractRenewalHoldNotFound";

    public const string ContractRenewalHoldAlreadyExists =
        "BuildingManagement:ContractRenewalHoldAlreadyExists";

    public const string ContractRenewalHoldCannotBeCreated =
        "BuildingManagement:ContractRenewalHoldCannotBeCreated";

    public const string ContractRenewalHoldCannotBeCancelled =
        "BuildingManagement:ContractRenewalHoldCannotBeCancelled";

    public const string ContractRenewalHoldExpired =
        "BuildingManagement:ContractRenewalHoldExpired";

    public const string ContractRenewalHoldRequired =
    "BuildingManagement:ContractRenewalHoldRequired";

    public const string ContractRenewalHoldNotActive =
        "BuildingManagement:ContractRenewalHoldNotActive";

    public const string RoomReservationNotFound =
    "BuildingManagement:RoomReservationNotFound";

    public const string RoomReservationNumberAlreadyExists =
        "BuildingManagement:RoomReservationNumberAlreadyExists";

    public const string InvalidRoomReservationNumber =
        "BuildingManagement:InvalidRoomReservationNumber";

    public const string InvalidExpectedMoveInDate =
        "BuildingManagement:InvalidExpectedMoveInDate";

    public const string InvalidQuotedMonthlyRent =
        "BuildingManagement:InvalidQuotedMonthlyRent";

    public const string InvalidRequiredDepositAmount =
        "BuildingManagement:InvalidRequiredDepositAmount";

    public const string RoomNotAvailableForReservation =
        "BuildingManagement:RoomNotAvailableForReservation";

    public const string RoomAlreadyReserved =
        "BuildingManagement:RoomAlreadyReserved";

    public const string RoomReservationAlreadyExistsForTenant =
        "BuildingManagement:RoomReservationAlreadyExistsForTenant";

    public const string RoomReservationCannotBeCancelled =
        "BuildingManagement:RoomReservationCannotBeCancelled";

    public const string InvalidReservationCancellationReason =
        "BuildingManagement:InvalidReservationCancellationReason";

    public const string InvalidMinimumReservationDepositAmount =
        "BuildingManagement:InvalidMinimumReservationDepositAmount";
} // Consolidate all of the module's error codes in one place.
