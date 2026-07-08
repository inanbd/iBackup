using iBackup.Server.Application.Features.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly ISender _sender;

    public IndexModel(ISender sender)
    {
        _sender = sender;
    }

    public AdminOverview Overview { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Overview = await _sender.Send(new GetAdminOverviewQuery(), ct);
    }
}
