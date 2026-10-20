namespace BuildingManagement.DepositSettlements;

public enum DepositSettlementType
{
    Refund = 1,

    Forfeit = 2,

    AllocateToContractDeposit = 3,

    ContractTransfer = 4,

    RoomChangeCarryForward = 5,

    RentCredit = 6

//Refund
//→ hoàn cọc cho khách

//Forfeit
//→ khách mất cọc

//AllocateToContractDeposit
//→ tiền giữ phòng / tiền thanh toán
//  được tính vào cọc hợp đồng

//ContractTransfer
//→ quyền cọc A → B

//RoomChangeCarryForward
//→ cọc phòng cũ → hợp đồng phòng mới

//RentCredit
//→ phần cọc dư được cấn tiền phòng tháng sau
}