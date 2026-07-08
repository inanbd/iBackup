namespace iBackup.Server.Infrastructure.Storage;

/// <summary>Bound from the "Storage" configuration section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root directory for all user data (chunks and assembled objects).</summary>
    public string RootPath { get; set; } = "Storage";

    /// <summary>Buffer size for streaming file I/O.</summary>
    public int StreamBufferSize { get; set; } = 128 * 1024;
}
