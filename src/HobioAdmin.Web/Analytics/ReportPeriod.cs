namespace HobioAdmin.Web.Analytics;

public enum ReportPeriod
{
    OneMonth = 1,
    TwoMonths = 2,
    ThreeMonths = 3,
    SixMonths = 6,
    OneYear = 12,
}

public enum BucketSize
{
    Day,
    Week,
    Month,
}

/// <summary>
/// A reporting window. Dates are Toronto local days (Hobio's home market); FromUtc/ToUtc are the exact
/// UTC instants of those local midnights, used to filter the UTC timestamps in the database.
/// The window is [From, To), and To is the start of tomorrow, so "today" is included.
/// </summary>
public sealed record DateRange(DateOnly From, DateOnly To, DateTime FromUtc, DateTime ToUtc, BucketSize Bucket);

public static class ReportPeriods
{
    public static readonly ReportPeriod[] All =
        [ReportPeriod.OneMonth, ReportPeriod.TwoMonths, ReportPeriod.ThreeMonths, ReportPeriod.SixMonths, ReportPeriod.OneYear];

    public static TimeZoneInfo HomeTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    public static string Key(ReportPeriod period) => period switch
    {
        ReportPeriod.OneYear => "1y",
        _ => $"{(int)period}m",
    };

    public static string Label(ReportPeriod period) => period switch
    {
        ReportPeriod.OneMonth => "1 month",
        ReportPeriod.OneYear => "1 year",
        _ => $"{(int)period} months",
    };

    public static ReportPeriod Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "1m" => ReportPeriod.OneMonth,
        "2m" => ReportPeriod.TwoMonths,
        "3m" => ReportPeriod.ThreeMonths,
        "6m" => ReportPeriod.SixMonths,
        "1y" or "12m" => ReportPeriod.OneYear,
        _ => ReportPeriod.ThreeMonths,
    };

    public static BucketSize BucketFor(ReportPeriod period) => period switch
    {
        ReportPeriod.OneMonth => BucketSize.Day,
        ReportPeriod.TwoMonths or ReportPeriod.ThreeMonths => BucketSize.Week,
        _ => BucketSize.Month,
    };

    public static DateRange Resolve(ReportPeriod period, DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? HomeTimeZone;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz));
        var to = localToday.AddDays(1);
        var from = to.AddMonths(-(int)period);
        return Create(from, to, BucketFor(period), tz);
    }

    /// <summary>The window of the same length immediately before <paramref name="range"/>, for growth comparisons.</summary>
    public static DateRange Previous(DateRange range, TimeZoneInfo? timeZone = null)
    {
        var days = range.To.DayNumber - range.From.DayNumber;
        return Create(range.From.AddDays(-days), range.From, range.Bucket, timeZone ?? HomeTimeZone);
    }

    public static DateRange Create(DateOnly from, DateOnly to, BucketSize bucket, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? HomeTimeZone;
        return new DateRange(from, to, LocalMidnightToUtc(from, tz), LocalMidnightToUtc(to, tz), bucket);
    }

    public static DateOnly BucketStart(DateOnly day, BucketSize bucket) => bucket switch
    {
        BucketSize.Day => day,
        // Weeks start on Monday.
        BucketSize.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        BucketSize.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    /// <summary>Every bucket start that overlaps the range, in order, so charts show zero-value buckets too.</summary>
    public static IReadOnlyList<DateOnly> Buckets(DateRange range)
    {
        var result = new List<DateOnly>();
        var current = BucketStart(range.From, range.Bucket);
        while (current < range.To)
        {
            result.Add(current);
            current = range.Bucket switch
            {
                BucketSize.Day => current.AddDays(1),
                BucketSize.Week => current.AddDays(7),
                _ => current.AddMonths(1),
            };
        }

        return result;
    }

    public static string BucketLabel(DateOnly bucketStart, BucketSize bucket) => bucket switch
    {
        BucketSize.Month => bucketStart.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        _ => bucketStart.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static DateTime LocalMidnightToUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Midnight never falls in a DST gap in Toronto (changes happen at 02:00), but guard anyway.
        while (tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
