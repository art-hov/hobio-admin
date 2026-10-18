using HobioAdmin.DbTool;
using HobioAdmin.Web.Analytics;
using Microsoft.Data.SqlClient;

namespace HobioAdmin.Tests;

/// <summary>
/// Creates a throwaway database with the Tushpa tables, applies the real /db scripts, seeds a known data set
/// and checks every analytics query, reading through the read-only login exactly like the portal does.
///
/// Needs a SQL Server: set HOBIO_TEST_SQL to an admin connection string (CI starts one in Docker).
/// Without it the tests are skipped, unless HOBIO_REQUIRE_SQL_TESTS=1 (set in CI) makes that a failure.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ReaderPassword = "Rdr!Passw0rd-Strong#2026";

    public string? AdminConnectionString { get; private set; }
    public string? ReaderConnectionString { get; private set; }
    public string ReaderLogin { get; } = "hobio_reader_" + Guid.NewGuid().ToString("N")[..8];
    private string _databaseName = string.Empty;
    private string? _serverConnectionString;

    public bool Available => ReaderConnectionString is not null;

    public async Task InitializeAsync()
    {
        _serverConnectionString = Environment.GetEnvironmentVariable("HOBIO_TEST_SQL");
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            if (Environment.GetEnvironmentVariable("HOBIO_REQUIRE_SQL_TESTS") == "1")
            {
                throw new InvalidOperationException("HOBIO_TEST_SQL must be set when HOBIO_REQUIRE_SQL_TESTS=1.");
            }

            return;
        }

        _databaseName = "HobioAdminTest_" + Guid.NewGuid().ToString("N")[..8];
        var server = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master", TrustServerCertificate = true };
        await SqlScripts.ExecuteBatchesAsync(server.ConnectionString, $"CREATE DATABASE [{_databaseName}];");

        AdminConnectionString = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        await SqlScripts.ExecuteBatchesAsync(AdminConnectionString, LoadFixture());
        await SqlScripts.ApplyViewsAsync(AdminConnectionString);
        await SqlScripts.ApplyViewsAsync(AdminConnectionString); // re-runnable
        await SqlScripts.CreateReaderAsync(AdminConnectionString, ReaderPassword, ReaderLogin);
        await SqlScripts.CreateReaderAsync(AdminConnectionString, ReaderPassword, ReaderLogin); // re-runnable
        await SqlScripts.ExecuteBatchesAsync(AdminConnectionString, SeedSql);

        ReaderConnectionString = new SqlConnectionStringBuilder(AdminConnectionString)
        {
            IntegratedSecurity = false,
            UserID = ReaderLogin,
            Password = ReaderPassword,
            ApplicationIntent = ApplicationIntent.ReadOnly,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_serverConnectionString is null || _databaseName.Length == 0)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        var server = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master", TrustServerCertificate = true };
        await SqlScripts.ExecuteBatchesAsync(server.ConnectionString,
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; DROP LOGIN [{ReaderLogin}];");
    }

    private static string LoadFixture()
    {
        using var stream = typeof(SqlServerFixture).Assembly.GetManifestResourceStream("tushpa-schema-fixture.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // All timestamps are UTC, like Tushpa. Toronto is UTC-4 in October 2026.
    private const string SeedSql = """
        DECLARE @H  uniqueidentifier = '00000000-0000-0000-0000-0000000000a0';
        DECLARE @P1 uniqueidentifier = '00000000-0000-0000-0000-0000000000a1';
        DECLARE @P2 uniqueidentifier = '00000000-0000-0000-0000-0000000000a2';
        DECLARE @P3 uniqueidentifier = '00000000-0000-0000-0000-0000000000a3';
        DECLARE @P4 uniqueidentifier = '00000000-0000-0000-0000-0000000000a4';

        INSERT INTO dbo.Users (Id, FirstName, LastName, DateOfBirth, CreatedAt, ReferralCode, IsDeleted, ReferredByUserId) VALUES
            (@H,  N'Host', N'One',   '1990-01-01', '2026-08-15T12:00:00', N'H',  0, NULL),
            (@P1, N'P',    N'One',   '1990-01-01', '2026-09-10T12:00:00', N'P1', 0, NULL),
            (@P2, N'P',    N'Two',   '1990-01-01', '2026-10-02T12:00:00', N'P2', 0, NULL),
            (@P3, N'P',    N'Three', '1990-01-01', '2026-10-03T12:00:00', N'P3', 1, NULL),
            (@P4, N'P',    N'Four',  '1990-01-01', '2026-10-20T12:00:00', N'P4', 0, @P1);

        INSERT INTO dbo.Countries (Name, Code) VALUES (N'Canada', N'CA');
        INSERT INTO dbo.Cities (Name, CountryId, TimeZoneId, CityCode) VALUES (N'Ottawa', 1, 'America/Toronto', 1), (N'Gatineau', 1, 'America/Toronto', 2);
        INSERT INTO dbo.Sports (Id, Name, Slug) VALUES (1, N'Soccer', N'soccer');
        INSERT INTO dbo.Venues (Name, Address, CityId, CreatedByUserId) VALUES (N'Field A', N'1 Main St', 1, @H), (N'Field B', N'2 Main St', 2, @H);

        -- E1 Ottawa paid $20, Sat Oct 3 19:30 Toronto
        -- E2 Gatineau free, Mon Oct 5 10:00 Toronto (completed)
        -- E3 Ottawa cancelled, E4 Ottawa draft
        -- E5 Ottawa paid $30, Windows time zone id, Sun Oct 4 03:30 UTC = Sat Oct 3 23:30 Toronto
        -- E6 Ottawa paid $50 in September (outside the October window)
        INSERT INTO dbo.Events (EventName, SportId, VenueId, IsHostPlaying, StartDate, DurationMinutes, EventType, IsPaidEvent, Price,
                                TotalAvailableSlots, BookedSlots, LocationType, EventStatus, CreatedByUserId, CreatedAt, TimeZoneId) VALUES
            (N'E1', 1, 1, 1, '2026-10-03T23:30:00', 60, 1, 1, 20.00, 10, 3, 1, 2, @H, '2026-10-01T12:00:00', 'America/Toronto'),
            (N'E2', 1, 2, 1, '2026-10-05T14:00:00', 60, 1, 0, NULL,   8, 2, 1, 5, @H, '2026-09-25T12:00:00', 'America/Toronto'),
            (N'E3', 1, 1, 1, '2026-10-06T14:00:00', 60, 1, 1, 25.00, 10, 0, 1, 4, @H, '2026-09-25T12:00:00', 'America/Toronto'),
            (N'E4', 1, 1, 1, '2026-10-07T14:00:00', 60, 1, 1, 25.00, 10, 0, 1, 1, @H, '2026-10-01T12:00:00', 'America/Toronto'),
            (N'E5', 1, 1, 1, '2026-10-04T03:30:00', 60, 1, 1, 30.00, 10, 0, 1, 2, @H, '2026-10-02T12:00:00', 'Eastern Standard Time'),
            (N'E6', 1, 1, 1, '2026-09-20T14:00:00', 60, 1, 1, 50.00, 10, 1, 1, 5, @H, '2026-09-01T12:00:00', 'America/Toronto');

        INSERT INTO dbo.EventBookings (EventId, UserId, BookedAt, PaidAmount, PaymentStatus, Status, Token) VALUES
            (1, @H,  '2026-10-01T13:00:00', 0,  2, 1, N't0'), -- host's own spot: excluded
            (1, @P1, '2026-10-02T13:00:00', 21, 2, 1, N't1'),
            (1, @P2, '2026-10-02T14:00:00', 21, 2, 1, N't2'),
            (2, @P1, '2026-10-04T13:00:00', 0,  2, 1, N't3'),
            (1, @P3, '2026-10-03T13:00:00', 0,  1, 2, N't4'); -- cancelled: excluded

        INSERT INTO dbo.Transactions (BookingHoldId, BookingId, EventId, UserId, GrossAmount, HostAmount, HobioFeeAmount, StripeFeeAmount, CreatedAt, RefundStatus) VALUES
            (1, 2, 1, @P1, 21.00, 19.00, 1.00, 1.00, '2026-10-02T13:01:00', 1),
            (2, 3, 1, @P2, 21.00, 19.00, 1.00, 1.00, '2026-10-02T14:01:00', 3); -- refunded: excluded from revenue

        INSERT INTO dbo.Payouts (HostUserId, Amount, HobioFeeAmount, Status, CreatedAt) VALUES
            (@H, 100.00, 3.00, 2, '2026-10-10T12:00:00'),
            (@H,  50.00, 1.50, 3, '2026-10-11T12:00:00'); -- failed: excluded
        """;
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;

[Collection("sql")]
public sealed class AnalyticsDatabaseTests(SqlServerFixture db)
{
    private static readonly DateRange October = ReportPeriods.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), BucketSize.Month);

    private AnalyticsRepository Repo() => new(db.ReaderConnectionString);

    [Fact]
    public async Task Kpis_match_seeded_data()
    {
        if (!db.Available) return;

        var k = await Repo().GetKpisAsync(October, cityId: null);

        Assert.Equal(3, k.NewUsers);           // P2, P3, P4
        Assert.Equal(4, k.TotalUsers);         // everyone except deleted P3
        Assert.Equal(2, k.EventsCreated);      // E1, E5 (E4 is a draft)
        Assert.Equal(3, k.EventsHeld);         // E1, E2, E5
        Assert.Equal(2, k.PaidEventsHeld);
        Assert.Equal(1, k.ActiveHosts);
        Assert.Equal(28, k.SlotsOffered);
        Assert.Equal(5, k.SlotsBooked);
        Assert.Equal(25.00m, k.AvgPaidPrice);  // (20 + 30) / 2
        Assert.Equal(3, k.Bookings);           // host's own spot and the cancellation excluded
        Assert.Equal(2, k.UniqueBookers);
        Assert.Equal(1, k.RepeatBookers);      // P1 booked twice
        Assert.Equal(1, k.PaidTransactions);
        Assert.Equal(21.00m, k.GrossBookingValue);
        Assert.Equal(1.00m, k.HobioFeeRevenue);
        Assert.Equal(1.00m, k.StripeFeesCollected);
        Assert.Equal(19.00m, k.HostEarnings);
        Assert.Equal(1, k.RefundedCount);
        Assert.Equal(21.00m, k.RefundedAmount);
        Assert.Equal(100.00m, k.PayoutsCompletedAmount);
    }

    [Fact]
    public async Task City_filter_limits_event_metrics()
    {
        if (!db.Available) return;

        var gatineau = await Repo().GetKpisAsync(October, cityId: 2);

        Assert.Equal(1, gatineau.EventsHeld);
        Assert.Equal(1, gatineau.Bookings);
        Assert.Equal(0m, gatineau.GrossBookingValue);
        Assert.Null(gatineau.AvgPaidPrice);
    }

    [Fact]
    public async Task Empty_window_returns_zeros()
    {
        if (!db.Available) return;

        var k = await Repo().GetKpisAsync(ReportPeriods.Create(new DateOnly(2020, 1, 1), new DateOnly(2020, 2, 1), BucketSize.Month), null);

        Assert.Equal(0, k.EventsHeld);
        Assert.Equal(0m, k.GrossBookingValue);
        Assert.Null(k.FillRate);
    }

    [Fact]
    public async Task Event_start_uses_local_time_zone_for_weekday_and_date()
    {
        if (!db.Available) return;

        var rows = await Repo().GetWeekdayByCityAsync(October);

        // E1 and E5 are both Saturday evening in Toronto (E5 is Sunday in UTC).
        var ottawa = Assert.Single(rows, r => r.CityName == "Ottawa");
        Assert.Equal(6, ottawa.IsoWeekday);
        Assert.Equal(2, ottawa.Events);

        var gatineau = Assert.Single(rows, r => r.CityName == "Gatineau");
        Assert.Equal(1, gatineau.IsoWeekday);

        var daily = await Repo().GetEventsHeldDailyAsync(October, null);
        Assert.Equal(2m, daily.Single(d => d.Day == new DateTime(2026, 10, 3)).Value);
        Assert.Equal(1m, daily.Single(d => d.Day == new DateTime(2026, 10, 5)).Value);
    }

    [Fact]
    public async Task Daily_series_and_revenue()
    {
        if (!db.Available) return;

        var users = await Repo().GetNewUsersDailyAsync(October);
        Assert.Equal(3m, users.Sum(u => u.Value));

        var before = await Repo().GetUsersBeforeAsync(October.FromUtc);
        Assert.Equal(2, before); // H and P1

        var bookings = await Repo().GetBookingsDailyAsync(October, null);
        Assert.Equal(2m, bookings.Single(b => b.Day == new DateTime(2026, 10, 2)).Value);

        var prices = await Repo().GetPaidPriceDailyAsync(October, null);
        var oct3 = prices.Single(p => p.Day == new DateTime(2026, 10, 3));
        Assert.Equal(50m, oct3.Total);
        Assert.Equal(2, oct3.Count);

        var revenue = await Repo().GetRevenueDailyAsync(October, null);
        Assert.Equal(21m, Assert.Single(revenue).Gross);
    }

    [Fact]
    public async Task City_summary_and_city_options()
    {
        if (!db.Available) return;

        var cities = await Repo().GetCitySummaryAsync(October);
        Assert.Equal("Ottawa", cities[0].CityName);
        Assert.Equal(2, cities[0].EventsHeld);
        Assert.Equal(2, cities[0].Bookings);
        Assert.Equal(21m, cities[0].GrossBookingValue);
        Assert.Equal("Gatineau", cities[1].CityName);
        Assert.Equal(1, cities[1].Bookings);

        var options = await Repo().GetCitiesAsync();
        Assert.Equal(new[] { "Gatineau", "Ottawa" }, options.Select(o => o.CityName));
    }

    [Fact]
    public async Task Monthly_active_and_cohorts()
    {
        if (!db.Available) return;

        var active = await Repo().GetMonthlyActiveAsync(October);
        var oct = Assert.Single(active);
        Assert.Equal(1, oct.ActiveHosts);
        Assert.Equal(2, oct.ActivePlayers);

        var cohorts = await Repo().GetCohortsAsync(ReportPeriods.Create(new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 1), BucketSize.Month));
        var sep = cohorts.Single(c => c.CohortMonth == new DateTime(2026, 9, 1));
        Assert.Equal(1, sep.CohortSize);   // P1
        Assert.Equal(0, sep.Month0);
        Assert.Equal(1, sep.Month1);       // P1 booked in October

        var octCohort = cohorts.Single(c => c.CohortMonth == new DateTime(2026, 10, 1));
        Assert.Equal(3, octCohort.CohortSize); // P2, P3, P4
        Assert.Equal(1, octCohort.Month0);     // only P2 (P3's booking was cancelled)
    }

    [Fact]
    public async Task Service_builds_overview_for_a_fixed_clock()
    {
        if (!db.Available) return;

        var service = new AnalyticsService(Repo(), new FixedClock(new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc)));
        var overview = await service.GetOverviewAsync(ReportPeriod.OneMonth, null);

        Assert.Equal(new DateOnly(2026, 10, 1), overview.Range.From);
        Assert.Equal(3, overview.Current.EventsHeld);
        Assert.Equal(31, overview.Users.Buckets.Count);
        Assert.Equal(5d, overview.Users.Series[1].Values[^1]); // 2 before + 3 new sign-ups
        Assert.Equal(3d, overview.Activity.Series[1].Values.Sum());

        var report = await service.GetInvestorReportAsync(ReportPeriod.OneMonth);
        Assert.Equal(12, report.Months.Count);
        Assert.Equal(new DateOnly(2026, 10, 1), report.Months[^1].Month);
        Assert.Equal(3, report.Months[^1].Bookings);
    }

    [Fact]
    public async Task Reader_cannot_read_or_change_base_tables()
    {
        if (!db.Available) return;

        await using var connection = new SqlConnection(db.ReaderConnectionString);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var cmd = new SqlCommand("SELECT TOP 1 Email FROM dbo.Users", connection);
            await cmd.ExecuteScalarAsync();
        });

        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var cmd = new SqlCommand("DELETE FROM analytics.vw_Payouts", connection);
            await cmd.ExecuteNonQueryAsync();
        });

        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var cmd = new SqlCommand("CREATE TABLE dbo.Hack (Id int)", connection);
            await cmd.ExecuteNonQueryAsync();
        });

        // ...but the views work through ownership chaining.
        await using var ok = new SqlCommand("SELECT COUNT(*) FROM analytics.vw_Users", connection);
        Assert.Equal(5, (int)(await ok.ExecuteScalarAsync())!);
    }

}

public sealed class FixedClock(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
