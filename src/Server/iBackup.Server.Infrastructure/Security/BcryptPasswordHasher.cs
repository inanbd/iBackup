using iBackup.Server.Application.Abstractions;

namespace iBackup.Server.Infrastructure.Security;

/// <summary>BCrypt password hashing with a work factor of 12.</summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string passwordHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (Exception)
        {
            // Malformed stored hash - treat as verification failure.
            return false;
        }
    }
}
