namespace HobioAdmin.Web.Analytics;

public sealed record Series(string Name, double[] Values);

public sealed record TimeSeries(IReadOnlyList<DateOnly> Buckets, IReadOnlyList<string> Labels, IReadOnlyList<Series> Series)
{
    public bool IsEmpty => Series.All(s => s.Values.All(v => v == 0));
}

public sealed record OverviewData(
    ReportPeriod Period,
    DateRange Range,
    DateRange PreviousRange,
    KpiSnapshot Current,
    KpiSnapshot Previous,
    TimeSeries Users,
    TimeSeries Activity,
    TimeSeries AveragePrice,
    TimeSeries Revenue);

public sealed record WeekdayMatrix(IReadOnlyList<string> Cities, int[,] Events, int MaxCell)
{
    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
}

public sealed record CitiesData(ReportPeriod Period, DateRange Range, WeekdayMatrix Weekdays, IReadOnlyList<CitySummary> Cities);

public sealed record MonthRow(
    DateOnly Month,
    int NewUsers,
    int TotalUsers,
    int EventsHeld,
    int Bookings,
    decimal GrossBookingValue,
    decimal HobioFeeRevenue,
    int ActiveHosts,
    int ActivePlayers);

public sealed record InvestorReportData(
    ReportPeriod Period,
    DateRange Range,
    DateRange PreviousRange,
    KpiSnapshot Current,
    KpiSnapshot Previous,
    IReadOnlyList<MonthRow> Months,
    IReadOnlyList<CitySummary> TopCities,
    IReadOnlyList<CohortRow> Cohorts,
    DateTime GeneratedAtUtc);

public interface IAnalyticsService
{
    bool IsConfigured { get; }
    Task<OverviewData> GetOverviewAsync(ReportPeriod period, int? cityId, CancellationToken ct = default);
    Task<CitiesData> GetCitiesAsync(ReportPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<CohortRow>> GetCohortsAsync(CancellationToken ct = default);
    Task<InvestorReportData> GetInvestorReportAsync(ReportPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<CityOption>> GetCityOptionsAsync(CancellationToken ct = default);
}

public sealed class AnalyticsService(IAnalyticsRepository repository, TimeProvider clock) : IAnalyticsService
{
    public bool IsConfigured => repository.IsConfigured;

    public async Task<OverviewData> GetOverviewAsync(ReportPeriod period, int? cityId, CancellationToken ct = default)
    {
        var range = ReportPeriods.Resolve(period, clock.GetUtcNow().UtcDateTime);
        var previous = ReportPeriods.Previous(range);

        var current = await repository.GetKpisAsync(range, cityId, ct);
        var prior = await repository.GetKpisAsync(previous, cityId, ct);
        var newUsers = await repository.GetNewUsersDailyAsync(range, ct);
        var usersBefore = await repository.GetUsersBeforeAsync(range.FromUtc, ct);
        var eventsHeld = await repository.GetEventsHeldDailyAsync(range, cityId, ct);
        var bookings = await repository.GetBookingsDailyAsync(range, cityId, ct);
        var prices = await repository.GetPaidPriceDailyAsync(range, cityId, ct);
        var revenue = await repository.GetRevenueDailyAsync(range, cityId, ct);

        var buckets = ReportPeriods.Buckets(range);
        var labels = Labels(buckets, range.Bucket);

        var newUsersPerBucket = SumByBucket(newUsers.Select(d => (d.Day, d.Value)), buckets, range.Bucket);
        var cumulative = Cumulative(newUsersPerBucket, usersBefore);

        var users = new TimeSeries(buckets, labels,
        [
            new Series("New users", newUsersPerBucket),
            new Series("Total sign-ups", cumulative),
        ]);

        var activity = new TimeSeries(buckets, labels,
        [
            new Series("Events held", SumByBucket(eventsHeld.Select(d => (d.Day, d.Value)), buckets, range.Bucket)),
            new Series("Bookings", SumByBucket(bookings.Select(d => (d.Day, d.Value)), buckets, range.Bucket)),
        ]);

        var averagePrice = new TimeSeries(buckets, labels,
        [
            new Series("Average paid event price ($)", WeightedAverageByBucket(prices, buckets, range.Bucket)),
        ]);

        var revenueSeries = new TimeSeries(buckets, labels,
        [
            new Series("Gross booking value ($)", SumByBucket(revenue.Select(d => (d.Day, d.Gross)), buckets, range.Bucket)),
            new Series("Hobio fee revenue ($)", SumByBucket(revenue.Select(d => (d.Day, d.HobioFees)), buckets, range.Bucket)),
        ]);

        return new OverviewData(period, range, previous, current, prior, users, activity, averagePrice, revenueSeries);
    }

    public async Task<CitiesData> GetCitiesAsync(ReportPeriod period, CancellationToken ct = default)
    {
        var range = ReportPeriods.Resolve(period, clock.GetUtcNow().UtcDateTime);
        var weekdays = await repository.GetWeekdayByCityAsync(range, ct);
        var cities = await repository.GetCitySummaryAsync(range, ct);
        return new CitiesData(period, range, BuildWeekdayMatrix(weekdays), cities);
    }

    public async Task<IReadOnlyList<CohortRow>> GetCohortsAsync(CancellationToken ct = default)
    {
        var range = ReportPeriods.Resolve(ReportPeriod.OneYear, clock.GetUtcNow().UtcDateTime);
        var monthAligned = ReportPeriods.Create(new DateOnly(range.From.Year, range.From.Month, 1), range.To, BucketSize.Month);
        return await repository.GetCohortsAsync(monthAligned, ct);
    }

    public async Task<InvestorReportData> GetInvestorReportAsync(ReportPeriod period, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var range = ReportPeriods.Resolve(period, now);
        var previous = ReportPeriods.Previous(range);
        var current = await repository.GetKpisAsync(range, null, ct);
        var prior = await repository.GetKpisAsync(previous, null, ct);

        // Month-by-month table always covers the last 12 calendar months (including the current one).
        var yearRange = ReportPeriods.Resolve(ReportPeriod.OneYear, now);
        var months = ReportPeriods.Create(new DateOnly(yearRange.To.AddDays(-1).Year, yearRange.To.AddDays(-1).Month, 1).AddMonths(-11),
            yearRange.To, BucketSize.Month);
        var monthBuckets = ReportPeriods.Buckets(months);

        var newUsers = SumByBucket((await repository.GetNewUsersDailyAsync(months, ct)).Select(d => (d.Day, d.Value)), monthBuckets, BucketSize.Month);
        var usersBefore = await repository.GetUsersBeforeAsync(months.FromUtc, ct);
        var totalUsers = Cumulative(newUsers, usersBefore);
        var events = SumByBucket((await repository.GetEventsHeldDailyAsync(months, null, ct)).Select(d => (d.Day, d.Value)), monthBuckets, BucketSize.Month);
        var bookings = SumByBucket((await repository.GetBookingsDailyAsync(months, null, ct)).Select(d => (d.Day, d.Value)), monthBuckets, BucketSize.Month);
        var revenue = await repository.GetRevenueDailyAsync(months, null, ct);
        var gross = SumByBucket(revenue.Select(d => (d.Day, d.Gross)), monthBuckets, BucketSize.Month);
        var fees = SumByBucket(revenue.Select(d => (d.Day, d.HobioFees)), monthBuckets, BucketSize.Month);
        var active = (await repository.GetMonthlyActiveAsync(months, ct))
            .ToDictionary(a => DateOnly.FromDateTime(a.Month));

        var monthRows = monthBuckets.Select((m, i) => new MonthRow(
            m,
            (int)newUsers[i],
            (int)totalUsers[i],
            (int)events[i],
            (int)bookings[i],
            (decimal)gross[i],
            (decimal)fees[i],
            active.TryGetValue(m, out var a) ? a.ActiveHosts : 0,
            active.TryGetValue(m, out var p) ? p.ActivePlayers : 0)).ToList();

        var topCities = (await repository.GetCitySummaryAsync(range, ct)).Take(10).ToList();
        var cohorts = await repository.GetCohortsAsync(months, ct);

        return new InvestorReportData(period, range, previous, current, prior, monthRows, topCities, cohorts, now);
    }

    public Task<IReadOnlyList<CityOption>> GetCityOptionsAsync(CancellationToken ct = default) => repository.GetCitiesAsync(ct);

    internal static IReadOnlyList<string> Labels(IReadOnlyList<DateOnly> buckets, BucketSize bucket)
    {
        // Thin out labels so the x axis stays readable (about 12 labels max).
        var step = Math.Max(1, (int)Math.Ceiling(buckets.Count / 12.0));
        return buckets.Select((b, i) => i % step == 0 ? ReportPeriods.BucketLabel(b, bucket) : string.Empty).ToList();
    }

    internal static double[] SumByBucket(IEnumerable<(DateTime Day, decimal Value)> daily, IReadOnlyList<DateOnly> buckets, BucketSize bucket)
    {
        var index = buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var result = new double[buckets.Count];
        foreach (var (day, value) in daily)
        {
            if (index.TryGetValue(ReportPeriods.BucketStart(DateOnly.FromDateTime(day), bucket), out var i))
            {
                result[i] += (double)value;
            }
        }

        return result.Select(v => Math.Round(v, 2)).ToArray();
    }

    internal static double[] WeightedAverageByBucket(IEnumerable<DailySumCount> daily, IReadOnlyList<DateOnly> buckets, BucketSize bucket)
    {
        var index = buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var totals = new decimal[buckets.Count];
        var counts = new int[buckets.Count];
        foreach (var d in daily)
        {
            if (index.TryGetValue(ReportPeriods.BucketStart(DateOnly.FromDateTime(d.Day), bucket), out var i))
            {
                totals[i] += d.Total;
                counts[i] += d.Count;
            }
        }

        return totals.Select((t, i) => counts[i] > 0 ? Math.Round((double)(t / counts[i]), 2) : 0d).ToArray();
    }

    internal static double[] Cumulative(double[] values, int startingTotal)
    {
        var result = new double[values.Length];
        double running = startingTotal;
        for (var i = 0; i < values.Length; i++)
        {
            running += values[i];
            result[i] = running;
        }

        return result;
    }

    internal static WeekdayMatrix BuildWeekdayMatrix(IReadOnlyList<WeekdayCityCount> rows)
    {
        var cities = rows
            .GroupBy(r => r.CityName)
            .OrderByDescending(g => g.Sum(r => r.Events))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();

        var matrix = new int[cities.Count, 7];
        var max = 0;
        foreach (var row in rows)
        {
            var c = cities.IndexOf(row.CityName);
            var d = row.IsoWeekday - 1;
            if (c < 0 || d is < 0 or > 6)
            {
                continue;
            }

            matrix[c, d] += row.Events;
            max = Math.Max(max, matrix[c, d]);
        }

        return new WeekdayMatrix(cities, matrix, max);
    }
}
