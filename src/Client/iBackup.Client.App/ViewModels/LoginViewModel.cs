using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;

namespace iBackup.Client.App.ViewModels;

/// <summary>
/// Sign-in only. Accounts are provisioned by an administrator (server admin
/// dashboard); the client does not offer self-registration.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private string _serverUrl = "https://localhost:5001";

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public LoginViewModel(AuthService auth, IServiceProvider services)
    {
        _auth = auth;
        _services = services;

        var stored = _auth.PersistedCredentials;
        if (stored is not null)
        {
            ServerUrl = stored.ServerUrl;
            Email = stored.Email;
        }
    }

    [RelayCommand]
    private async Task SignIn()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var tokens = await _auth.LoginAsync(ServerUrl, Email, Password, CancellationToken.None);

            Password = string.Empty;
            var main = (MainViewModel)_services.GetService(typeof(MainViewModel))!;
            await main.OnSignedInAsync(tokens.DeviceId);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
