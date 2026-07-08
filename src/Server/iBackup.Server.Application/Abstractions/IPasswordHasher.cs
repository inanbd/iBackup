namespace iBackup.Server.Application.Abstractions;

/// <summary>Password hashing (BCrypt in the default implementation).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}
