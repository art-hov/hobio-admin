using System.Security.Claims;
using HobioAdmin.Web.Analytics;
using HobioAdmin.Web.Auth;
using HobioAdmin.Web.Components;
using HobioAdmin.Web.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Secrets (Firebase Admin SDK, read-only connection string, ...) come from AWS Secrets Manager in AWS.
builder.Configuration.AddHobioAwsSecrets();

builder.Services.Configure<AdminAccessOptions>(builder.Configuration.GetSection(AdminAccessOptions.SectionName));
builder.Services.Configure<FirebaseOptions>(builder.Configuration.GetSection(FirebaseOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton<IAdminAllowList, AdminAllowList>();
builder.Services.AddSingleton<IFirebaseTokenVerifier, FirebaseTokenVerifier>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAnalyticsRepository>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["HobioAdmin:ReadOnlyConnectionString"] ?? config.GetConnectionString("ReadOnly");
    return new AnalyticsRepository(connectionString);
});
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "hobio_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login?error=not_authorized";
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = context =>
        {
            // Re-check the allow-list on every request so removing an email revokes access immediately.
            var allowList = context.HttpContext.RequestServices.GetRequiredService<IAdminAllowList>();
            var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
            if (!allowList.IsAllowed(email))
            {
                context.RejectPrincipal();
            }

            return Task.CompletedTask;
        };
    });

// Settings that depend on configuration are applied once configuration is complete.
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((options, auth) =>
    {
        options.Cookie.SecurePolicy = auth.Value.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(auth.Value.SessionHours);
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AuthEndpoints.AdminClaim, "true")
        .Build());

// Elastic Beanstalk puts nginx (and optionally a load balancer) in front of the app.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    if (authOptions.RequireHttps)
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Firebase sign-in popups need this to be relaxed from the strictest setting.
    headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapAuthEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HobioAdmin.Startup");
var allowListConfigured = app.Services.GetRequiredService<IAdminAllowList>().IsConfigured;
var firebaseConfigured = app.Services.GetRequiredService<IFirebaseTokenVerifier>().IsConfigured;
var webApiKeyConfigured = !string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<FirebaseOptions>>().Value.WebApiKey);
var databaseConfigured = app.Services.GetRequiredService<IAnalyticsRepository>().IsConfigured;
startupLogger.LogInformation(
    "Hobio Admin starting. Allow-list configured: {AllowList}. Firebase Admin SDK: {Firebase}. Firebase web API key: {WebKey}. Read-only DB: {Db}.",
    allowListConfigured, firebaseConfigured, webApiKeyConfigured, databaseConfigured);

app.Run();

public partial class Program;

namespace HobioAdmin.Web
{
    /// <summary>Marker type for WebApplicationFactory in tests.</summary>
    public sealed class WebAppMarker;
}
