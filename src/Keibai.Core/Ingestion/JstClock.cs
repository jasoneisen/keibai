namespace Keibai.Core.Ingestion;

/// <summary>
/// Japan Standard Time helpers. BIT is a Japanese system: night-hours crawling, 開札 publication times,
/// and the per-day <see cref="Domain.DailyStats"/> key are all reckoned in JST regardless of host TZ.
/// </summary>
public static class JstClock
{
    /// <summary>JST (UTC+9, no DST).</summary>
    public static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    /// <summary>Current wall-clock instant in JST.</summary>
    public static DateTimeOffset Now(TimeProvider time) => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    /// <summary>Today's date in JST.</summary>
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(Now(time).DateTime);

    /// <summary>The <c>DailyStats</c> id for today (JST) — <c>yyyy-MM-dd</c>.</summary>
    public static string TodayKey(TimeProvider time) =>
        Now(time).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
