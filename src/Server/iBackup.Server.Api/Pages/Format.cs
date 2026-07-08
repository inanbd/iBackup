namespace iBackup.Server.Api.Pages;

/// <summary>Display helpers shared by the admin pages.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    public static string When(DateTime? utc)
        => utc is null ? "never" : utc.Value.ToString("yyyy-MM-dd HH:mm") + " UTC";

    public static int Percent(long used, long total)
        => total <= 0 ? 0 : (int)Math.Clamp(used * 100.0 / total, 0, 100);
}
