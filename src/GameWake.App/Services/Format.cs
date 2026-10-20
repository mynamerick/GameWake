using System.Globalization;

namespace GameWake.App.Services;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    /// <summary>"45 s", "12 min", "2 h 05 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds} s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes} min";
        }

        return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
    }

    /// <summary>"0:42", "12:05", "1:02:09".</summary>
    public static string Clock(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    public static string Time(DateTimeOffset at, DateTimeOffset now) =>
        at.LocalDateTime.Date == now.LocalDateTime.Date
            ? at.LocalDateTime.ToString("T", CultureInfo.CurrentCulture)
            : at.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}
