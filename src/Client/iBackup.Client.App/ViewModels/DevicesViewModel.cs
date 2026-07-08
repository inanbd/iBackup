using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iBackup.Client.Core.Api;
using iBackup.Shared.Contracts;

namespace iBackup.Client.App.ViewModels;

public partial class DevicesViewModel : ObservableObject, IRefreshable
{
    private readonly BackupApiClient _api;

    public ObservableCollection<DeviceDto> Devices { get; } = [];

    [ObservableProperty] private DeviceDto? _selectedDevice;
    [ObservableProperty] private string _renameText = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public DevicesViewModel(BackupApiClient api)
    {
        _api = api;
    }

    public void RefreshCommandIfIdle()
    {
        if (!IsBusy)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private async Task Load() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var devices = await _api.GetDevicesAsync(CancellationToken.None);
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(device);
            }
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

    [RelayCommand]
    private async Task Rename()
    {
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(RenameText))
        {
            return;
        }
        await ExecuteAsync(() => _api.UpdateDeviceAsync(
            new UpdateDeviceRequest(SelectedDevice.Id, RenameText.Trim(), null), CancellationToken.None));
        RenameText = string.Empty;
    }

    [RelayCommand]
    private async Task ToggleActive(DeviceDto? device)
    {
        if (device is null)
        {
            return;
        }
        await ExecuteAsync(() => _api.UpdateDeviceAsync(
            new UpdateDeviceRequest(device.Id, null, !device.IsActive), CancellationToken.None));
    }

    [RelayCommand]
    private async Task ForceLogout(DeviceDto? device)
    {
        if (device is null)
        {
            return;
        }
        await ExecuteAsync(() => _api.ForceLogoutDeviceAsync(device.Id, CancellationToken.None));
    }

    [RelayCommand]
    private async Task Remove(DeviceDto? device)
    {
        if (device is null || device.IsCurrentDevice)
        {
            Error = device?.IsCurrentDevice == true ? "You cannot remove the device you are using." : null;
            return;
        }
        await ExecuteAsync(() => _api.DeleteDeviceAsync(device.Id, CancellationToken.None));
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        Error = null;
        try
        {
            await action();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
