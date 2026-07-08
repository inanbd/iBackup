using iBackup.Server.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace iBackup.Server.UnitTests;

public class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var hash = _hasher.Hash("password-one");
        Assert.False(_hasher.Verify("password-two", hash));
    }

    [Fact]
    public void Verify_fails_gracefully_for_malformed_hash()
    {
        Assert.False(_hasher.Verify("whatever", "not-a-bcrypt-hash"));
    }

    [Fact]
    public void Hashes_are_salted()
    {
        Assert.NotEqual(_hasher.Hash("same password"), _hasher.Hash("same password"));
    }
}

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService() => new(Options.Create(new JwtOptions
    {
        SigningKey = "unit-test-signing-key-with-at-least-32-bytes!",
        Issuer = "test",
        Audience = "test-clients",
        AccessTokenLifetimeMinutes = 15,
        RefreshTokenLifetimeDays = 30
    }));

    [Fact]
    public void CreateAccessToken_returns_token_with_future_expiry()
    {
        var service = CreateService();
        var result = service.CreateAccessToken(Guid.NewGuid(), "user@example.com", Guid.NewGuid());

        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        Assert.Equal(3, result.Token.Split('.').Length); // header.payload.signature
        Assert.True(result.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(10));
    }

    [Fact]
    public void CreateRefreshToken_is_unique_and_long()
    {
        var service = CreateService();
        var a = service.CreateRefreshToken();
        var b = service.CreateRefreshToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 64);
    }

    [Fact]
    public void HashToken_is_deterministic_hex_sha256()
    {
        var service = CreateService();
        var hash1 = service.HashToken("some-token");
        var hash2 = service.HashToken("some-token");
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
        Assert.NotEqual(hash1, service.HashToken("other-token"));
    }

    [Fact]
    public void Short_signing_key_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new JwtTokenService(Options.Create(new JwtOptions { SigningKey = "too-short" })));
    }
}
