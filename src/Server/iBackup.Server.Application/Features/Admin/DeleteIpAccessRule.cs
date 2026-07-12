using FluentValidation;
using iBackup.Server.Application.Abstractions;
using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using MediatR;

namespace iBackup.Server.Application.Features.Admin;

/// <summary>Admin: remove a whitelist/blacklist rule by id.</summary>
public sealed record DeleteIpAccessRuleCommand(long RuleId) : IRequest;

internal sealed class DeleteIpAccessRuleValidator : AbstractValidator<DeleteIpAccessRuleCommand>
{
    public DeleteIpAccessRuleValidator()
    {
        RuleFor(x => x.RuleId).GreaterThan(0);
    }
}

internal sealed class DeleteIpAccessRuleHandler : IRequestHandler<DeleteIpAccessRuleCommand>
{
    private readonly ISqlConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IIpAccessControl _ipAccess;

    public DeleteIpAccessRuleHandler(
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

    public async Task Handle(DeleteIpAccessRuleCommand request, CancellationToken ct)
    {
        const string sql = "DELETE FROM dbo.IpAccessRules OUTPUT deleted.IpAddress, deleted.Kind WHERE Id = @Id;";

        await using var connection = await _connections.OpenConnectionAsync(ct);
        await using var command = Sql.Command(connection, sql).With("@Id", request.RuleId);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw AppException.NotFound("Rule not found.");
        }
        var ip = reader.GetString(0);
        var kind = (IpRuleKind)reader.GetByte(1);
        await reader.CloseAsync();

        _ipAccess.Invalidate();
        await _audit.LogAsync(_currentUser.UserId, null, "ip.rule_removed",
            $"{kind}: {ip}", _currentUser.IpAddress, ct);
    }
}
