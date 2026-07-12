using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Features.Admin;

/// <summary>0 = whitelist (allow), 1 = blacklist (deny).</summary>
public enum IpRuleKind : byte
{
    Whitelist = 0,
    Blacklist = 1
}

/// <summary>Admin: add a whitelist or blacklist entry ('*' or an IP literal).</summary>
public sealed record AddIpAccessRuleCommand(string IpAddress, IpRuleKind Kind, string? Reason) : IRequest;

internal sealed class AddIpAccessRuleValidator : AbstractValidator<AddIpAccessRuleCommand>
{
    public AddIpAccessRuleValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(256);
        RuleFor(x => x.IpAddress)
            .NotEmpty()
            .Must(IpRuleEvaluator.IsValidRuleValue)
            .WithMessage("Enter a valid IP address or '*' for all addresses.");
    }
}

internal sealed class AddIpAccessRuleHandler : IRequestHandler<AddIpAccessRuleCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IIpAccessControl _ipAccess;

    public AddIpAccessRuleHandler(
        ISqlConnectionFactory connections,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IIpAccessControl ipAccess)
    {
        _connections = connections;
        _currentUser = currentUser;
        _audit = audit;
        _ipAccess = ipAccess;
    }

    public async Task Handle(AddIpAccessRuleCommand request, CancellationToken ct)
    {
        var value = IpRuleEvaluator.NormalizeRuleValue(request.IpAddress);

        const string sql = """
            INSERT INTO dbo.IpAccessRules (IpAddress, Kind, Reason, IsAuto, CreatedByUserId)
            VALUES (@Ip, @Kind, @Reason, 0, @UserId);
            """;

        await using var connection = await _connections.OpenConnectionAsync(ct);
        try
        {
            await using var command = Sql.Command(connection, sql)
                .With("@Ip", value)
                .With("@Kind", (byte)request.Kind)
                .With("@Reason", string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim())
                .With("@UserId", _currentUser.UserId);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw AppException.Conflict($"'{value}' is already on the {request.Kind.ToString().ToLowerInvariant()}.");
        }

        _ipAccess.Invalidate();
        await _audit.LogAsync(_currentUser.UserId, null, "ip.rule_added",
            $"{request.Kind}: {value}", _currentUser.IpAddress, ct);
    }
}
