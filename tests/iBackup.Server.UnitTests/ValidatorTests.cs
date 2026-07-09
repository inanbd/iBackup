using iBackup.Server.Application.Features.Backups;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Server.UnitTests;

public class CreateUserValidatorTests
{
    private readonly iBackup.Server.Application.Features.Admin.CreateUserValidator _validator = new();

    private static iBackup.Server.Application.Features.Admin.CreateUserCommand Valid()
        => new("new@example.com", "long-enough-password", "New User", QuotaBytes: 50L * 1024 * 1024 * 1024, IsAdmin: false);

    [Fact]
    public void Valid_command_passes()
        => Assert.True(_validator.Validate(Valid()).IsValid);

    [Fact]
    public void Null_quota_is_allowed()
        => Assert.True(_validator.Validate(Valid() with { QuotaBytes = null }).IsValid);

    [Theory]
    [InlineData("not-an-email", "long-enough-password", "Name")]
    [InlineData("new@example.com", "short", "Name")]
    [InlineData("new@example.com", "long-enough-password", "")]
    public void Invalid_command_fails(string email, string password, string displayName)
        => Assert.False(_validator.Validate(Valid() with { Email = email, Password = password, DisplayName = displayName }).IsValid);

    [Fact]
    public void Non_positive_quota_fails()
        => Assert.False(_validator.Validate(Valid() with { QuotaBytes = 0 }).IsValid);
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
