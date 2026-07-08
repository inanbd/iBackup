using iBackup.Server.Application.Features.Dashboard;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iBackup.Server.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly ISender _sender;

    public DashboardController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Aggregated account overview for the dashboard page.</summary>
    [HttpGet]
    public async Task<DashboardResponse> Get(CancellationToken ct)
        => await _sender.Send(new GetDashboardQuery(), ct);
}
