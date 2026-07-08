using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace iBackup.Client.App.Views;

/// <summary>Shared value converters, exposed as x:Static singletons.</summary>
public static class Converters
{
    public static readonly IValueConverter BoolToVisibility = new BoolToVisibilityConverter();
    public static readonly IValueConverter BytesToText = new BytesToTextConverter();
    public static readonly IValueConverter InvertBool = new InvertBoolConverter();

    private sealed class InvertBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not true;
    }

    private sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    private sealed class BytesToTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var bytes = value switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => 0L
            };
            return Format(bytes);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        internal static string Format(long bytes)
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
    }
}
