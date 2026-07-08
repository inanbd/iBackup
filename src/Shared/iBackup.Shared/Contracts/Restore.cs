namespace iBackup.Shared.Contracts;

/// <summary>A stored version of a file.</summary>
public sealed record FileVersionDto(
    Guid VersionId,
    int VersionNumber,
    long OriginalSize,
    long EncryptedSize,
    string Sha256,
    DateTime FileModifiedAtUtc,
    DateTime BackedUpAtUtc,
    CompressionMethod Compression,
    EncryptionMethod Encryption,
    int ChunkSize);

/// <summary>A backed-up file with its version history, as shown in the restore browser.</summary>
public sealed record RestoreFileItemDto(
    Guid FileId,
    Guid FolderId,
    string RelativePath,
    string FileName,
    bool IsDeleted,
    DateTime? DeletedAtUtc,
    IReadOnlyList<FileVersionDto> Versions);

/// <summary>Requests restore download tickets for a set of file versions.</summary>
public sealed record CreateRestoreRequest(IReadOnlyList<Guid> FileVersionIds);

public sealed record RestoreItemDto(
    Guid FileVersionId,
    Guid FileId,
    string RelativePath,
    string FileName,
    long OriginalSize,
    long EncryptedSize,
    CompressionMethod Compression,
    EncryptionMethod Encryption,
    int ChunkSize,
    string DownloadUrl);

public sealed record CreateRestoreResponse(Guid RestoreId, IReadOnlyList<RestoreItemDto> Items);
