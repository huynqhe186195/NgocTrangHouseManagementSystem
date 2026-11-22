using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Mobile;
using BuildingManagement.RentalAccounts;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;

namespace BuildingManagement.Application.Mobile;

[RemoteService(IsEnabled = false)]
public class MobileTenantAppService : ApplicationService, IMobileTenantAppService
{
    private readonly IRepository<RentalAccountUser, Guid> _accountUserRepository;
    private readonly IRepository<RentalAccount, Guid> _accountRepository;
    private readonly IRepository<Contract, Guid> _contractRepository;
    private readonly IRepository<Room, Guid> _roomRepository;
    private readonly IRepository<Floor, Guid> _floorRepository;
    private readonly IRepository<Building, Guid> _buildingRepository;
    private readonly IRepository<ContractTenant, Guid> _contractTenantRepository;
    private readonly IRepository<Tenant, Guid> _tenantRepository;
    private readonly ICurrentUser _currentUser;

    public MobileTenantAppService(
        IRepository<RentalAccountUser, Guid> accountUserRepository,
        IRepository<RentalAccount, Guid> accountRepository,
        IRepository<Contract, Guid> contractRepository,
        IRepository<Room, Guid> roomRepository,
        IRepository<Floor, Guid> floorRepository,
        IRepository<Building, Guid> buildingRepository,
        IRepository<ContractTenant, Guid> contractTenantRepository,
        IRepository<Tenant, Guid> tenantRepository,
        ICurrentUser currentUser)
    {
        _accountUserRepository = accountUserRepository;
        _accountRepository = accountRepository;
        _contractRepository = contractRepository;
        _roomRepository = roomRepository;
        _floorRepository = floorRepository;
        _buildingRepository = buildingRepository;
        _contractTenantRepository = contractTenantRepository;
        _tenantRepository = tenantRepository;
        _currentUser = currentUser;
    }

    public async Task<TenantMobileOverviewDto> GetMeAsync()
    {
        if (!_currentUser.IsAuthenticated ||
            !_currentUser.Id.HasValue ||
            _currentUser.Id.Value == Guid.Empty)
        {
            throw Denied();
        }

        var identityUserId = _currentUser.Id.Value;
        var accountUser = await _accountUserRepository.FirstOrDefaultAsync(
            x => x.IdentityUserId == identityUserId && x.IsActive);

        if (accountUser is null)
        {
            throw Denied();
        }

        var account = await _accountRepository.FirstOrDefaultAsync(
            x => x.Id == accountUser.RentalAccountId && x.IsActive);

        if (account is null)
        {
            throw Denied();
        }

        var activeContract = (await _contractRepository.GetListAsync(
                x => x.RentalAccountId == account.Id &&
                     x.Status == ContractStatus.Active))
            .OrderByDescending(x => x.StartDate)
            .ThenByDescending(x => x.CreationTime)
            .FirstOrDefault();

        var currentUser = new TenantMobileUserDto
        {
            IdentityUserId = identityUserId,
            TenantId = accountUser.TenantId,
            AccessRole = accountUser.AccessRole
        };

        var members = new List<TenantMobileMemberDto>();
        Tenant? currentTenant = null;

        if (activeContract is not null)
        {
            var room = await _roomRepository.FirstOrDefaultAsync(
                x => x.Id == activeContract.RoomId);
            var floor = room is null
                ? null
                : await _floorRepository.FirstOrDefaultAsync(x => x.Id == room.FloorId);
            var building = floor is null
                ? null
                : await _buildingRepository.FirstOrDefaultAsync(x => x.Id == floor.BuildingId);

            // An account without a complete room graph is not a valid mobile lease.
            if (room is null || floor is null || building is null)
            {
                throw Denied();
            }

            var links = await _contractTenantRepository.GetListAsync(
                x => x.ContractId == activeContract.Id);
            var tenantIds = links.Select(x => x.TenantId).Distinct().ToArray();
            var tenants = tenantIds.Length == 0
                ? new List<Tenant>()
                : await _tenantRepository.GetListAsync(x => tenantIds.Contains(x.Id));
            var tenantsById = tenants.ToDictionary(x => x.Id);

            foreach (var link in links)
            {
                if (!tenantsById.TryGetValue(link.TenantId, out var tenant))
                {
                    continue;
                }

                members.Add(new TenantMobileMemberDto
                {
                    TenantId = tenant.Id,
                    DisplayName = tenant.FullName,
                    Role = link.Role.ToString()
                });

                if (accountUser.TenantId == tenant.Id)
                {
                    currentTenant = tenant;
                }
            }

            currentUser.DisplayName = currentTenant?.FullName;

            return new TenantMobileOverviewDto
            {
                CurrentUser = currentUser,
                Account = new TenantMobileAccountDto
                {
                    Id = account.Id,
                    AccountNumber = account.AccountNumber,
                    DisplayName = account.DisplayName,
                    IsActive = account.IsActive
                },
                ActiveContract = new TenantMobileContractDto
                {
                    Id = activeContract.Id,
                    ContractNumber = activeContract.ContractNumber,
                    StartDate = activeContract.StartDate,
                    EndDate = activeContract.EndDate,
                    Room = new TenantMobileRoomDto
                    {
                        Id = room.Id,
                        RoomNumber = room.RoomNumber,
                        Name = room.Name,
                        FloorId = floor.Id,
                        FloorName = floor.Name,
                        BuildingId = building.Id,
                        BuildingName = building.Name
                    }
                },
                Members = members
            };
        }

        // A mapped account with no active lease is intentionally non-global: it
        // returns only the account and current-user link, never unrelated data.
        if (accountUser.TenantId.HasValue)
        {
            currentTenant = await _tenantRepository.FirstOrDefaultAsync(
                x => x.Id == accountUser.TenantId.Value);
        }

        currentUser.DisplayName = currentTenant?.FullName;

        return new TenantMobileOverviewDto
        {
            CurrentUser = currentUser,
            Account = new TenantMobileAccountDto
            {
                Id = account.Id,
                AccountNumber = account.AccountNumber,
                DisplayName = account.DisplayName,
                IsActive = account.IsActive
            },
            ActiveContract = null,
            Members = members
        };
    }

    private static AbpAuthorizationException Denied()
        => new("BuildingManagement: rental account is not authorized.");
}
