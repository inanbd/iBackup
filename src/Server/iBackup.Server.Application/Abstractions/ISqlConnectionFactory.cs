using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Abstractions;

/// <summary>Creates open SQL Server connections. All data access goes through ADO.NET.</summary>
public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
