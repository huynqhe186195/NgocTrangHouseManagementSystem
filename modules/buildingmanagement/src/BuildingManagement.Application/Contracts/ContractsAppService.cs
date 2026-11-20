using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using BuildingManagement.Tenants;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;

namespace BuildingManagement.Contracts
{
    /// <summary>
    /// The admin screen's contract read. Two methods, both reads.
    ///
    /// <see cref="ContractAppService.GetListAsync"/> returns every contract in
    /// the database with no predicate at all, so it must never be HTTP-exposed.
    /// This service is what the wire gets instead: it asks the access policy for
    /// the buildings the caller may see and then joins Contract -> Room -> Floor
    /// -> Building, the same shape <see cref="RoomAppService.GetListAsync"/> uses.
    /// </summary>
    public class ContractsAppService :
        ApplicationService,
        IContractsAppService
    {
        private readonly IRepository<Contract, Guid> _contractRepository;
        private readonly IRepository<ContractTenant, Guid> _contractTenantRepository;
        private readonly IRepository<Room, Guid> _roomRepository;
        private readonly IRepository<Floor, Guid> _floorRepository;
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IRepository<Tenant, Guid> _tenantRepository;
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly IAsyncQueryableExecuter _queryExecuter;

        public ContractsAppService(
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> contractTenantRepository,
            IRepository<Room, Guid> roomRepository,
            IRepository<Floor, Guid> floorRepository,
            IRepository<Building, Guid> buildingRepository,
            IRepository<Tenant, Guid> tenantRepository,
            IBuildingCrudAccessPolicy access,
            IAsyncQueryableExecuter queryExecuter)
        {
            _contractRepository = contractRepository;
            _contractTenantRepository = contractTenantRepository;
            _roomRepository = roomRepository;
            _floorRepository = floorRepository;
            _buildingRepository = buildingRepository;
            _tenantRepository = tenantRepository;
            _access = access;
            _queryExecuter = queryExecuter;
        }

        public async Task<List<ContractSummaryDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadContract);

            var contracts = await _contractRepository.GetQueryableAsync();
            var rooms = await _roomRepository.GetQueryableAsync();
            var floors = await _floorRepository.GetQueryableAsync();
            var buildings = await _buildingRepository.GetQueryableAsync();

            var scoped = await _queryExecuter.ToListAsync(
                from contract in contracts
                join room in rooms on contract.RoomId equals room.Id
                join floor in floors on room.FloorId equals floor.Id
                join building in buildings on floor.BuildingId equals building.Id
                where authorized.Contains(building.Id)
                    && !contract.IsDeleted && !room.IsDeleted
                    && !floor.IsDeleted && !building.IsDeleted
                orderby contract.StartDate descending, contract.ContractNumber
                select new
                {
                    Contract = contract,
                    Room = room,
                    Floor = floor,
                    Building = building
                });

            if (scoped.Count == 0)
                return new List<ContractSummaryDto>();

            var tenantRows = await LoadTenantsAsync(
                scoped.Select(x => x.Contract.Id).ToList());

            return scoped
                .Select(x => Map(
                    x.Contract, x.Room, x.Floor, x.Building,
                    tenantRows.GetValueOrDefault(x.Contract.Id)))
                .ToList();
        }

        public async Task<ContractSummaryDto> GetAsync(Guid id)
        {
            // Contract, not Room: knowing a room exists is a different fact from
            // knowing who rents it and for how much. The policy also proves the
            // contract's building is one the caller holds a grant on, so a GUID
            // cannot be used to probe another building's contracts.
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract, id,
                BuildingBusinessCapability.ReadContract);

            var contract = await _contractRepository.FindAsync(id);
            if (contract is null)
                throw new Volo.Abp.Domain.Entities.EntityNotFoundException(
                    typeof(Contract), id);

            var room = await _roomRepository.FindAsync(contract.RoomId);
            var floor = room is null ? null : await _floorRepository.FindAsync(room.FloorId);
            var building = floor is null
                ? null
                : await _buildingRepository.FindAsync(floor.BuildingId);

            // The scope reader already walked this graph; a null here means the
            // row moved between the check and the read.
            if (room is null || floor is null || building is null)
                throw new Volo.Abp.Authorization.AbpAuthorizationException(
                    "BuildingManagement: contract scope changed.");

            var tenants = await LoadTenantsAsync(new List<Guid> { id });

            return Map(contract, room, floor, building,
                tenants.GetValueOrDefault(id));
        }

        private async Task<Dictionary<Guid, List<ContractTenantSummaryDto>>>
            LoadTenantsAsync(List<Guid> contractIds)
        {
            var links = await _contractTenantRepository.GetQueryableAsync();
            var tenants = await _tenantRepository.GetQueryableAsync();

            var rows = await _queryExecuter.ToListAsync(
                from link in links
                join tenant in tenants on link.TenantId equals tenant.Id
                // A tenant who moves out is removed from the contract by a soft
                // delete on the link (ContractAppService does exactly that), so
                // filtering only the tenant would keep a departed name on screen.
                where contractIds.Contains(link.ContractId)
                    && !link.IsDeleted && !tenant.IsDeleted
                orderby link.Role, tenant.FullName
                select new
                {
                    link.ContractId,
                    Summary = new ContractTenantSummaryDto
                    {
                        TenantId = link.TenantId,
                        FullName = tenant.FullName,
                        Role = link.Role
                    }
                });

            return rows
                .GroupBy(x => x.ContractId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Summary).ToList());
        }

        private static ContractSummaryDto Map(
            Contract contract, Room room, Floor floor, Building building,
            List<ContractTenantSummaryDto>? tenants)
            => new()
            {
                Id = contract.Id,
                CreationTime = contract.CreationTime,
                CreatorId = contract.CreatorId,
                LastModificationTime = contract.LastModificationTime,
                LastModifierId = contract.LastModifierId,
                ContractNumber = contract.ContractNumber,
                Status = contract.Status,
                StartDate = contract.StartDate,
                EndDate = contract.EndDate,
                ActualEndedAt = contract.ActualEndedAt,
                EndReason = contract.EndReason,
                MonthlyRent = contract.MonthlyRent,
                DepositAmount = contract.DepositAmount,
                Notes = contract.Notes,
                RoomId = room.Id,
                RoomNumber = room.RoomNumber,
                FloorId = floor.Id,
                FloorName = floor.Name,
                BuildingId = building.Id,
                BuildingName = building.Name,
                Tenants = tenants ?? new List<ContractTenantSummaryDto>()
            };
    }
}
