using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Server.IntegrationTests;

/// <summary>
/// Full upload lifecycle against the real API: start job → announce → chunked
/// upload (out of order, with resume) → dedup on re-announce → history → restore download.
/// </summary>
public class BackupFlowTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public BackupFlowTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Chunked_upload_dedup_and_restore_roundtrip()
    {
        if (!ApiFixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        // --- register + login
        var email = $"bk-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("api/auth/register", new RegisterUserRequest(email, "backup-password-1", "Backup User"));
        var login = await client.PostAsJsonAsync("api/auth/login",
            new LoginRequest(email, "backup-password-1", new DeviceInfo("BK Device", "TestOS", "1.0.0")));
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // --- folder + job
        var folder = (await (await client.PostAsJsonAsync("api/folders", new CreateFolderRequest(
            tokens.DeviceId, @"C:\TestData", true, true, false, false, [], [], [], null, 0,
            new ScheduleSettings(ScheduleType.Manual), new RetentionSettings(RetentionMode.KeepForever),
            CompressionMethod.None))).Content.ReadFromJsonAsync<BackupFolderDto>())!;

        var job = (await (await client.PostAsJsonAsync("api/backup/start",
            new StartBackupRequest(tokens.DeviceId, folder.Id, BackupType.Full))).Content.ReadFromJsonAsync<StartBackupResponse>())!;

        // --- announce a 3-chunk "encrypted" payload
        var content = new byte[3 * 1024 + 100];
        RandomNumberGenerator.Fill(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        const int chunkSize = 1024;
        var totalChunks = 4;

        var beginResponse = await client.PostAsJsonAsync("api/backup/upload", new BeginFileUploadRequest(
            job.BackupJobId, folder.Id, "data/blob.bin", "blob.bin", sha256,
            content.Length, content.Length, DateTime.UtcNow,
            CompressionMethod.None, EncryptionMethod.None, chunkSize, totalChunks));
        beginResponse.EnsureSuccessStatusCode();
        var begin = (await beginResponse.Content.ReadFromJsonAsync<BeginFileUploadResponse>())!;
        Assert.False(begin.Deduplicated);
        Assert.NotNull(begin.UploadSessionId);

        // --- upload chunks out of order; last chunk completes and assembles
        Guid? versionId = null;
        foreach (var index in new[] { 2, 0, 3, 1 })
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, content.Length - offset);
            var chunk = content.AsSpan(offset, length).ToArray();
            var chunkSha = Convert.ToHexStringLower(SHA256.HashData(chunk));

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"api/backup/chunk?sessionId={begin.UploadSessionId}&chunkIndex={index}&sha256={chunkSha}")
            {
                Content = new ByteArrayContent(chunk)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<ChunkUploadResponse>())!;
            Assert.True(result.Received);
            if (result.FileCompleted)
            {
                versionId = result.FileVersionId;
            }
        }
        Assert.NotNull(versionId);

        // --- re-announcing identical content is deduplicated (no upload session)
        var dedupResponse = await client.PostAsJsonAsync("api/backup/upload", new BeginFileUploadRequest(
            job.BackupJobId, folder.Id, "data/copy-of-blob.bin", "copy-of-blob.bin", sha256,
            content.Length, content.Length, DateTime.UtcNow,
            CompressionMethod.None, EncryptionMethod.None, chunkSize, totalChunks));
        var dedup = (await dedupResponse.Content.ReadFromJsonAsync<BeginFileUploadResponse>())!;
        Assert.True(dedup.Deduplicated);
        Assert.NotNull(dedup.FileVersionId);

        // --- finish job, check history
        await client.PostAsJsonAsync("api/backup/finish", new FinishBackupRequest(
            job.BackupJobId, BackupJobStatus.Completed, 2, 0, 0, content.Length, null));
        var history = (await client.GetFromJsonAsync<PagedResult<BackupHistoryItemDto>>("api/backup/history"))!;
        Assert.Contains(history.Items, j => j.BackupJobId == job.BackupJobId && j.Status == BackupJobStatus.Completed);

        // --- restore: browse, request, download, verify bytes
        var files = (await client.GetFromJsonAsync<PagedResult<RestoreFileItemDto>>(
            $"api/restore/files?folderId={folder.Id}"))!;
        Assert.Equal(2, files.TotalCount);

        var restore = (await (await client.PostAsJsonAsync("api/restore",
            new CreateRestoreRequest([versionId!.Value]))).Content.ReadFromJsonAsync<CreateRestoreResponse>())!;
        var downloaded = await client.GetByteArrayAsync(restore.Items[0].DownloadUrl);
        Assert.Equal(content, downloaded);
    }

    [Fact]
    public async Task Upload_resume_reports_already_received_chunks()
    {
        if (!ApiFixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.CreateClient();
        var email = $"rs-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("api/auth/register", new RegisterUserRequest(email, "resume-password-1", "Resume User"));
        var login = await client.PostAsJsonAsync("api/auth/login",
            new LoginRequest(email, "resume-password-1", new DeviceInfo("RS Device", "TestOS", "1.0.0")));
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var folder = (await (await client.PostAsJsonAsync("api/folders", new CreateFolderRequest(
            tokens.DeviceId, @"C:\ResumeData", true, true, false, false, [], [], [], null, 0,
            new ScheduleSettings(ScheduleType.Manual), new RetentionSettings(RetentionMode.KeepForever),
            CompressionMethod.None))).Content.ReadFromJsonAsync<BackupFolderDto>())!;
        var job = (await (await client.PostAsJsonAsync("api/backup/start",
            new StartBackupRequest(tokens.DeviceId, folder.Id, BackupType.Full))).Content.ReadFromJsonAsync<StartBackupResponse>())!;

        var content = new byte[2048];
        RandomNumberGenerator.Fill(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

        BeginFileUploadRequest Announce() => new(
            job.BackupJobId, folder.Id, "big/file.bin", "file.bin", sha256,
            content.Length, content.Length, DateTime.UtcNow,
            CompressionMethod.None, EncryptionMethod.None, 1024, 2);

        var begin = (await (await client.PostAsJsonAsync("api/backup/upload", Announce()))
            .Content.ReadFromJsonAsync<BeginFileUploadResponse>())!;

        // Upload only chunk 0, then "crash" and re-announce.
        var chunk0 = content.AsSpan(0, 1024).ToArray();
        var chunk0Request = new HttpRequestMessage(HttpMethod.Post,
            $"api/backup/chunk?sessionId={begin.UploadSessionId}&chunkIndex=0&sha256={Convert.ToHexStringLower(SHA256.HashData(chunk0))}")
        {
            Content = new ByteArrayContent(chunk0)
        };
        chunk0Request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        (await client.SendAsync(chunk0Request)).EnsureSuccessStatusCode();

        var resumed = (await (await client.PostAsJsonAsync("api/backup/upload", Announce()))
            .Content.ReadFromJsonAsync<BeginFileUploadResponse>())!;

        Assert.Equal(begin.UploadSessionId, resumed.UploadSessionId);
        Assert.Contains(0, resumed.ReceivedChunkIndexes);
        Assert.DoesNotContain(1, resumed.ReceivedChunkIndexes);
    }
}
