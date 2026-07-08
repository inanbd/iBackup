using iBackup.Client.Core.Api;
using iBackup.Client.Core.Compression;
using iBackup.Client.Core.Configuration;
using iBackup.Client.Core.Security;
using iBackup.Client.Core.Util;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace iBackup.Client.Core.Engine;

public sealed record UploadProgress(
    string RelativePath, long BytesUploaded, long TotalBytes, int ChunksCompleted, int TotalChunks);

public sealed record UploadOutcome(
    bool Success,
    bool Deduplicated,
    Guid? FileVersionId,
    string Sha256,
    long OriginalSize,
    long EncryptedSize,
    string? Error);

/// <summary>
/// Uploads one file: hash → compress → encrypt (all streaming, bounded memory) →
/// announce (dedup / resume) → push missing chunks in parallel with retries.
/// A crashed upload resumes because the server reports the chunk indexes it
/// already stores.
/// </summary>
public sealed class UploadEngine
{
    private readonly BackupApiClient _api;
    private readonly EncryptionKeyProvider _keys;
    private readonly ClientOptions _options;
    private readonly ILogger<UploadEngine> _logger;

    /// <summary>Progress callback for the UI (bytes uploaded so far, per file).</summary>
    public event Action<UploadProgress>? Progress;

    public UploadEngine(
        BackupApiClient api,
        EncryptionKeyProvider keys,
        IOptions<ClientOptions> options,
        ILogger<UploadEngine> logger)
    {
        _api = api;
        _keys = keys;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UploadOutcome> UploadFileAsync(
        BackupFolderDto folder, ScannedFile file, Guid backupJobId, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.TempDirectory);
        var tempCompressed = Path.Combine(_options.TempDirectory, Guid.NewGuid().ToString("N") + ".cmp");
        var tempEncrypted = Path.Combine(_options.TempDirectory, Guid.NewGuid().ToString("N") + ".enc");

        try
        {
            // 1. Hash the plaintext (this is the dedup identity).
            var sha256 = await FileHasher.Sha256OfFileAsync(file.FullPath, ct);

            // 2. Compress to a temp file (streaming).
            var compressor = CompressorFactory.Create(folder.Compression);
            long compressedSize;
            await using (var input = OpenSharedRead(file.FullPath))
            await using (var compressed = CreateTempWrite(tempCompressed))
            {
                await compressor.CompressAsync(input, compressed, ct);
                await compressed.FlushAsync(ct);
                compressedSize = compressed.Length;
            }

            // 3. Encrypt chunk-by-chunk to a second temp file (streaming).
            var plainChunkSize = _options.ChunkSizeBytes;
            await using (var compressed = OpenSharedRead(tempCompressed))
            await using (var encrypted = CreateTempWrite(tempEncrypted))
            {
                await ClientCrypto.EncryptAsync(compressed, encrypted, _keys.GetKey(), plainChunkSize, ct);
                await encrypted.FlushAsync(ct);
            }
            File.Delete(tempCompressed);

            var encryptedSize = new FileInfo(tempEncrypted).Length;
            var totalChunks = ClientCrypto.GetChunkCount(compressedSize, plainChunkSize);
            var encryptedChunkSize = plainChunkSize + ClientCrypto.ChunkOverhead;

            // 4. Announce the file: dedup hit or upload session (with resume info).
            var begin = await RetryPolicy.ExecuteAsync(
                token => _api.BeginFileUploadAsync(new BeginFileUploadRequest(
                    backupJobId, folder.Id, file.RelativePath, Path.GetFileName(file.FullPath),
                    sha256, file.Size, encryptedSize, file.ModifiedAtUtc,
                    folder.Compression, EncryptionMethod.Aes256Gcm,
                    plainChunkSize, totalChunks), token),
                _options.MaxRetryAttempts, _options.RetryBaseDelay, _logger, $"announce {file.RelativePath}", ct);

            if (begin.Deduplicated)
            {
                _logger.LogInformation("Deduplicated {Path} ({Sha256})", file.RelativePath, sha256);
                return new UploadOutcome(true, true, begin.FileVersionId, sha256, file.Size, encryptedSize, null);
            }

            var sessionId = begin.UploadSessionId
                ?? throw new InvalidOperationException("Server returned neither dedup nor an upload session.");
            var alreadyReceived = new HashSet<int>(begin.ReceivedChunkIndexes);
            if (alreadyReceived.Count > 0)
            {
                _logger.LogInformation("Resuming upload of {Path}: {Received}/{Total} chunks already stored",
                    file.RelativePath, alreadyReceived.Count, totalChunks);
            }

            // 5. Push missing chunks in parallel.
            Guid? versionId = null;
            var chunksCompleted = alreadyReceived.Count;
            long bytesUploaded = (long)alreadyReceived.Count * encryptedChunkSize;
            var gate = new SemaphoreSlim(Math.Max(1, _options.ParallelChunkUploads));
            var completionLock = new Lock();

            var tasks = Enumerable.Range(0, totalChunks)
                .Where(index => !alreadyReceived.Contains(index))
                .Select(async index =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var response = await UploadSingleChunkAsync(
                            sessionId, tempEncrypted, index, encryptedChunkSize, encryptedSize, ct);

                        lock (completionLock)
                        {
                            chunksCompleted++;
                            bytesUploaded += ChunkLength(index, encryptedChunkSize, encryptedSize);
                            if (response.FileVersionId is { } v)
                            {
                                versionId = v;
                            }
                            Progress?.Invoke(new UploadProgress(
                                file.RelativePath, bytesUploaded, encryptedSize, chunksCompleted, totalChunks));
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToList();

            await Task.WhenAll(tasks);

            _logger.LogInformation("Uploaded {Path}: {Chunks} chunks, {Bytes} bytes", file.RelativePath, totalChunks, encryptedSize);
            return new UploadOutcome(true, false, versionId, sha256, file.Size, encryptedSize, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed for {Path}", file.FullPath);
            return new UploadOutcome(false, false, null, string.Empty, file.Size, 0, ex.Message);
        }
        finally
        {
            TryDelete(tempCompressed);
            TryDelete(tempEncrypted);
        }
    }

    private async Task<ChunkUploadResponse> UploadSingleChunkAsync(
        Guid sessionId, string encryptedPath, int chunkIndex, int encryptedChunkSize, long encryptedTotal, CancellationToken ct)
    {
        var offset = (long)chunkIndex * encryptedChunkSize;
        var length = ChunkLength(chunkIndex, encryptedChunkSize, encryptedTotal);

        // Pass 1: hash the chunk (streamed).
        string chunkSha;
        await using (var hashWindow = new SubStream(OpenSharedRead(encryptedPath), offset, length))
        {
            chunkSha = await FileHasher.Sha256OfStreamAsync(hashWindow, ct);
        }

        // Pass 2: upload with retry; a fresh window per attempt.
        return await RetryPolicy.ExecuteAsync(async token =>
        {
            await using var window = new SubStream(OpenSharedRead(encryptedPath), offset, length);
            return await _api.UploadChunkAsync(sessionId, chunkIndex, chunkSha, window, token);
        }, _options.MaxRetryAttempts, _options.RetryBaseDelay, _logger, $"chunk {chunkIndex}", ct);
    }

    private static long ChunkLength(int chunkIndex, int encryptedChunkSize, long encryptedTotal)
    {
        var offset = (long)chunkIndex * encryptedChunkSize;
        return Math.Min(encryptedChunkSize, encryptedTotal - offset);
    }

    private static FileStream OpenSharedRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream CreateTempWrite(string path) => new(
        path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
        128 * 1024, FileOptions.Asynchronous);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}
