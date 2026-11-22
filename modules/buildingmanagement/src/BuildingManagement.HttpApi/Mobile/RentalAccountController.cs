using System.Threading.Tasks;
using BuildingManagement.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingManagement.HttpApi.Mobile;

[ApiController]
[Authorize]
[Route("api/mobile/rental-account")]
public sealed class RentalAccountController : ControllerBase
{
    private readonly IMobileTenantAppService _service;

    public RentalAccountController(IMobileTenantAppService service)
    {
        _service = service;
    }

    [HttpGet("me")]
    public Task<TenantMobileOverviewDto> GetMeAsync()
        => _service.GetMeAsync();
}
