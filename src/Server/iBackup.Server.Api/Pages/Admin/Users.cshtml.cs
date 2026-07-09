using iBackup.Server.Application.Features.Admin;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

/// <summary>Form input for creating a user from the admin panel.</summary>
public sealed class NewUserInput
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    public int? QuotaGb { get; set; }
    public bool IsAdmin { get; set; }
}

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

    // Bound values for the "create user" form, preserved on validation errors.
    [BindProperty]
    public NewUserInput NewUser { get; set; } = new();

    public bool ShowCreateForm { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Users = await _sender.Send(new GetAdminUsersQuery(Search, Math.Max(1, P)), ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var quotaBytes = NewUser.QuotaGb is { } gb ? (long)gb * 1024 * 1024 * 1024 : (long?)null;
            await _sender.Send(new CreateUserCommand(
                NewUser.Email?.Trim() ?? string.Empty,
                NewUser.Password ?? string.Empty,
                string.IsNullOrWhiteSpace(NewUser.DisplayName) ? (NewUser.Email?.Trim() ?? string.Empty) : NewUser.DisplayName.Trim(),
                quotaBytes,
                NewUser.IsAdmin), ct);
            Notice = $"User {NewUser.Email} created.";
            return RedirectToPage(new { Search, P });
        }
        catch (AppException ex)
        {
            Error = ex.Message;
        }
        catch (FluentValidation.ValidationException ex)
        {
            Error = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Invalid input.";
        }

        // Re-render the list with the create form open and the entered values kept.
        ShowCreateForm = true;
        Users = await _sender.Send(new GetAdminUsersQuery(Search, Math.Max(1, P)), ct);
        return Page();
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
