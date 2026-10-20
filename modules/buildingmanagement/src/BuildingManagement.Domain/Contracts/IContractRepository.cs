using System;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Contracts;

public interface IContractRepository
    : IRepository<Contract, Guid>
{
    Task<Contract?> FindForUpdateAsync(
        Guid id
    );
}