/*
    iBackup - default application settings.
    Idempotent.
*/

MERGE dbo.ApplicationSettings AS target
USING (VALUES
    (N'DefaultQuotaBytes',            N'107374182400'),
    (N'MaxChunkSizeBytes',            N'134217728'),
    (N'AbandonedUploadCutoffHours',   N'48'),
    (N'RetentionSweepIntervalMinutes',N'60')
) AS src (SettingKey, SettingValue)
ON target.SettingKey = src.SettingKey
WHEN NOT MATCHED THEN
    INSERT (SettingKey, SettingValue) VALUES (src.SettingKey, src.SettingValue);
