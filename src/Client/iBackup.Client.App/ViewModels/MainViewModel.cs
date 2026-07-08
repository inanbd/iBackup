using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;
using iBackup.Client.Core.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace iBackup.Client.App.ViewModels;

/// <summary>Shell view model: navigation, session state and engine lifecycle.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly AuthService _auth;
    private readonly BackupEngine _engine;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private bool _isLoggedIn;

    public MainViewModel(IServiceProvider services, AuthService auth, BackupEngine engine)
    {
        _services = services;
        _auth = auth;
        _engine = engine;
    }

    public async Task InitializeAsync()
    {
        if (_auth.TryResumeSession() && _auth.PersistedCredentials is { DeviceId: not null } stored)
        {
            _engine.DeviceId = stored.DeviceId;
            try
            {
                await _engine.StartAsync();
                IsLoggedIn = true;
                Navigate("dashboard");
                return;
            }
            catch (Exception)
            {
                // Session could not be resumed (offline / revoked); fall through to login.
            }
        }
        Navigate("login");
    }

    /// <summary>Called by the login page after a successful sign-in.</summary>
    public async Task OnSignedInAsync(Guid deviceId)
    {
        _engine.DeviceId = deviceId;
        await _engine.StartAsync();
        IsLoggedIn = true;
        Navigate("dashboard");
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "login" => _services.GetRequiredService<LoginViewModel>(),
            "dashboard" => Activate(_services.GetRequiredService<DashboardViewModel>()),
            "folders" => Activate(_services.GetRequiredService<FoldersViewModel>()),
            "schedules" => Activate(_services.GetRequiredService<SchedulesViewModel>()),
            "progress" => _services.GetRequiredService<ProgressViewModel>(),
            "restore" => Activate(_services.GetRequiredService<RestoreViewModel>()),
            "devices" => Activate(_services.GetRequiredService<DevicesViewModel>()),
            "logs" => Activate(_services.GetRequiredService<LogsViewModel>()),
            "settings" => _services.GetRequiredService<SettingsViewModel>(),
            _ => CurrentPage
        };
    }

    private static T Activate<T>(T viewModel) where T : class
    {
        (viewModel as IRefreshable)?.RefreshCommandIfIdle();
        return viewModel;
    }

    [RelayCommand]
    private async Task SignOut()
    {
        try
        {
            await _engine.StopAsync();
            await _auth.LogoutAsync(CancellationToken.None);
        }
        finally
        {
            IsLoggedIn = false;
            Navigate("login");
        }
    }
}

/// <summary>Pages that reload their data when navigated to.</summary>
public interface IRefreshable
{
    void RefreshCommandIfIdle();
}
