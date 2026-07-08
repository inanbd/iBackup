using iBackup.Server.Api.Services;
using iBackup.Server.Application.Features.Admin;
using iBackup.Server.Domain.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace iBackup.Server.Api.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly ISender _sender;

    public LoginModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? Error { get; private set; }

    public IActionResult OnGet()
        => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Admin/Index") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var admin = await _sender.Send(new AdminLoginQuery(Email, Password), ct);
            await HttpContext.SignInAsync(
                AdminAuth.Scheme,
                AdminAuth.CreatePrincipal(admin),
                new AuthenticationProperties { IsPersistent = RememberMe });
            return RedirectToPage("/Admin/Index");
        }
        catch (AppException ex)
        {
            Error = ex.Message;
            return Page();
        }
        catch (FluentValidation.ValidationException)
        {
            Error = "Please enter a valid email address and password.";
            return Page();
        }
    }
}
