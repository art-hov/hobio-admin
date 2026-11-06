using HobioAdmin.DbTool;
using HobioAdmin.Web.Analytics;
using HobioAdmin.Web.Auth;
using HobioAdmin.Web.Components.Shared;
using HobioAdmin.Web.Configuration;
using HobioAdmin.Web.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HobioAdmin.Tests;

public class ReportPeriodTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    [Theory]
    [InlineData("1w", ReportPeriod.OneWeek)]
    [InlineData("7d", ReportPeriod.OneWeek)]
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
    public void One_week_is_the_last_seven_toronto_days_including_today()
    {
        // 2026-11-06 03:00 UTC is still Thursday 2026-11-05 22:00 in Toronto (EST, UTC-5).
        var range = ReportPeriods.Resolve(ReportPeriod.OneWeek, new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc), Toronto);

        Assert.Equal(new DateOnly(2026, 10, 30), range.From);
        Assert.Equal(new DateOnly(2026, 11, 6), range.To);
        Assert.Equal(7, range.To.DayNumber - range.From.DayNumber);
        Assert.Equal(BucketSize.Day, range.Bucket);
        Assert.Equal(7, ReportPeriods.Buckets(range).Count);
        // Spans the Nov 1 DST change: Oct 30 midnight is EDT (UTC-4), Nov 6 midnight is EST (UTC-5).
        Assert.Equal(new DateTime(2026, 10, 30, 4, 0, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 6, 5, 0, 0, DateTimeKind.Utc), range.ToUtc);
    }

    [Fact]
    public void One_week_has_key_label_and_comes_first_in_the_picker()
    {
        Assert.Equal("1w", ReportPeriods.Key(ReportPeriod.OneWeek));
        Assert.Equal("1 week", ReportPeriods.Label(ReportPeriod.OneWeek));
        Assert.Equal(ReportPeriod.OneWeek, ReportPeriods.All[0]);
        Assert.Equal(ReportPeriod.OneWeek, ReportPeriods.Parse(ReportPeriods.Key(ReportPeriod.OneWeek)));
        // Existing keys and labels are unchanged.
        Assert.Equal("1m", ReportPeriods.Key(ReportPeriod.OneMonth));
        Assert.Equal("1 month", ReportPeriods.Label(ReportPeriod.OneMonth));
        Assert.Equal("1y", ReportPeriods.Key(ReportPeriod.OneYear));
    }

    [Fact]
    public void Previous_week_is_the_seven_days_before()
    {
        var range = ReportPeriods.Resolve(ReportPeriod.OneWeek, new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc), Toronto);
        var previous = ReportPeriods.Previous(range, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 18), previous.From);
        Assert.Equal(range.From, previous.To);
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
    public void Nested_email_credentials_bind_to_email_options()
    {
        var pairs = AwsSecretsConfiguration.Flatten("hobio-admin/prod", """
            { "EmailCredentials": { "Host": "smtp.example.com", "Port": 587, "Email": "support@hobio.ca", "Password": "pw" },
              "FirebaseAdminSdk": { "project_id": "p" } }
            """).ToList();

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        var options = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>()!;

        Assert.Equal("smtp.example.com", options.Host);
        Assert.Equal(587, options.Port);
        Assert.Equal("support@hobio.ca", options.Email);
        Assert.Equal("pw", options.Password);
        Assert.True(options.IsConfigured);

        // A nested object still keeps its raw JSON under its own key, as before.
        Assert.Equal("{ \"project_id\": \"p\" }", pairs.Single(p => p.Key == "FirebaseAdminSdk").Value);
    }

    [Fact]
    public void Email_options_need_host_email_and_password()
    {
        Assert.False(new EmailOptions().IsConfigured);
        Assert.False(new EmailOptions { Host = "h", Email = "e@x.ca" }.IsConfigured);
        Assert.True(new EmailOptions { Host = "h", Email = "e@x.ca", Password = "p" }.IsConfigured);
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

public class UserSignupRangeTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    // Thursday 2026-11-05 22:00 in Toronto.
    private static readonly DateTime Now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Defaults_to_the_last_seven_days_including_today()
    {
        var range = UserSignupRange.Resolve(null, null, Now, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 30), range.From);
        Assert.Equal(new DateOnly(2026, 11, 6), range.To); // exclusive: Nov 5 is the last day shown
        Assert.Equal(7, range.To.DayNumber - range.From.DayNumber);
    }

    [Fact]
    public void To_date_is_inclusive_and_converted_with_toronto_midnights()
    {
        var range = UserSignupRange.Resolve(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Now, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 1), range.From);
        Assert.Equal(new DateOnly(2026, 11, 1), range.To);
        Assert.Equal(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), range.ToUtc);
    }

    [Fact]
    public void Single_day_range_is_allowed()
    {
        var day = new DateOnly(2026, 10, 20);
        var range = UserSignupRange.Resolve(day, day, Now, Toronto);

        Assert.Equal(day, range.From);
        Assert.Equal(day.AddDays(1), range.To);
    }

    [Fact]
    public void Reversed_range_is_swapped()
    {
        var range = UserSignupRange.Resolve(new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1), Now, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 1), range.From);
        Assert.Equal(new DateOnly(2026, 11, 1), range.To);
    }

    [Fact]
    public void Only_from_given_runs_to_today()
    {
        var range = UserSignupRange.Resolve(new DateOnly(2026, 10, 1), null, Now, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 1), range.From);
        Assert.Equal(new DateOnly(2026, 11, 6), range.To);
    }

    [Fact]
    public void Only_to_given_shows_the_seven_days_ending_then()
    {
        var range = UserSignupRange.Resolve(null, new DateOnly(2026, 10, 10), Now, Toronto);

        Assert.Equal(new DateOnly(2026, 10, 4), range.From);
        Assert.Equal(new DateOnly(2026, 10, 11), range.To);
    }

    [Theory]
    [InlineData(7, "2026-10-30")]
    [InlineData(30, "2026-10-07")]
    public void Last_days_quick_ranges_end_today(int days, string expectedFrom)
    {
        var (from, to) = UserSignupRange.LastDays(days, Now, Toronto);

        Assert.Equal(DateOnly.Parse(expectedFrom), from);
        Assert.Equal(new DateOnly(2026, 11, 5), to);
    }

    [Fact]
    public void Page_count_rounds_up_and_is_at_least_one()
    {
        var range = UserSignupRange.Resolve(null, null, Now, Toronto);

        Assert.Equal(1, new UsersData(range, 1, 50, new UserSignupPage(0, 0, [])).PageCount);
        Assert.Equal(1, new UsersData(range, 1, 50, new UserSignupPage(50, 0, [])).PageCount);
        Assert.Equal(2, new UsersData(range, 1, 50, new UserSignupPage(51, 0, [])).PageCount);
    }
}

public class ReportReplyServiceTests
{
    private static IssueReport Report(string? email = "p1@example.com", bool deleted = false) => new()
    {
        ReportId = 7,
        CreatedAtUtc = new DateTime(2026, 10, 5, 12, 0, 0),
        Title = "App crash",
        Description = "The app closed when I opened my bookings.",
        ReporterFullName = deleted ? null : "Pat One",
        ReporterIsDeleted = deleted,
        ReporterEmail = email,
    };

    private static (ReportReplyService Service, FakeSender Sender) Create(IssueReport? report, bool configured = true, Exception? failWith = null)
    {
        var sender = new FakeSender(configured, failWith);
        return (new ReportReplyService(new FakeRepository(report), sender, NullLogger<ReportReplyService>.Instance), sender);
    }

    [Fact]
    public async Task Sends_to_the_reporter_email_from_the_database_with_bcc_to_the_sender()
    {
        var (service, sender) = Create(Report());

        var result = await service.SendAsync(7, null, "  Thanks, we fixed it.  ", "admin@hobio.ca");

        Assert.True(result.Sent);
        var email = Assert.Single(sender.Sent);
        Assert.Equal("p1@example.com", email.To);
        Assert.Equal("support@hobio.ca", email.Bcc);
        Assert.Equal("Re: App crash", email.Subject);
        Assert.StartsWith("Hi Pat,", email.Body);
        Assert.Contains("Thanks, we fixed it.", email.Body);
        Assert.Contains("The app closed when I opened my bookings.", email.Body); // original report quoted
        Assert.Contains("Oct 5, 2026 8:00 AM", email.Body);                     // Toronto time
    }

    [Fact]
    public async Task Subject_cannot_contain_line_breaks()
    {
        var (service, sender) = Create(Report());

        await service.SendAsync(7, "Hello\r\nBcc: attacker@example.com", "Hi", "admin@hobio.ca");

        Assert.Equal("Hello Bcc: attacker@example.com", sender.Sent.Single().Subject);
    }

    [Fact]
    public async Task Refuses_when_email_is_not_configured()
    {
        var (service, sender) = Create(Report(), configured: false);

        var result = await service.SendAsync(7, null, "Hi", "admin@hobio.ca");

        Assert.False(result.Sent);
        Assert.Contains("isn't set up", result.Message);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_an_empty_message(string message)
    {
        var (service, sender) = Create(Report());

        Assert.False((await service.SendAsync(7, null, message, "admin@hobio.ca")).Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Refuses_an_overlong_message()
    {
        var (service, sender) = Create(Report());

        var result = await service.SendAsync(7, null, new string('x', ReportReplyService.MaxMessageLength + 1), "admin@hobio.ca");

        Assert.False(result.Sent);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not-an-email", false)]
    [InlineData("p1@example.com", true)] // deleted reporter
    public async Task Refuses_when_there_is_no_one_to_reply_to(string? email, bool deleted)
    {
        var (service, sender) = Create(Report(email, deleted));

        var result = await service.SendAsync(7, null, "Hi", "admin@hobio.ca");

        Assert.False(result.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Refuses_a_missing_report()
    {
        var (service, sender) = Create(null);

        Assert.False((await service.SendAsync(99, null, "Hi", "admin@hobio.ca")).Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Smtp_failure_is_reported_not_thrown()
    {
        var (service, _) = Create(Report(), failWith: new System.Net.Mail.SmtpException("Authentication failed"));

        var result = await service.SendAsync(7, null, "Hi", "admin@hobio.ca");

        Assert.False(result.Sent);
        Assert.Contains("Authentication failed", result.Message);
    }

    private sealed class FakeSender(bool configured, Exception? failWith) : IEmailSender
    {
        public List<OutgoingEmail> Sent { get; } = [];
        public bool IsConfigured => configured;
        public string? FromAddress => "support@hobio.ca";

        public Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
        {
            if (failWith is not null)
            {
                throw failWith;
            }

            Sent.Add(email);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepository(IssueReport? report) : IAnalyticsRepository
    {
        public bool IsConfigured => true;
        public Task<IssueReport?> GetIssueReportAsync(long reportId, CancellationToken ct = default) =>
            Task.FromResult(report is not null && report.ReportId == reportId ? report : null);

        public Task<KpiSnapshot> GetKpisAsync(DateRange range, int? cityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyValue>> GetNewUsersDailyAsync(DateRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetUsersBeforeAsync(DateTime utc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyValue>> GetEventsHeldDailyAsync(DateRange range, int? cityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyValue>> GetBookingsDailyAsync(DateRange range, int? cityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailySumCount>> GetPaidPriceDailyAsync(DateRange range, int? cityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyRevenue>> GetRevenueDailyAsync(DateRange range, int? cityId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WeekdayCityCount>> GetWeekdayByCityAsync(DateRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CitySummary>> GetCitySummaryAsync(DateRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MonthlyActive>> GetMonthlyActiveAsync(DateRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CohortRow>> GetCohortsAsync(DateRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CityOption>> GetCitiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UserSignupPage> GetUserSignupsAsync(DateRange range, int offset, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IssueReportPage> GetIssueReportsAsync(int offset, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
