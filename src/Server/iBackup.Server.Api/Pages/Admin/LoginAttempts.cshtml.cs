using iBackup.Server.Application.Features.Admin;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class LoginAttemptsModel : PageModel
{
    private readonly ISender _sender;

    public LoginAttemptsModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty(SupportsGet = true)] public string? Ip { get; set; }
    [BindProperty(SupportsGet = true)] public string? Email { get; set; }
    [BindProperty(SupportsGet = true)] public bool FailuresOnly { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public PagedResult<LoginAttemptRow> Attempts { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Attempts = await _sender.Send(new GetLoginAttemptsQuery(Ip, Email, FailuresOnly, Math.Max(1, P)), ct);
    }
}
