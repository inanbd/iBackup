using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Server.IntegrationTests;

/// <summary>Multiple users uploading identical content concurrently: storage stays per-user isolated.</summary>
public class ConcurrencyTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ConcurrencyTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Five_users_upload_the_same_content_concurrently()
    {
        if (!ApiFixture.IsAvailable)
        {
            return;
        }

        // Same bytes for every user: dedup must apply per user, never across users.
        var content = new byte[4096];
        RandomNumberGenerator.Fill(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            var client = _fixture.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            var email = $"cc{i}-{Guid.NewGuid():N}@example.com";
            await client.PostAsJsonAsync("api/auth/register", new RegisterUserRequest(email, "concurrency-pw-1", $"User {i}"));
            var login = await client.PostAsJsonAsync("api/auth/login",
                new LoginRequest(email, "concurrency-pw-1", new DeviceInfo($"CC {i}", "TestOS", "1.0.0")));
            var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var folder = (await (await client.PostAsJsonAsync("api/folders", new CreateFolderRequest(
                tokens.DeviceId, @"C:\Concurrent", true, true, false, false, [], [], [], null, 0,
                new ScheduleSettings(ScheduleType.Manual), new RetentionSettings(RetentionMode.KeepForever),
                CompressionMethod.None))).Content.ReadFromJsonAsync<BackupFolderDto>())!;
            var job = (await (await client.PostAsJsonAsync("api/backup/start",
                new StartBackupRequest(tokens.DeviceId, folder.Id, BackupType.Full))).Content.ReadFromJsonAsync<StartBackupResponse>())!;

            var begin = (await (await client.PostAsJsonAsync("api/backup/upload", new BeginFileUploadRequest(
                job.BackupJobId, folder.Id, "shared/file.bin", "file.bin", sha256,
                content.Length, content.Length, DateTime.UtcNow,
                CompressionMethod.None, EncryptionMethod.None, content.Length, 1)))
                .Content.ReadFromJsonAsync<BeginFileUploadResponse>())!;

            // Every user uploads bytes at least once: dedup is per-user, so no
            // user should get a dedup hit on their very first upload.
            Assert.False(begin.Deduplicated);

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"api/backup/chunk?sessionId={begin.UploadSessionId}&chunkIndex=0&sha256={sha256}")
            {
                Content = new ByteArrayContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<ChunkUploadResponse>())!;
            Assert.True(result.FileCompleted);

            // Each user sees exactly their own file.
            var files = (await client.GetFromJsonAsync<PagedResult<RestoreFileItemDto>>("api/restore/files"))!;
            Assert.Equal(1, files.TotalCount);
        });

        await Task.WhenAll(tasks);
    }
}
