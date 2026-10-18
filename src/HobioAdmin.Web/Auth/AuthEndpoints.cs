using System.Security.Claims;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HobioAdmin.Web.Auth;

public static class AuthEndpoints
{
    public const string AdminClaim = "hobio_admin";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The login page signs in with Firebase in the browser, then posts the ID token here (normal form post,
        // protected by the antiforgery token). The server verifies the token and issues its own session cookie.
        app.MapPost("/auth/session", async (
            [FromForm] string idToken,
            [FromForm] string? returnUrl,
            HttpContext context,
            IFirebaseTokenVerifier verifier,
            IAdminAllowList allowList,
            IOptions<FirebaseOptions> firebaseOptions,
            IOptions<AuthOptions> authOptions,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("HobioAdmin.Auth");

            if (string.IsNullOrWhiteSpace(idToken))
            {
                return Results.Redirect("/login?error=missing_token");
            }

            if (!verifier.IsConfigured || !allowList.IsConfigured)
            {
                logger.LogError("Sign-in attempted but the portal is not configured (Firebase configured: {Firebase}, allow-list configured: {AllowList}).",
                    verifier.IsConfigured, allowList.IsConfigured);
                return Results.Redirect("/login?error=not_configured");
            }

            VerifiedFirebaseUser user;
            try
            {
                user = await verifier.VerifyAsync(idToken, context.RequestAborted);
            }
            catch (FirebaseAuthException ex)
            {
                logger.LogWarning("Rejected Firebase ID token: {Reason}", ex.AuthErrorCode);
                return Results.Redirect("/login?error=invalid_token");
            }

            var maxAge = TimeSpan.FromMinutes(firebaseOptions.Value.MaxSignInAgeMinutes);
            if (user.AuthTime is null || DateTimeOffset.UtcNow - user.AuthTime.Value > maxAge)
            {
                logger.LogWarning("Rejected stale Firebase sign-in for uid {Uid}.", user.Uid);
                return Results.Redirect("/login?error=stale_sign_in");
            }

            if (!user.EmailVerified || !allowList.IsAllowed(user.Email))
            {
                logger.LogWarning("Denied admin access for uid {Uid} (email verified: {Verified}).", user.Uid, user.EmailVerified);
                return Results.Redirect("/login?error=not_authorized");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Uid),
                new(ClaimTypes.Email, user.Email!),
                new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.Name) ? user.Email! : user.Name),
                new(AdminClaim, "true"),
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(authOptions.Value.SessionHours),
                AllowRefresh = false,
            });

            logger.LogInformation("Admin {Email} signed in.", user.Email);
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login?signedOut=1");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            || returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            || returnUrl.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return returnUrl;
    }
}
