using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;

namespace BuildingManagement.Tenants
{
    /// <summary>
    /// The admin "Khách thuê" read. Two methods, both reads.
    ///
    /// <see cref="TenantAppService.GetListAsync"/> returns every tenant in the
    /// database with no predicate, and the service also writes, so it must stay
    /// HTTP-denied. This service is what the wire gets instead.
    ///
    /// A tenant has no building of its own — one person may rent in several —
    /// so the scope rule is: a tenant is visible when at least one of their
    /// non-deleted contracts sits in a building the caller holds a grant on.
    /// The contracts shown are filtered the same way, so a shared building
    /// never reveals where else that person rents.
    /// </summary>
    public class TenantsAppService :
        ApplicationService,
        ITenantsAppService
    {
        private readonly IRepository<Tenant, Guid> _tenantRepository;
        private readonly IRepository<Contract, Guid> _contractRepository;
        private readonly IRepository<ContractTenant, Guid> _linkRepository;
        private readonly IRepository<Room, Guid> _roomRepository;
        private readonly IRepository<Floor, Guid> _floorRepository;
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly IAsyncQueryableExecuter _queryExecuter;

        public TenantsAppService(
            IRepository<Tenant, Guid> tenantRepository,
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> linkRepository,
            IRepository<Room, Guid> roomRepository,
            IRepository<Floor, Guid> floorRepository,
            IRepository<Building, Guid> buildingRepository,
            IBuildingCrudAccessPolicy access,
            IAsyncQueryableExecuter queryExecuter)
        {
            _tenantRepository = tenantRepository;
            _contractRepository = contractRepository;
            _linkRepository = linkRepository;
            _roomRepository = roomRepository;
            _floorRepository = floorRepository;
            _buildingRepository = buildingRepository;
            _access = access;
            _queryExecuter = queryExecuter;
        }

        public async Task<List<TenantSummaryDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadTenant);

            var rows = await LoadScopedContractsAsync(authorized, null);

            if (rows.Count == 0)
                return new List<TenantSummaryDto>();

            var tenantIds = rows.Select(x => x.TenantId).Distinct().ToList();
            var tenants = await _tenantRepository.GetQueryableAsync();

            var people = await _queryExecuter.ToListAsync(
                from tenant in tenants
                where tenantIds.Contains(tenant.Id) && !tenant.IsDeleted
                orderby tenant.FullName
                select tenant);

            var byTenant = rows
                .GroupBy(x => x.TenantId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Contract).ToList());

            return people
                .Select(t => Map(t, byTenant.GetValueOrDefault(t.Id)))
                .ToList();
        }

        public async Task<TenantSummaryDto> GetAsync(Guid id)
        {
            // There is no resource-scope entry for a tenant: a tenant belongs to
            // no building, so "is this tenant in my buildings" can only be
            // answered by the links themselves. The grants are resolved first so
            // an unauthorized caller cannot tell an existing tenant id from a
            // missing one.
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadTenant);

            var rows = await LoadScopedContractsAsync(authorized, id);
            if (rows.Count == 0)
            {
                // Same exception shape as a missing row: a tenant that exists
                // only outside this caller's buildings must not be probeable.
                throw new Volo.Abp.Domain.Entities.EntityNotFoundException(
                    typeof(Tenant), id);
            }

            var tenant = await _tenantRepository.FindAsync(id);
            if (tenant is null)
                throw new Volo.Abp.Domain.Entities.EntityNotFoundException(
                    typeof(Tenant), id);

            return Map(tenant, rows.Select(x => x.Contract).ToList());
        }

        private async Task<List<(Guid TenantId, TenantContractSummaryDto Contract)>>
            LoadScopedContractsAsync(
                IReadOnlyCollection<Guid> authorized, Guid? tenantFilter)
        {
            var links = await _linkRepository.GetQueryableAsync();
            var contracts = await _contractRepository.GetQueryableAsync();
            var rooms = await _roomRepository.GetQueryableAsync();
            var floors = await _floorRepository.GetQueryableAsync();
            var buildings = await _buildingRepository.GetQueryableAsync();

            var rows = await _queryExecuter.ToListAsync(
                from link in links
                join contract in contracts on link.ContractId equals contract.Id
                join room in rooms on contract.RoomId equals room.Id
                join floor in floors on room.FloorId equals floor.Id
                join building in buildings on floor.BuildingId equals building.Id
                // Moving a tenant out soft-deletes the LINK, so a departed
                // person must drop out of the roster on that signal alone.
                where authorized.Contains(building.Id)
                    && !link.IsDeleted && !contract.IsDeleted
                    && !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted
                    && (tenantFilter == null || link.TenantId == tenantFilter)
                orderby building.Name, floor.Name, room.RoomNumber
                select new
                {
                    link.TenantId,
                    Summary = new TenantContractSummaryDto
                    {
                        ContractId = contract.Id,
                        ContractNumber = contract.ContractNumber,
                        Status = (int)contract.Status,
                        EndDate = contract.EndDate,
                        RoomId = room.Id,
                        RoomNumber = room.RoomNumber,
                        FloorName = floor.Name,
                        BuildingId = building.Id,
                        BuildingName = building.Name
                    }
                });

            return rows
                .Select(x => (x.TenantId, x.Summary))
                .ToList();
        }

        private static TenantSummaryDto Map(
            Tenant tenant, List<TenantContractSummaryDto>? contracts)
            => new()
            {
                Id = tenant.Id,
                CreationTime = tenant.CreationTime,
                LastModificationTime = tenant.LastModificationTime,
                FullName = tenant.FullName,
                PhoneNumber = tenant.PhoneNumber,
                Email = tenant.Email,
                Gender = tenant.Gender,
                PermanentAddress = tenant.PermanentAddress,
                Notes = tenant.Notes,
                Contracts = contracts ?? new List<TenantContractSummaryDto>()
            };
    }
}
