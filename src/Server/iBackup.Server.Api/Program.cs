using System.Text;
using System.Threading.RateLimiting;
using iBackup.Server.Api.Middleware;
using iBackup.Server.Api.Services;
using iBackup.Server.Application;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Infrastructure;
using iBackup.Server.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---------------------------------------------------------------- logging
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            path: "logs/api-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true)
        .WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e =>
                e.Properties.TryGetValue("SourceContext", out var source) &&
                source.ToString().Contains("Features.Auth"))
            .WriteTo.File("logs/auth-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30))
        .WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e =>
                e.Properties.TryGetValue("SourceContext", out var source) &&
                source.ToString().Contains("BackgroundServices"))
            .WriteTo.File("logs/background-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)));

    // ---------------------------------------------------------------- services
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    builder.Services.AddControllers();

    // JWT bearer authentication.
    var signingKey = builder.Configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "iBackup",
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "iBackup.Clients",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                NameClaimType = "sub"
            };
        });
    builder.Services.AddAuthorization();

    // Rate limiting: modest global limit per client IP plus a strict window for auth endpoints.
    var globalPermit = builder.Configuration.GetValue("RateLimiting:GlobalPermitPerMinute", 1200);
    var authPermit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20);
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = globalPermit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        limiter.AddPolicy("auth", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authPermit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    });

    // Swagger / OpenAPI with bearer auth.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "iBackup API",
            Version = "v1",
            Description = "Secure client-server backup API."
        });
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT access token."
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    var app = builder.Build();

    // ---------------------------------------------------------------- pipeline
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }
    else
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    // Optional schema bootstrap (Database:InitializeOnStartup).
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<DbInitializer>().InitializeAsync();
    }

    Log.Information("iBackup server starting");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "iBackup server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
