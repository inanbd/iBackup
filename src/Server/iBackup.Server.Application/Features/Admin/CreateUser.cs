using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

/// <summary>
/// Admin: create a new user account from the dashboard, with an optional custom
/// quota and admin flag. Unlike public self-registration this lets an admin set
/// the quota and grant administrator access up front.
/// </summary>
public sealed record CreateUserCommand(
    string Email,
    string Password,
    string DisplayName,
    long? QuotaBytes,
    bool IsAdmin) : IRequest<Guid>;

internal sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().Length(10, 128)
            .WithMessage("Password must be between 10 and 128 characters.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.QuotaBytes).GreaterThan(0).When(x => x.QuotaBytes.HasValue);
    }
}

internal sealed class CreateUserHandler : IRequestHandler<CreateUserCommand, Guid>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;

    public CreateUserHandler(
        ISqlConnectionFactory connections,
        IPasswordHasher passwordHasher,
        IAuditLogger audit,
        ICurrentUser currentUser)
    {
        _connections = connections;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _currentUser = currentUser;
    }

    public async Task<Guid> Handle(CreateUserCommand request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var passwordHash = _passwordHasher.Hash(request.Password);

        await using var connection = await _connections.OpenConnectionAsync(ct);

        const string existsSql = "SELECT 1 FROM dbo.Users WHERE Email = @Email;";
        await using (var exists = Sql.Command(connection, existsSql).With("@Email", email))
        {
            if (await exists.ExecuteScalarAsync(ct) is not null)
            {
                throw AppException.Conflict("An account with this email already exists.");
            }
        }

        var userId = Guid.NewGuid();
        // When no quota is supplied, fall back to the Users.QuotaBytes column
        // default (100 GB) by omitting the column from the INSERT.
        var insertSql = request.QuotaBytes.HasValue
            ? """
              INSERT INTO dbo.Users (Id, Email, PasswordHash, DisplayName, QuotaBytes, IsAdmin)
              VALUES (@Id, @Email, @PasswordHash, @DisplayName, @Quota, @IsAdmin);
              """
            : """
              INSERT INTO dbo.Users (Id, Email, PasswordHash, DisplayName, IsAdmin)
              VALUES (@Id, @Email, @PasswordHash, @DisplayName, @IsAdmin);
              """;
        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", userId)
            .With("@Email", email)
            .With("@PasswordHash", passwordHash)
            .With("@DisplayName", request.DisplayName.Trim())
            .With("@Quota", request.QuotaBytes)
            .With("@IsAdmin", request.IsAdmin))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(_currentUser.UserId, null, "admin.user_created",
            $"Created {email}" + (request.IsAdmin ? " (admin)" : ""), _currentUser.IpAddress, ct);
        return userId;
    }
}
