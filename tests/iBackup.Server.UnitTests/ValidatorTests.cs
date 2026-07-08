using iBackup.Server.Application.Features.Auth;
using iBackup.Server.Application.Features.Backups;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Server.UnitTests;

public class RegisterUserValidatorTests
{
    private readonly RegisterUserValidator _validator = new();

    [Fact]
    public void Valid_registration_passes()
    {
        var result = _validator.Validate(new RegisterUserCommand("user@example.com", "long-enough-password", "User"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("not-an-email", "long-enough-password", "User")]
    [InlineData("user@example.com", "short", "User")]
    [InlineData("user@example.com", "long-enough-password", "")]
    [InlineData("", "long-enough-password", "User")]
    public void Invalid_registration_fails(string email, string password, string displayName)
    {
        var result = _validator.Validate(new RegisterUserCommand(email, password, displayName));
        Assert.False(result.IsValid);
    }
}

public class BeginFileUploadValidatorTests
{
    private readonly BeginFileUploadValidator _validator = new();

    private static BeginFileUploadRequest ValidRequest() => new(
        BackupJobId: Guid.NewGuid(),
        FolderId: Guid.NewGuid(),
        RelativePath: "docs/report.docx",
        FileName: "report.docx",
        Sha256: new string('a', 64),
        OriginalSize: 1234,
        EncryptedSize: 1300,
        FileModifiedAtUtc: DateTime.UtcNow,
        Compression: CompressionMethod.Zstd,
        Encryption: EncryptionMethod.Aes256Gcm,
        ChunkSize: 4 * 1024 * 1024,
        TotalChunks: 1);

    [Fact]
    public void Valid_request_passes()
    {
        Assert.True(_validator.Validate(new BeginFileUploadCommand(ValidRequest())).IsValid);
    }

    [Fact]
    public void Invalid_hash_fails()
    {
        var request = ValidRequest() with { Sha256 = "zzzz" };
        Assert.False(_validator.Validate(new BeginFileUploadCommand(request)).IsValid);
    }

    [Fact]
    public void Zero_chunks_fails()
    {
        var request = ValidRequest() with { TotalChunks = 0 };
        Assert.False(_validator.Validate(new BeginFileUploadCommand(request)).IsValid);
    }

    [Fact]
    public void Negative_size_fails()
    {
        var request = ValidRequest() with { OriginalSize = -1 };
        Assert.False(_validator.Validate(new BeginFileUploadCommand(request)).IsValid);
    }
}

public class FinishBackupValidatorTests
{
    private readonly FinishBackupValidator _validator = new();

    [Fact]
    public void Running_status_is_rejected_as_final_state()
    {
        var request = new FinishBackupRequest(Guid.NewGuid(), BackupJobStatus.Running, 0, 0, 0, 0, null);
        Assert.False(_validator.Validate(new FinishBackupCommand(request)).IsValid);
    }

    [Fact]
    public void Completed_status_passes()
    {
        var request = new FinishBackupRequest(Guid.NewGuid(), BackupJobStatus.Completed, 10, 5, 0, 1024, null);
        Assert.True(_validator.Validate(new FinishBackupCommand(request)).IsValid);
    }
}
