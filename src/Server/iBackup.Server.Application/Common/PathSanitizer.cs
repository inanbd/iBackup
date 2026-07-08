using iBackup.Server.Domain.Exceptions;

namespace iBackup.Server.Application.Common;

/// <summary>
/// Validates and normalizes client-supplied relative paths and file names.
/// Protects the server against path traversal and reserved-name tricks.
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Normalizes a relative path to forward slashes and rejects traversal segments,
    /// rooted paths, and invalid characters. Returns the normalized path.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw AppException.BadRequest("Relative path must not be empty.");
        }

        var normalized = relativePath.Replace('\\', '/').Trim();

        if (normalized.StartsWith('/') || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw AppException.BadRequest("Relative path must not be rooted or contain empty segments.");
        }

        // Reject drive-letter or UNC style paths.
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            throw AppException.BadRequest("Relative path must not contain a drive letter.");
        }

        foreach (var segment in normalized.Split('/'))
        {
            ValidateSegment(segment);
        }

        if (normalized.Length > 1024)
        {
            throw AppException.BadRequest("Relative path is too long.");
        }

        return normalized;
    }

    /// <summary>Validates a single file name (no separators allowed).</summary>
    public static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw AppException.BadRequest("Invalid file name.");
        }
        ValidateSegment(fileName);
        return fileName;
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
        {
            throw AppException.BadRequest("Path contains an invalid segment.");
        }

        if (segment.IndexOfAny(InvalidNameChars) >= 0)
        {
            throw AppException.BadRequest("Path contains invalid characters.");
        }

        var baseName = segment.Split('.')[0];
        if (ReservedNames.Contains(baseName))
        {
            throw AppException.BadRequest("Path contains a reserved name.");
        }
    }
}
