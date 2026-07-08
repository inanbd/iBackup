using iBackup.Client.Core.Api;
using iBackup.Client.Core.Configuration;
using iBackup.Client.Core.Engine;
using iBackup.Client.Core.Notifications;
using iBackup.Client.Core.Restore;
using iBackup.Client.Core.Security;
using iBackup.Client.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace iBackup.Client.Core;

public static class DependencyInjection
{
    /// <summary>Registers the full client backup stack (shared by the WPF app and the Windows service).</summary>
    public static IServiceCollection AddBackupClientCore(this IServiceCollection services, Action<ClientOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<ClientOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton(sp =>
            new CredentialStore(sp.GetRequiredService<IOptions<ClientOptions>>().Value.DataDirectory));
        services.AddSingleton<EncryptionKeyProvider>();
        services.AddSingleton<TokenStore>();

        // Singleton HttpClient with connection recycling (the engine's services are
        // singletons, so a typed transient client would be captured forever anyway).
        services.AddSingleton(sp =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30) // large chunk uploads
            };
        });
        services.AddSingleton<BackupApiClient>();

        services.AddSingleton<AuthService>();
        services.AddSingleton(sp =>
            new LocalStateStore(sp.GetRequiredService<IOptions<ClientOptions>>().Value.StateDatabasePath));
        services.AddSingleton<FileScanner>();
        services.AddSingleton<ChangeMonitor>();
        services.AddSingleton<UploadEngine>();
        services.AddSingleton<BackupEngine>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<INotificationService, NotificationService>();

        return services;
    }
}
