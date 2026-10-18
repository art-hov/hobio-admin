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
using Microsoft.Playwright;

namespace HobioAdmin.Tests;

/// <summary>
/// Takes screenshots of every page in a real browser (Playwright + Kestrel) against the seeded test database.
/// Only runs when HOBIO_SCREENSHOTS_DIR is set (the "Screenshots" workflow). Uses a test-only sign-in.
/// </summary>
[Collection("sql")]
[Trait("Category", "Screenshots")]
public sealed class ScreenshotTests(SqlServerFixture db)
{
    [Fact]
    public async Task Capture_pages()
    {
        var outputDir = Environment.GetEnvironmentVariable("HOBIO_SCREENSHOTS_DIR");
        if (string.IsNullOrWhiteSpace(outputDir) || !db.Available)
        {
            return;
        }

        Directory.CreateDirectory(outputDir);

        await using var signedIn = CreateFactory(signedIn: true, port: 5181);
        signedIn.UseKestrel(5181);
        signedIn.StartServer();
        var baseUrl = signedIn.ClientOptions.BaseAddress.ToString().TrimEnd('/');

        await using var anonymous = CreateFactory(signedIn: false, port: 5182);
        anonymous.UseKestrel(5182);
        anonymous.StartServer();
        var anonymousUrl = anonymous.ClientOptions.BaseAddress.ToString().TrimEnd('/');

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var errors = new List<string>();
        foreach (var (name, width) in new[] { ("desktop", 1440), ("phone", 390) })
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = 900 },
                DeviceScaleFactor = 1,
            });
            var page = await context.NewPageAsync();
            page.Console += (_, msg) => { if (msg.Type == "error") errors.Add($"{name}: {msg.Text}"); };
            page.PageError += (_, err) => errors.Add($"{name}: {err}");

            foreach (var (file, path) in new[]
            {
                ("overview", "/?period=1m"),
                ("overview-1y", "/?period=1y"),
                ("cities", "/cities?period=1m"),
                ("retention", "/retention"),
                ("investor-report", "/investor-report?period=1m"),
            })
            {
                await page.GotoAsync(baseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await page.WaitForTimeoutAsync(1500); // let the interactive circuit connect and charts settle
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outputDir, $"{name}-{file}.png"), FullPage = true });
            }

            await page.GotoAsync(anonymousUrl + "/login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outputDir, $"{name}-login.png"), FullPage = true });

            await context.CloseAsync();
        }

        await File.WriteAllLinesAsync(Path.Combine(outputDir, "browser-errors.txt"), errors.DefaultIfEmpty("(none)"));
    }

    private WebApplicationFactory<HobioAdmin.Web.WebAppMarker> CreateFactory(bool signedIn, int port) =>
        new WebApplicationFactory<HobioAdmin.Web.WebAppMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseUrls($"http://127.0.0.1:{port}");
            builder.UseSetting("Admin:AllowedEmails", "admin@hobio.ca");
            builder.UseSetting("Auth:RequireHttps", "false");
            builder.UseSetting("HobioAdmin:ReadOnlyConnectionString", db.ReaderConnectionString ?? "");
            if (!signedIn)
            {
                // Show the real sign-in page (buttons) rather than the "not configured" notice.
                builder.UseSetting("Firebase:WebApiKey", "demo-key");
                builder.UseSetting("Firebase:ProjectId", "hobio-demo");
                builder.UseSetting("FirebaseAdminSdk", "{\"type\":\"service_account\",\"project_id\":\"hobio-demo\"}");
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock(new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc)));
                if (signedIn)
                {
                    services.AddAuthentication(o =>
                        {
                            o.DefaultScheme = ScreenshotAuth.SchemeName;
                            o.DefaultAuthenticateScheme = ScreenshotAuth.SchemeName;
                            o.DefaultChallengeScheme = ScreenshotAuth.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions, ScreenshotAuth>(ScreenshotAuth.SchemeName, _ => { });
                }
            });
        });

    private sealed class ScreenshotAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Screenshot";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "uid"),
                new Claim(ClaimTypes.Email, "admin@hobio.ca"),
                new Claim(ClaimTypes.Name, "Admin"),
                new Claim(AuthEndpoints.AdminClaim, "true"),
            ], SchemeName)), SchemeName)));
    }
}
