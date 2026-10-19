using HobioAdmin.DbTool;
using HobioAdmin.Web.Analytics;
using HobioAdmin.Web.Auth;
using HobioAdmin.Web.Components.Shared;
using HobioAdmin.Web.Configuration;
using Microsoft.Extensions.Options;

namespace HobioAdmin.Tests;

public class ReportPeriodTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    [Theory]
    [InlineData("1m", ReportPeriod.OneMonth)]
    [InlineData("2m", ReportPeriod.TwoMonths)]
    [InlineData("3m", ReportPeriod.ThreeMonths)]
    [InlineData("6m", ReportPeriod.SixMonths)]
    [InlineData("1y", ReportPeriod.OneYear)]
    [InlineData("12m", ReportPeriod.OneYear)]
    [InlineData(null, ReportPeriod.ThreeMonths)]
    [InlineData("nonsense", ReportPeriod.ThreeMonths)]
    public void Parse_maps_keys(string? key, ReportPeriod expected) =>
        Assert.Equal(expected, ReportPeriods.Parse(key));

    [Fact]
    public void Resolve_uses_toronto_today_and_includes_it()
    {
        // 2026-10-18 02:00 UTC is still 2026-10-17 22:00 in Toronto (EDT, UTC-4).
        var range = ReportPeriods.Resolve(ReportPeriod.OneMonth, new DateTime(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc), Toronto);

        Assert.Equal(new DateOnly(2026, 10, 18), range.To);
        Assert.Equal(new DateOnly(2026, 9, 18), range.From);
        Assert.Equal(new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc), range.ToUtc);
        Assert.Equal(new DateTime(2026, 9, 18, 4, 0, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(BucketSize.Day, range.Bucket);
    }

    [Fact]
    public void Resolve_handles_dst_in_winter()
    {
        // January: Toronto is EST, UTC-5.
        var range = ReportPeriods.Resolve(ReportPeriod.OneYear, new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc), Toronto);

        Assert.Equal(new DateTime(2027, 1, 16, 5, 0, 0, DateTimeKind.Utc), range.ToUtc);
        Assert.Equal(new DateOnly(2026, 1, 16), range.From);
        Assert.Equal(new DateTime(2026, 1, 16, 5, 0, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(BucketSize.Month, range.Bucket);
    }

    [Fact]
    public void Previous_is_same_length_immediately_before()
    {
        var range = ReportPeriods.Create(new DateOnly(2026, 7, 19), new DateOnly(2026, 10, 19), BucketSize.Week, Toronto);
        var previous = ReportPeriods.Previous(range, Toronto);

        Assert.Equal(range.From, previous.To);
        Assert.Equal(range.To.DayNumber - range.From.DayNumber, previous.To.DayNumber - previous.From.DayNumber);
    }

    [Theory]
    [InlineData(BucketSize.Week, "2026-10-18", "2026-10-12")] // Sunday -> Monday of that week
    [InlineData(BucketSize.Week, "2026-10-12", "2026-10-12")] // Monday stays
    [InlineData(BucketSize.Month, "2026-10-18", "2026-10-01")]
    [InlineData(BucketSize.Day, "2026-10-18", "2026-10-18")]
    public void BucketStart_truncates(BucketSize bucket, string day, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), ReportPeriods.BucketStart(DateOnly.Parse(day), bucket));

    [Fact]
    public void Buckets_cover_range_without_gaps()
    {
        var range = ReportPeriods.Create(new DateOnly(2026, 9, 18), new DateOnly(2026, 10, 18), BucketSize.Day, Toronto);
        var buckets = ReportPeriods.Buckets(range);

        Assert.Equal(30, buckets.Count);
        Assert.Equal(new DateOnly(2026, 9, 18), buckets[0]);
        Assert.Equal(new DateOnly(2026, 10, 17), buckets[^1]);
    }

    [Fact]
    public void Month_buckets_for_a_year()
    {
        var range = ReportPeriods.Create(new DateOnly(2025, 11, 1), new DateOnly(2026, 10, 19), BucketSize.Month, Toronto);
        Assert.Equal(12, ReportPeriods.Buckets(range).Count);
    }
}

public class AnalyticsServiceHelperTests
{
    [Fact]
    public void SumByBucket_groups_days_into_weeks_and_fills_zeros()
    {
        var range = ReportPeriods.Create(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 26), BucketSize.Week);
        var buckets = ReportPeriods.Buckets(range);
        var daily = new[]
        {
            (new DateTime(2026, 10, 5), 2m),
            (new DateTime(2026, 10, 11), 3m), // same week (Mon 5th - Sun 11th)
            (new DateTime(2026, 10, 20), 1m),
        };

        var result = AnalyticsService.SumByBucket(daily, buckets, BucketSize.Week);

        Assert.Equal(new[] { 5d, 0d, 1d }, result);
    }

    [Fact]
    public void WeightedAverage_weights_by_event_count()
    {
        var range = ReportPeriods.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), BucketSize.Month);
        var buckets = ReportPeriods.Buckets(range);
        var daily = new[]
        {
            new DailySumCount { Day = new DateTime(2026, 10, 2), Total = 60m, Count = 3 }, // 3 events at $20
            new DailySumCount { Day = new DateTime(2026, 10, 9), Total = 40m, Count = 1 }, // 1 event at $40
        };

        var result = AnalyticsService.WeightedAverageByBucket(daily, buckets, BucketSize.Month);

        Assert.Equal(new[] { 25d }, result); // (60 + 40) / 4, not the average of daily averages (30)
    }

    [Fact]
    public void Cumulative_starts_from_existing_total() =>
        Assert.Equal(new[] { 12d, 12d, 15d }, AnalyticsService.Cumulative(new[] { 2d, 0d, 3d }, 10));

    [Fact]
    public void WeekdayMatrix_orders_cities_by_volume()
    {
        var rows = new[]
        {
            new WeekdayCityCount { CityName = "Gatineau", IsoWeekday = 1, Events = 1 },
            new WeekdayCityCount { CityName = "Ottawa", IsoWeekday = 6, Events = 4 },
            new WeekdayCityCount { CityName = "Ottawa", IsoWeekday = 7, Events = 2 },
        };

        var matrix = AnalyticsService.BuildWeekdayMatrix(rows);

        Assert.Equal(new[] { "Ottawa", "Gatineau" }, matrix.Cities);
        Assert.Equal(4, matrix.Events[0, 5]);
        Assert.Equal(2, matrix.Events[0, 6]);
        Assert.Equal(1, matrix.Events[1, 0]);
        Assert.Equal(4, matrix.MaxCell);
    }

    [Fact]
    public void Labels_are_thinned_for_long_series()
    {
        var range = ReportPeriods.Create(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), BucketSize.Day);
        var labels = AnalyticsService.Labels(ReportPeriods.Buckets(range), BucketSize.Day);

        Assert.Equal(30, labels.Count);
        Assert.True(labels.Count(l => l.Length > 0) <= 12);
        Assert.Equal("Sep 1", labels[0]);
    }
}

public class AuthTests
{
    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static AdminAllowList AllowList(string emails) =>
        new(new StaticMonitor<AdminAccessOptions>(new AdminAccessOptions { AllowedEmails = emails }));

    [Fact]
    public void Empty_allow_list_denies_everyone()
    {
        var list = AllowList("");
        Assert.False(list.IsConfigured);
        Assert.False(list.IsAllowed("anyone@hobio.ca"));
    }

    [Theory]
    [InlineData("vardan@hobio.ca", true)]
    [InlineData("VARDAN@Hobio.ca", true)]
    [InlineData(" artur@hobio.ca ", true)]
    [InlineData("someone@hobio.ca", false)]
    [InlineData("vardan@hobio.ca.evil.com", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Allow_list_matches_exact_emails_case_insensitively(string? email, bool expected) =>
        Assert.Equal(expected, AllowList("vardan@hobio.ca, Artur@Hobio.ca;").IsAllowed(email));

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/cities?period=1y", "/cities?period=1y")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/login", "/")]
    [InlineData("/auth/logout", "/")]
    public void Return_url_must_be_local(string? input, string expected) =>
        Assert.Equal(expected, AuthEndpoints.SafeReturnUrl(input));
}

public class ConfigurationTests
{
    [Fact]
    public void Json_secret_is_flattened_with_double_underscore_as_section_separator()
    {
        var pairs = AwsSecretsConfiguration.Flatten("hobio-admin/dev", """
            { "HobioAdmin__ReadOnlyConnectionString": "Server=x", "FirebaseAdminSdk": "{\"project_id\":\"p\"}", "Port": 587 }
            """).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("Server=x", pairs["HobioAdmin:ReadOnlyConnectionString"]);
        Assert.Equal("{\"project_id\":\"p\"}", pairs["FirebaseAdminSdk"]);
        Assert.Equal("587", pairs["Port"]);
    }

    [Fact]
    public void Plain_text_secret_uses_its_name()
    {
        var pairs = AwsSecretsConfiguration.Flatten("ConnectionString", "Server=y").ToList();
        Assert.Equal("ConnectionString", Assert.Single(pairs).Key);
    }

    [Fact]
    public void Secret_id_list_is_trimmed_and_deduplicated() =>
        Assert.Equal(new[] { "a", "b" }, AwsSecretsConfiguration.ParseList(" a, b ;a "));
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 1)]
    [InlineData(37, 10)]
    [InlineData(480, 100)]
    [InlineData(1234, 500)]
    public void NiceTickStep_gives_about_five_lines(double max, int expected) =>
        Assert.Equal(expected, Fmt.NiceTickStep(max));

    [Fact]
    public void Change_handles_zero_base()
    {
        Assert.Null(Fmt.Change(5, 0));
        Assert.Equal(0d, Fmt.Change(0, 0));
        Assert.Equal(0.5, Fmt.Change(15, 10));
        Assert.Equal("new", Fmt.ChangeText(null, previousWasZero: true));
        Assert.Equal("+50.0%", Fmt.ChangeText(0.5, previousWasZero: false));
    }
}

public class SqlScriptTests
{
    [Fact]
    public void Views_script_splits_into_batches()
    {
        var batches = SqlScripts.SplitBatches(SqlScripts.Load(SqlScripts.ViewsScript));
        Assert.True(batches.Count >= 8);
        Assert.All(batches, b => Assert.DoesNotContain("\nGO\n", b));
    }

    [Fact]
    public void Reader_script_has_master_and_database_parts_and_no_unresolved_variables()
    {
        var script = SqlScripts.Substitute(SqlScripts.Load(SqlScripts.ReaderScript), new Dictionary<string, string>
        {
            ["ReaderLogin"] = "hobio_admin_reader",
            ["ReaderPassword"] = "x",
            ["DatabaseName"] = "Tushpa",
        });

        var (master, database) = SqlScripts.SplitParts(script);
        Assert.Contains("CREATE LOGIN", master);
        Assert.DoesNotContain("CREATE USER", master);
        Assert.Contains("GRANT SELECT ON SCHEMA::[analytics]", database);
    }

    [Fact]
    public void Unresolved_variable_throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            SqlScripts.Substitute("SELECT '$(Missing)'", new Dictionary<string, string>()));
}

public class HttpsRedirectTests
{
    [Theory]
    [InlineData("http", "admin.hobio.ca", "/", "", "https://admin.hobio.ca/")]
    [InlineData("http", "admin.hobio.ca:80", "/retention", "?p=6m", "https://admin.hobio.ca/retention?p=6m")]
    [InlineData("HTTP, http", "admin.hobio.ca", "/login", "", "https://admin.hobio.ca/login")]
    public void Redirects_plain_http_from_the_load_balancer(string proto, string host, string path, string query, string expected) =>
        Assert.Equal(expected, HobioAdmin.Web.Hosting.HttpsRedirect.GetRedirectUrl(proto, host, "", path, query));

    [Theory]
    [InlineData("https", "admin.hobio.ca")]   // already secure
    [InlineData("", "10.0.1.5")]              // load balancer health check: no forwarded header
    [InlineData(null, "admin.hobio.ca")]
    [InlineData("http", "")]
    public void Leaves_other_requests_alone(string? proto, string host) =>
        Assert.Null(HobioAdmin.Web.Hosting.HttpsRedirect.GetRedirectUrl(proto, host, "", "/healthz", ""));
}
