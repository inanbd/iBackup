using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

public sealed record AdminIdentity(Guid UserId, string Email, string DisplayName);

/// <summary>
/// Admin dashboard sign-in: verifies credentials and requires the IsAdmin flag.
/// The Razor Pages layer turns the result into an auth cookie.
/// </summary>
public sealed record AdminLoginQuery(string Email, string Password) : IRequest<AdminIdentity>;

internal sealed class AdminLoginValidator : AbstractValidator<AdminLoginQuery>
{
    public AdminLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

internal sealed class AdminLoginHandler : IRequestHandler<AdminLoginQuery, AdminIdentity>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IIpAccessControl _ipAccess;

    public AdminLoginHandler(
        ISqlConnectionFactory connections,
        IPasswordHasher passwordHasher,
        IAuditLogger audit,
        ICurrentUser currentUser,
        IIpAccessControl ipAccess)
    {
        _connections = connections;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _currentUser = currentUser;
        _ipAccess = ipAccess;
    }

    public async Task<AdminIdentity> Handle(AdminLoginQuery request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var ip = _currentUser.IpAddress;

        const string sql = """
            SELECT Id, PasswordHash, DisplayName, IsActive, IsAdmin
            FROM dbo.Users
            WHERE Email = @Email;
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);

        Guid userId = Guid.Empty;
        string? passwordHash = null;
        var displayName = string.Empty;
        var isActive = false;
        var isAdmin = false;

        await using (var command = Sql.Command(connection, sql).With("@Email", email))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                userId = reader.GetGuid(0);
                passwordHash = reader.GetString(1);
                displayName = reader.GetString(2);
                isActive = reader.GetBoolean(3);
                isAdmin = reader.GetBoolean(4);
            }
        }

        if (passwordHash is null || !_passwordHasher.Verify(request.Password, passwordHash))
        {
            await _audit.LogAsync(passwordHash is null ? null : userId, null, "admin.login_failed",
                $"Failed admin login for {email}", ip, ct);
            await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
                email, ip, Success: false, "Invalid email or password", passwordHash is null ? null : userId, "admin"), ct);
            throw AppException.Unauthorized("Invalid email or password.");
        }

        if (!isActive || !isAdmin)
        {
            var reason = isAdmin ? "Account disabled" : "Account is not an administrator";
            await _audit.LogAsync(userId, null, "admin.login_denied", reason, ip, ct);
            await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
                email, ip, Success: false, reason, userId, "admin"), ct);
            throw AppException.Forbidden("This account does not have administrator access.");
        }

        await _audit.LogAsync(userId, null, "admin.login", $"Admin sign-in from {ip ?? "unknown"}", ip, ct);
        await _ipAccess.RecordLoginAttemptAsync(new LoginAttemptRecord(
            email, ip, Success: true, "Admin sign-in", userId, "admin"), ct);
        return new AdminIdentity(userId, email, displayName);
    }
}
