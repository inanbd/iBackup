using iBackup.Server.Application.Abstractions;
using iBackup.Server.Infrastructure.Auditing;
using iBackup.Server.Infrastructure.BackgroundServices;
using iBackup.Server.Infrastructure.Data;
using iBackup.Server.Infrastructure.Security;
using iBackup.Server.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace iBackup.Server.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers data access, security, storage, auditing and background services.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<IpAccessOptions>(configuration.GetSection(IpAccessOptions.SectionName));

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IFileStorage, DiskFileStorage>();
        services.AddSingleton<IAuditLogger, SqlAuditLogger>();
        services.AddSingleton<IIpAccessControl, IpAccessControl>();
        services.AddSingleton<DbInitializer>();

        services.AddHostedService<AbandonedUploadCleanupService>();
        services.AddHostedService<RetentionPolicyService>();

        return services;
    }
}
