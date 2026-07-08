using iBackup.Server.Application.Features.Admin;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class UsersModel : PageModel
{
    private readonly ISender _sender;

    public UsersModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public PagedResult<AdminUserRow> Users { get; private set; } = null!;

    [TempData]
    public string? Notice { get; set; }

    [TempData]
    public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Users = await _sender.Send(new GetAdminUsersQuery(Search, Math.Max(1, P)), ct);
    }

    public Task<IActionResult> OnPostQuotaAsync(Guid userId, int quotaGb, CancellationToken ct)
        => ExecuteAsync(new UpdateUserAccountCommand(userId, (long)quotaGb * 1024 * 1024 * 1024, null, null),
            $"Quota set to {quotaGb} GB.", ct);

    public Task<IActionResult> OnPostToggleActiveAsync(Guid userId, bool isActive, CancellationToken ct)
        => ExecuteAsync(new UpdateUserAccountCommand(userId, null, !isActive, null),
            isActive ? "Account disabled and sessions revoked." : "Account enabled.", ct);

    public Task<IActionResult> OnPostToggleAdminAsync(Guid userId, bool isAdmin, CancellationToken ct)
        => ExecuteAsync(new UpdateUserAccountCommand(userId, null, null, !isAdmin),
            isAdmin ? "Administrator access revoked." : "Administrator access granted.", ct);

    private async Task<IActionResult> ExecuteAsync(UpdateUserAccountCommand command, string notice, CancellationToken ct)
    {
        try
        {
            await _sender.Send(command, ct);
            Notice = notice;
        }
        catch (AppException ex)
        {
            Error = ex.Message;
        }
        catch (FluentValidation.ValidationException ex)
        {
            Error = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Invalid input.";
        }
        return RedirectToPage(new { Search, P });
    }
}
