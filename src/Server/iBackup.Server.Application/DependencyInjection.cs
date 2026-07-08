using FluentValidation;
using iBackup.Server.Application.Common.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace iBackup.Server.Application;

public static class DependencyInjection
{
    /// <summary>Registers MediatR handlers, pipeline behaviors and validators from this assembly.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            configuration.AddOpenBehavior(typeof(LoggingBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        return services;
    }
}
