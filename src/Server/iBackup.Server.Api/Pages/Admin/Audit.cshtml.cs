using iBackup.Server.Application.Features.Admin;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class AuditModel : PageModel
{
    private readonly ISender _sender;

    public AuditModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty(SupportsGet = true)]
    public string? Email { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public PagedResult<AdminAuditRow> Entries { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Entries = await _sender.Send(new GetAuditLogQuery(Email, Action, Math.Max(1, P)), ct);
    }
}
