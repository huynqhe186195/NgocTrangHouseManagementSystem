using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.MoveOuts;

public interface IMoveOutRequestAppService : IApplicationService
{
    Task<MoveOutRequestDto> CreateAsync(CreateMoveOutRequestDto input);
    Task<MoveOutRequestDto> GetAsync(Guid id);
    Task<List<MoveOutRequestDto>> GetByContractAsync(Guid contractId);
    Task<List<MoveOutChecklistTaskDto>> GetChecklistAsync(Guid id);
    Task<MoveOutRequestDto> ApproveAsync(Guid id, ApproveMoveOutRequestDto input);
    Task<MoveOutRequestDto> RejectAsync(Guid id, RejectMoveOutRequestDto input);
    Task<MoveOutRequestDto> CancelAsync(Guid id);
}
