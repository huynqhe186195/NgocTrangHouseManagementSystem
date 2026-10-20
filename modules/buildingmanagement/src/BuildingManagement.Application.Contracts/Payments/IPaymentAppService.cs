using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Payments
{
    public interface IPaymentAppService
        : IApplicationService
    {
        Task<PaymentDto> GetAsync(Guid id);

        Task<List<PaymentDto>> GetListAsync();

        Task<PaymentDto> CreateReservationDepositAsync(
            CreateReservationDepositPaymentDto input
        );

        Task<PaymentDto> CompleteAsync(
            Guid id,
            CompletePaymentDto input
        );

        Task<PaymentDto> CancelAsync(Guid id);

        Task<PaymentDto> CreateContractDepositAsync(CreateContractDepositPaymentDto input);

        Task<ContractDepositAllocationDto> AllocateReservationDepositToContractAsync(
            Guid reservationId,
            Guid contractId);

        Task<ReservationDepositSettlementDto> SettleCancelledReservationDepositAsync(Guid reservationId);
    }
}