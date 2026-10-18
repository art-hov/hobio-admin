using Dapper;
using Microsoft.Data.SqlClient;

namespace HobioAdmin.Web.Analytics;

public interface IAnalyticsRepository
{
    bool IsConfigured { get; }
    Task<KpiSnapshot> GetKpisAsync(DateRange range, int? cityId, CancellationToken ct = default);
    Task<IReadOnlyList<DailyValue>> GetNewUsersDailyAsync(DateRange range, CancellationToken ct = default);
    Task<int> GetUsersBeforeAsync(DateTime utc, CancellationToken ct = default);
    Task<IReadOnlyList<DailyValue>> GetEventsHeldDailyAsync(DateRange range, int? cityId, CancellationToken ct = default);
    Task<IReadOnlyList<DailyValue>> GetBookingsDailyAsync(DateRange range, int? cityId, CancellationToken ct = default);
    Task<IReadOnlyList<DailySumCount>> GetPaidPriceDailyAsync(DateRange range, int? cityId, CancellationToken ct = default);
    Task<IReadOnlyList<DailyRevenue>> GetRevenueDailyAsync(DateRange range, int? cityId, CancellationToken ct = default);
    Task<IReadOnlyList<WeekdayCityCount>> GetWeekdayByCityAsync(DateRange range, CancellationToken ct = default);
    Task<IReadOnlyList<CitySummary>> GetCitySummaryAsync(DateRange range, CancellationToken ct = default);
    Task<IReadOnlyList<MonthlyActive>> GetMonthlyActiveAsync(DateRange range, CancellationToken ct = default);
    Task<IReadOnlyList<CohortRow>> GetCohortsAsync(DateRange range, CancellationToken ct = default);
    Task<IReadOnlyList<CityOption>> GetCitiesAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the [analytics] views with the portal's read-only login. Every query is a parameterised SELECT.
/// </summary>
public sealed class AnalyticsRepository(string? connectionString) : IAnalyticsRepository
{
    private const int CommandTimeoutSeconds = 60;

    // Shared filters. "Held" = scheduled in the window, not cancelled, not a draft.
    private const string EventsHeldFilter =
        "StartAtUtc >= @FromUtc AND StartAtUtc < @ToUtc AND IsCancelled = 0 AND IsDraft = 0";
    private const string PlayerBookingsFilter =
        "BookedAtUtc >= @FromUtc AND BookedAtUtc < @ToUtc AND IsCancelled = 0 AND IsHostSelfBooking = 0";
    private const string RevenueFilter =
        "CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc AND IsReversed = 0";
    private const string CityFilter = "(@CityId IS NULL OR CityId = @CityId)";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(connectionString);

    public async Task<KpiSnapshot> GetKpisAsync(DateRange range, int? cityId, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT
                u.NewUsers, u.TotalUsers,
                ec.EventsCreated,
                eh.EventsHeld, eh.PaidEventsHeld, eh.ActiveHosts, eh.SlotsOffered, eh.SlotsBooked, eh.AvgPaidPrice,
                b.Bookings, b.UniqueBookers, rb.RepeatBookers,
                t.PaidTransactions, t.GrossBookingValue, t.HobioFeeRevenue, t.StripeFeesCollected, t.HostEarnings,
                r.RefundedCount, r.RefundedAmount,
                p.PayoutsCompletedAmount
            FROM
                (SELECT
                    SUM(CASE WHEN CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc THEN 1 ELSE 0 END) AS NewUsers,
                    SUM(CASE WHEN CreatedAtUtc < @ToUtc AND IsDeleted = 0 THEN 1 ELSE 0 END) AS TotalUsers
                 FROM analytics.vw_Users) AS u
            CROSS JOIN
                (SELECT COUNT(*) AS EventsCreated
                 FROM analytics.vw_Events
                 WHERE CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc AND IsDraft = 0 AND {CityFilter}) AS ec
            CROSS JOIN
                (SELECT
                    COUNT(*) AS EventsHeld,
                    SUM(CASE WHEN IsPaidEvent = 1 THEN 1 ELSE 0 END) AS PaidEventsHeld,
                    COUNT(DISTINCT HostUserId) AS ActiveHosts,
                    CAST(SUM(CAST(TotalAvailableSlots AS bigint)) AS bigint) AS SlotsOffered,
                    CAST(SUM(CAST(BookedSlots AS bigint)) AS bigint) AS SlotsBooked,
                    CAST(AVG(CASE WHEN IsPaidEvent = 1 THEN Price END) AS decimal(10,2)) AS AvgPaidPrice
                 FROM analytics.vw_Events
                 WHERE {EventsHeldFilter} AND {CityFilter}) AS eh
            CROSS JOIN
                (SELECT COUNT(*) AS Bookings, COUNT(DISTINCT UserId) AS UniqueBookers
                 FROM analytics.vw_Bookings
                 WHERE {PlayerBookingsFilter} AND {CityFilter}) AS b
            CROSS JOIN
                (SELECT COUNT(*) AS RepeatBookers
                 FROM (SELECT UserId
                       FROM analytics.vw_Bookings
                       WHERE {PlayerBookingsFilter} AND {CityFilter}
                       GROUP BY UserId
                       HAVING COUNT(*) >= 2) AS x) AS rb
            CROSS JOIN
                (SELECT
                    COUNT(*) AS PaidTransactions,
                    COALESCE(SUM(GrossAmount), 0) AS GrossBookingValue,
                    COALESCE(SUM(HobioFeeAmount), 0) AS HobioFeeRevenue,
                    COALESCE(SUM(StripeFeeAmount), 0) AS StripeFeesCollected,
                    COALESCE(SUM(HostAmount), 0) AS HostEarnings
                 FROM analytics.vw_Transactions
                 WHERE {RevenueFilter} AND {CityFilter}) AS t
            CROSS JOIN
                (SELECT COUNT(*) AS RefundedCount, COALESCE(SUM(GrossAmount), 0) AS RefundedAmount
                 FROM analytics.vw_Transactions
                 WHERE CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc AND IsReversed = 1 AND {CityFilter}) AS r
            CROSS JOIN
                (SELECT COALESCE(SUM(Amount), 0) AS PayoutsCompletedAmount
                 FROM analytics.vw_Payouts
                 WHERE CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc AND PayoutStatus = 2) AS p;
            """;

        await using var connection = Open();
        var row = await connection.QuerySingleAsync<KpiRow>(Command(sql, range, cityId, ct));
        return row.ToSnapshot();
    }

    public async Task<IReadOnlyList<DailyValue>> GetNewUsersDailyAsync(DateRange range, CancellationToken ct = default)
    {
        const string sql = """
            SELECT CreatedDateLocal AS Day, CAST(COUNT(*) AS decimal(18,2)) AS Value
            FROM analytics.vw_Users
            WHERE CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc
            GROUP BY CreatedDateLocal;
            """;
        return await QueryAsync<DailyValue>(sql, range, null, ct);
    }

    public async Task<int> GetUsersBeforeAsync(DateTime utc, CancellationToken ct = default)
    {
        // All sign-ups (including later-deleted accounts) so the cumulative chart matches the "new users" bars.
        const string sql = "SELECT COUNT(*) FROM analytics.vw_Users WHERE CreatedAtUtc < @Utc;";
        await using var connection = Open();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Utc = utc },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DailyValue>> GetEventsHeldDailyAsync(DateRange range, int? cityId, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT StartDateLocal AS Day, CAST(COUNT(*) AS decimal(18,2)) AS Value
            FROM analytics.vw_Events
            WHERE {EventsHeldFilter} AND {CityFilter}
            GROUP BY StartDateLocal;
            """;
        return await QueryAsync<DailyValue>(sql, range, cityId, ct);
    }

    public async Task<IReadOnlyList<DailyValue>> GetBookingsDailyAsync(DateRange range, int? cityId, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT BookedDateLocal AS Day, CAST(COUNT(*) AS decimal(18,2)) AS Value
            FROM analytics.vw_Bookings
            WHERE {PlayerBookingsFilter} AND {CityFilter}
            GROUP BY BookedDateLocal;
            """;
        return await QueryAsync<DailyValue>(sql, range, cityId, ct);
    }

    public async Task<IReadOnlyList<DailySumCount>> GetPaidPriceDailyAsync(DateRange range, int? cityId, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT StartDateLocal AS Day, SUM(Price) AS Total, COUNT(*) AS Count
            FROM analytics.vw_Events
            WHERE {EventsHeldFilter} AND {CityFilter} AND IsPaidEvent = 1 AND Price IS NOT NULL
            GROUP BY StartDateLocal;
            """;
        return await QueryAsync<DailySumCount>(sql, range, cityId, ct);
    }

    public async Task<IReadOnlyList<DailyRevenue>> GetRevenueDailyAsync(DateRange range, int? cityId, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT CreatedDateLocal AS Day, SUM(GrossAmount) AS Gross, SUM(HobioFeeAmount) AS HobioFees
            FROM analytics.vw_Transactions
            WHERE {RevenueFilter} AND {CityFilter}
            GROUP BY CreatedDateLocal;
            """;
        return await QueryAsync<DailyRevenue>(sql, range, cityId, ct);
    }

    public async Task<IReadOnlyList<WeekdayCityCount>> GetWeekdayByCityAsync(DateRange range, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT CityId, CityName, StartIsoWeekday AS IsoWeekday, COUNT(*) AS Events,
                   CAST(SUM(CAST(BookedSlots AS bigint)) AS bigint) AS BookedSlots
            FROM analytics.vw_Events
            WHERE {EventsHeldFilter}
            GROUP BY CityId, CityName, StartIsoWeekday;
            """;
        return await QueryAsync<WeekdayCityCount>(sql, range, null, ct);
    }

    public async Task<IReadOnlyList<CitySummary>> GetCitySummaryAsync(DateRange range, CancellationToken ct = default)
    {
        const string sql = $"""
            WITH ev AS (
                SELECT CityId,
                       COUNT(*) AS EventsHeld,
                       COUNT(DISTINCT HostUserId) AS ActiveHosts,
                       CAST(SUM(CAST(TotalAvailableSlots AS bigint)) AS bigint) AS SlotsOffered,
                       CAST(SUM(CAST(BookedSlots AS bigint)) AS bigint) AS SlotsBooked,
                       CAST(AVG(CASE WHEN IsPaidEvent = 1 THEN Price END) AS decimal(10,2)) AS AvgPaidPrice
                FROM analytics.vw_Events
                WHERE {EventsHeldFilter}
                GROUP BY CityId),
            bk AS (
                SELECT CityId, COUNT(*) AS Bookings, COUNT(DISTINCT UserId) AS Players
                FROM analytics.vw_Bookings
                WHERE {PlayerBookingsFilter}
                GROUP BY CityId),
            tx AS (
                SELECT CityId, SUM(GrossAmount) AS GrossBookingValue, SUM(HobioFeeAmount) AS HobioFeeRevenue
                FROM analytics.vw_Transactions
                WHERE {RevenueFilter}
                GROUP BY CityId)
            SELECT c.CityId, c.CityName, c.CountryName,
                   COALESCE(ev.EventsHeld, 0) AS EventsHeld,
                   COALESCE(ev.ActiveHosts, 0) AS ActiveHosts,
                   COALESCE(ev.SlotsOffered, 0) AS SlotsOffered,
                   COALESCE(ev.SlotsBooked, 0) AS SlotsBooked,
                   ev.AvgPaidPrice,
                   COALESCE(bk.Bookings, 0) AS Bookings,
                   COALESCE(bk.Players, 0) AS Players,
                   COALESCE(tx.GrossBookingValue, 0) AS GrossBookingValue,
                   COALESCE(tx.HobioFeeRevenue, 0) AS HobioFeeRevenue
            FROM analytics.vw_Cities AS c
            LEFT JOIN ev ON ev.CityId = c.CityId
            LEFT JOIN bk ON bk.CityId = c.CityId
            LEFT JOIN tx ON tx.CityId = c.CityId
            WHERE ev.CityId IS NOT NULL OR bk.CityId IS NOT NULL OR tx.CityId IS NOT NULL
            ORDER BY COALESCE(ev.EventsHeld, 0) DESC, COALESCE(bk.Bookings, 0) DESC, c.CityName;
            """;
        return await QueryAsync<CitySummary>(sql, range, null, ct);
    }

    public async Task<IReadOnlyList<MonthlyActive>> GetMonthlyActiveAsync(DateRange range, CancellationToken ct = default)
    {
        const string sql = $"""
            WITH hosts AS (
                SELECT DATEFROMPARTS(YEAR(StartDateLocal), MONTH(StartDateLocal), 1) AS Month, COUNT(DISTINCT HostUserId) AS ActiveHosts
                FROM analytics.vw_Events
                WHERE {EventsHeldFilter}
                GROUP BY DATEFROMPARTS(YEAR(StartDateLocal), MONTH(StartDateLocal), 1)),
            players AS (
                SELECT DATEFROMPARTS(YEAR(BookedDateLocal), MONTH(BookedDateLocal), 1) AS Month, COUNT(DISTINCT UserId) AS ActivePlayers
                FROM analytics.vw_Bookings
                WHERE {PlayerBookingsFilter}
                GROUP BY DATEFROMPARTS(YEAR(BookedDateLocal), MONTH(BookedDateLocal), 1))
            SELECT COALESCE(h.Month, p.Month) AS Month,
                   COALESCE(h.ActiveHosts, 0) AS ActiveHosts,
                   COALESCE(p.ActivePlayers, 0) AS ActivePlayers
            FROM hosts AS h
            FULL OUTER JOIN players AS p ON p.Month = h.Month
            ORDER BY Month;
            """;
        return await QueryAsync<MonthlyActive>(sql, range, null, ct);
    }

    public async Task<IReadOnlyList<CohortRow>> GetCohortsAsync(DateRange range, CancellationToken ct = default)
    {
        // Cohort = calendar month of sign-up (Toronto). MonthN = cohort users with at least one player booking
        // made N calendar months after their sign-up month.
        const string sql = """
            WITH cohort AS (
                SELECT UserId, DATEFROMPARTS(YEAR(CreatedDateLocal), MONTH(CreatedDateLocal), 1) AS CohortMonth
                FROM analytics.vw_Users
                WHERE CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc),
            activity AS (
                SELECT DISTINCT UserId, DATEFROMPARTS(YEAR(BookedDateLocal), MONTH(BookedDateLocal), 1) AS ActivityMonth
                FROM analytics.vw_Bookings
                WHERE IsCancelled = 0 AND IsHostSelfBooking = 0 AND BookedAtUtc >= @FromUtc)
            SELECT c.CohortMonth,
                   COUNT(DISTINCT c.UserId) AS CohortSize,
                   COUNT(DISTINCT CASE WHEN DATEDIFF(month, c.CohortMonth, a.ActivityMonth) = 0 THEN c.UserId END) AS Month0,
                   COUNT(DISTINCT CASE WHEN DATEDIFF(month, c.CohortMonth, a.ActivityMonth) = 1 THEN c.UserId END) AS Month1,
                   COUNT(DISTINCT CASE WHEN DATEDIFF(month, c.CohortMonth, a.ActivityMonth) = 2 THEN c.UserId END) AS Month2,
                   COUNT(DISTINCT CASE WHEN DATEDIFF(month, c.CohortMonth, a.ActivityMonth) = 3 THEN c.UserId END) AS Month3
            FROM cohort AS c
            LEFT JOIN activity AS a ON a.UserId = c.UserId
            GROUP BY c.CohortMonth
            ORDER BY c.CohortMonth;
            """;
        return await QueryAsync<CohortRow>(sql, range, null, ct);
    }

    public async Task<IReadOnlyList<CityOption>> GetCitiesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT DISTINCT c.CityId, c.CityName
            FROM analytics.vw_Cities AS c
            WHERE EXISTS (SELECT 1 FROM analytics.vw_Events AS e WHERE e.CityId = c.CityId)
            ORDER BY c.CityName;
            """;
        await using var connection = Open();
        var rows = await connection.QueryAsync<CityOption>(new CommandDefinition(sql,
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, DateRange range, int? cityId, CancellationToken ct)
    {
        await using var connection = Open();
        var rows = await connection.QueryAsync<T>(Command(sql, range, cityId, ct));
        return rows.AsList();
    }

    private static CommandDefinition Command(string sql, DateRange range, int? cityId, CancellationToken ct) =>
        new(sql, new { range.FromUtc, range.ToUtc, CityId = cityId },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct);

    private SqlConnection Open()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("The read-only database connection string is not configured.");
        }

        return new SqlConnection(connectionString);
    }

    // SUM over an empty set returns NULL, so the raw row uses nullable columns.
    private sealed class KpiRow
    {
        public int? NewUsers { get; set; }
        public int? TotalUsers { get; set; }
        public int? EventsCreated { get; set; }
        public int? EventsHeld { get; set; }
        public int? PaidEventsHeld { get; set; }
        public int? ActiveHosts { get; set; }
        public long? SlotsOffered { get; set; }
        public long? SlotsBooked { get; set; }
        public decimal? AvgPaidPrice { get; set; }
        public int? Bookings { get; set; }
        public int? UniqueBookers { get; set; }
        public int? RepeatBookers { get; set; }
        public int? PaidTransactions { get; set; }
        public decimal? GrossBookingValue { get; set; }
        public decimal? HobioFeeRevenue { get; set; }
        public decimal? StripeFeesCollected { get; set; }
        public decimal? HostEarnings { get; set; }
        public int? RefundedCount { get; set; }
        public decimal? RefundedAmount { get; set; }
        public decimal? PayoutsCompletedAmount { get; set; }

        public KpiSnapshot ToSnapshot() => new()
        {
            NewUsers = NewUsers ?? 0,
            TotalUsers = TotalUsers ?? 0,
            EventsCreated = EventsCreated ?? 0,
            EventsHeld = EventsHeld ?? 0,
            PaidEventsHeld = PaidEventsHeld ?? 0,
            ActiveHosts = ActiveHosts ?? 0,
            SlotsOffered = SlotsOffered ?? 0,
            SlotsBooked = SlotsBooked ?? 0,
            AvgPaidPrice = AvgPaidPrice,
            Bookings = Bookings ?? 0,
            UniqueBookers = UniqueBookers ?? 0,
            RepeatBookers = RepeatBookers ?? 0,
            PaidTransactions = PaidTransactions ?? 0,
            GrossBookingValue = GrossBookingValue ?? 0,
            HobioFeeRevenue = HobioFeeRevenue ?? 0,
            StripeFeesCollected = StripeFeesCollected ?? 0,
            HostEarnings = HostEarnings ?? 0,
            RefundedCount = RefundedCount ?? 0,
            RefundedAmount = RefundedAmount ?? 0,
            PayoutsCompletedAmount = PayoutsCompletedAmount ?? 0,
        };
    }
}
