using iBackup.Shared;

namespace iBackup.Client.Core.Configuration;

/// <summary>Client-side engine configuration.</summary>
public sealed class ClientOptions
{
    public const string SectionName = "iBackup";

    /// <summary>Base URL of the backup server, e.g. https://backup.example.com.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext payload bytes per chunk. Encrypted chunks are 28 bytes larger
    /// (12-byte nonce + 16-byte GCM tag). Default 100 MB, configurable.
    /// </summary>
    public int ChunkSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Files uploaded concurrently.</summary>
    public int ParallelFileUploads { get; set; } = 4;

    /// <summary>Chunks of a single file uploaded concurrently.</summary>
    public int ParallelChunkUploads { get; set; } = 4;

    /// <summary>Default compression for new folders.</summary>
    public CompressionMethod Compression { get; set; } = CompressionMethod.Zstd;

    /// <summary>Maximum upload retry attempts before a file is marked failed.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Interval of the consistency scan that catches missed filesystem events.</summary>
    public TimeSpan ConsistencyScanInterval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Local application data directory (state database, temp files, logs).</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iBackup");

    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string StateDatabasePath => Path.Combine(DataDirectory, "state.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}
