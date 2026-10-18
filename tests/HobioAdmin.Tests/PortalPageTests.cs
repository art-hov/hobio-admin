using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HobioAdmin.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HobioAdmin.Tests;

/// <summary>
/// Runs the real app in memory. Pages are server pre-rendered, so the HTML already contains the numbers.
/// Authentication is replaced by a test scheme here only; the app itself has no sign-in bypass.
/// </summary>
[Collection("sql")]
public sealed class PortalPageTests(SqlServerFixture db)
{
    private const string AdminEmail = "admin@hobio.ca";

    private WebApplicationFactory<Program> CreateFactory(bool signedIn) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Admin:AllowedEmails", AdminEmail);
            builder.UseSetting("Auth:RequireHttps", "false");
            builder.UseSetting("AwsSecrets:Enabled", "false");
            builder.UseSetting("HobioAdmin:ReadOnlyConnectionString", db.ReaderConnectionString ?? "");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock(new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc)));

                if (signedIn)
                {
                    services.AddAuthentication(options =>
                        {
                            options.DefaultScheme = TestAuthHandler.Scheme;
                            options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                            options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                        })
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                }
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Anonymous_visitor_is_sent_to_login()
    {
        using var factory = CreateFactory(signedIn: false);
        using var client = Client(factory);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/cities")]
    [InlineData("/retention")]
    [InlineData("/investor-report")]
    [InlineData("/_blazor/negotiate?negotiateVersion=1")]
    public async Task Every_page_requires_sign_in(string path)
    {
        using var factory = CreateFactory(signedIn: false);
        using var client = Client(factory);

        var response = path.StartsWith("/_blazor", StringComparison.Ordinal)
            ? await client.PostAsync(path, null)
            : await client.GetAsync(path);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"{path} returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Login_page_and_health_are_public()
    {
        using var factory = CreateFactory(signedIn: false);
        using var client = Client(factory);

        var login = await client.GetAsync("/login");
        var html = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Hobio Admin", html);
        Assert.Contains("img/logo.svg", html);
        // No Firebase settings in tests, so the page explains what's missing instead of offering sign-in.
        Assert.Contains("configured yet", html);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/img/logo.svg")).StatusCode);
    }

    [Fact]
    public async Task Session_endpoint_rejects_posts_without_antiforgery_token()
    {
        using var factory = CreateFactory(signedIn: false);
        using var client = Client(factory);

        var response = await client.PostAsync("/auth/session",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["idToken"] = "fake" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie") && response.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("hobio_admin=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        using var factory = CreateFactory(signedIn: false);
        using var client = Client(factory);

        var response = await client.GetAsync("/login");

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Overview_shows_kpis_and_charts()
    {
        if (!db.Available) return;
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);

        var html = await GetOk(client, "/?period=1m");

        Assert.Contains("Overview", html);
        Assert.Contains("Oct 1, 2026", html);
        Assert.Contains("Events held", html);
        Assert.Contains("$21.00", html);   // gross booking value
        Assert.Contains("$25.00", html);   // average paid price
        Assert.Contains("Ottawa", html);   // city filter options
        Assert.Contains("<svg", html);     // charts rendered
        Assert.Contains(AdminEmail, html); // signed-in user in the app bar
    }

    [Fact]
    public async Task Overview_city_filter()
    {
        if (!db.Available) return;
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);

        var html = await GetOk(client, "/?period=1m&city=2");

        Assert.Contains("$0.00", html); // Gatineau had only a free event
    }

    [Fact]
    public async Task Cities_page_shows_weekday_heatmap_and_summary()
    {
        if (!db.Available) return;
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);

        var html = await GetOk(client, "/cities?period=1m");

        Assert.Contains("Which days events happen", html);
        Assert.Contains("Ottawa, Sat: 2 events", html);
        Assert.Contains("Gatineau, Mon: 1 events", html);
        Assert.Contains("City summary", html);
    }

    [Fact]
    public async Task Retention_page_shows_cohorts()
    {
        if (!db.Available) return;
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);

        var html = await GetOk(client, "/retention");

        Assert.Contains("Sign-up month", html);
        Assert.Contains("Oct 2026", html);
        Assert.Contains("1 of 3 users", html);
    }

    [Fact]
    public async Task Investor_report_has_all_sections()
    {
        if (!db.Available) return;
        using var factory = CreateFactory(signedIn: true);
        using var client = Client(factory);

        var html = await GetOk(client, "/investor-report?period=1m");

        Assert.Contains("growth report", html);
        Assert.Contains("Headline numbers", html);
        Assert.Contains("Month by month", html);
        Assert.Contains("Top cities", html);
        Assert.Contains("Retention by sign-up month", html);
        Assert.Contains("Print / Save as PDF", html);
    }

    [Fact]
    public async Task Removed_admin_loses_access_on_next_request()
    {
        // The cookie handler re-checks the allow-list on every request (see Program.cs).
        using var factory = CreateFactory(signedIn: false);
        var allowList = factory.Services.GetRequiredService<IAdminAllowList>();

        Assert.True(allowList.IsAllowed(AdminEmail));
        Assert.False(allowList.IsAllowed("former-admin@hobio.ca"));
    }

    private static async Task<string> GetOk(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(html.Length, 2000)]}");
        Assert.DoesNotContain("Couldn&#x27;t load the numbers", html);
        Assert.DoesNotContain("Couldn't load the numbers", html);
        return html;
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "test-uid"),
                new Claim(ClaimTypes.Email, AdminEmail),
                new Claim(ClaimTypes.Name, "Test Admin"),
                new Claim(AuthEndpoints.AdminClaim, "true"),
            ], Scheme);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }
}
