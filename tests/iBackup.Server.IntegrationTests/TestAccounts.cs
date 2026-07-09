using iBackup.Server.Infrastructure.Security;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.IntegrationTests;

/// <summary>
/// Seeds user accounts directly in the database for test setup.
///
/// Account creation is admin-only (there is no public registration endpoint),
/// so tests provision their fixtures at the data layer using the same BCrypt
/// hasher the application uses. This mirrors how real accounts are created
/// (admin dashboard / seed script) without needing an authenticated admin in
/// every test's arrange step.
/// </summary>
internal static class TestAccounts
{
    private static readonly BcryptPasswordHasher Hasher = new();

    public static async Task<Guid> CreateAsync(string email, string password, bool isAdmin = false, string? displayName = null)
    {
        var id = Guid.NewGuid();
        const string sql = """
            INSERT INTO dbo.Users (Id, Email, PasswordHash, DisplayName, IsAdmin)
            VALUES (@Id, @Email, @Hash, @Name, @Admin);
            """;

        await using var connection = new SqlConnection(ApiFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Email", email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("@Hash", Hasher.Hash(password));
        command.Parameters.AddWithValue("@Name", displayName ?? email);
        command.Parameters.AddWithValue("@Admin", isAdmin);
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
