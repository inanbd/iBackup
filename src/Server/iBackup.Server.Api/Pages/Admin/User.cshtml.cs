using iBackup.Server.Application.Features.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class UserModel : PageModel
{
    private readonly ISender _sender;

    public UserModel(ISender sender)
    {
        _sender = sender;
    }

    public AdminUserDetail Detail { get; private set; } = null!;

    public async Task OnGetAsync(Guid id, CancellationToken ct)
    {
        Detail = await _sender.Send(new GetAdminUserQuery(id), ct);
    }
}
