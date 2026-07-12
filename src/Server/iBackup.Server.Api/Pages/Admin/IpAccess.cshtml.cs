using iBackup.Server.Application.Features.Admin;
using iBackup.Server.Domain.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class IpAccessModel : PageModel
{
    private readonly ISender _sender;

    public IpAccessModel(ISender sender)
    {
        _sender = sender;
    }

    public IpAccessRules Rules { get; private set; } = null!;

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rules = await _sender.Send(new GetIpAccessRulesQuery(), ct);
    }

    public Task<IActionResult> OnPostAddWhitelistAsync(string ipAddress, string? reason, CancellationToken ct)
        => AddAsync(ipAddress, IpRuleKind.Whitelist, reason, ct);

    public Task<IActionResult> OnPostAddBlacklistAsync(string ipAddress, string? reason, CancellationToken ct)
        => AddAsync(ipAddress, IpRuleKind.Blacklist, reason, ct);

    public async Task<IActionResult> OnPostDeleteAsync(long ruleId, CancellationToken ct)
    {
        try
        {
            await _sender.Send(new DeleteIpAccessRuleCommand(ruleId), ct);
            Notice = "Rule removed.";
        }
        catch (AppException ex)
        {
            Error = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task<IActionResult> AddAsync(string ipAddress, IpRuleKind kind, string? reason, CancellationToken ct)
    {
        try
        {
            await _sender.Send(new AddIpAccessRuleCommand(ipAddress, kind, reason), ct);
            Notice = $"Added {ipAddress} to the {kind.ToString().ToLowerInvariant()}.";
        }
        catch (AppException ex)
        {
            Error = ex.Message;
        }
        catch (FluentValidation.ValidationException ex)
        {
            Error = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Invalid input.";
        }
        return RedirectToPage();
    }
}
