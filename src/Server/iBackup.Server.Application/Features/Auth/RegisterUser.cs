using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using iBackup.Shared.Contracts;
using MediatR;

namespace iBackup.Server.Application.Features.Auth;

/// <summary>POST /api/auth/register — creates a new user account.</summary>
public sealed record RegisterUserCommand(string Email, string Password, string DisplayName)
    : IRequest<RegisterUserResponse>;

internal sealed class RegisterUserValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).MaximumLength(128)
            .WithMessage("Password must be between 10 and 128 characters.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}

internal sealed class RegisterUserHandler : IRequestHandler<RegisterUserCommand, RegisterUserResponse>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;

    public RegisterUserHandler(
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

    public async Task<RegisterUserResponse> Handle(RegisterUserCommand request, CancellationToken ct)
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
        const string insertSql = """
            INSERT INTO dbo.Users (Id, Email, PasswordHash, DisplayName)
            VALUES (@Id, @Email, @PasswordHash, @DisplayName);
            """;
        await using (var insert = Sql.Command(connection, insertSql)
            .With("@Id", userId)
            .With("@Email", email)
            .With("@PasswordHash", passwordHash)
            .With("@DisplayName", request.DisplayName.Trim()))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await _audit.LogAsync(userId, null, "auth.register", $"Account created for {email}", _currentUser.IpAddress, ct);
        return new RegisterUserResponse(userId, email);
    }
}
