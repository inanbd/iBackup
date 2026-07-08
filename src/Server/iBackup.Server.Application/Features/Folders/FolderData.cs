using System.Text.Json;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Features.Folders;

/// <summary>Row mapping and JSON helpers shared by the folder slices.</summary>
internal static class FolderData
{
    public const string SelectColumns = """
        Id, DeviceId, FolderPath, IsEnabled, Recursive, IncludeHidden, IncludeSystem,
        ExcludedFolders, ExcludedExtensions, ExcludedFileNames, MaxFileSizeBytes, Priority,
        ScheduleType, ScheduleIntervalMinutes, ScheduleTimeOfDayMinutes, ScheduleDayOfWeek, ScheduleDayOfMonth,
        RetentionMode, RetentionValue, CompressionMethod, CreatedAtUtc, UpdatedAtUtc
        """;

    public static string ToJson(IReadOnlyList<string>? values)
        => JsonSerializer.Serialize(values ?? []);

    public static IReadOnlyList<string> FromJson(string? json)
        => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    public static BackupFolderDto Map(SqlDataReader reader)
    {
        var scheduleType = (ScheduleType)reader.GetByte(12);
        var timeOfDayMinutes = reader.IsDBNull(14) ? (int?)null : reader.GetInt32(14);

        return new BackupFolderDto(
            Id: reader.GetGuid(0),
            DeviceId: reader.GetGuid(1),
            Path: reader.GetString(2),
            IsEnabled: reader.GetBoolean(3),
            Recursive: reader.GetBoolean(4),
            IncludeHidden: reader.GetBoolean(5),
            IncludeSystem: reader.GetBoolean(6),
            ExcludedFolders: FromJson(reader.GetString(7)),
            ExcludedExtensions: FromJson(reader.GetString(8)),
            ExcludedFileNames: FromJson(reader.GetString(9)),
            MaxFileSizeBytes: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            Priority: reader.GetInt32(11),
            Schedule: new ScheduleSettings(
                scheduleType,
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                timeOfDayMinutes is null ? null : TimeSpan.FromMinutes(timeOfDayMinutes.Value),
                reader.IsDBNull(15) ? null : (DayOfWeek)reader.GetByte(15),
                reader.IsDBNull(16) ? null : reader.GetByte(16)),
            Retention: new RetentionSettings(
                (RetentionMode)reader.GetByte(17),
                reader.IsDBNull(18) ? null : reader.GetInt32(18)),
            Compression: (CompressionMethod)reader.GetByte(19),
            CreatedAtUtc: reader.GetDateTime(20),
            UpdatedAtUtc: reader.GetDateTime(21));
    }
}
