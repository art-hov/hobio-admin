using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace HobioAdmin.Web.Auth;

public sealed record VerifiedFirebaseUser(string Uid, string? Email, bool EmailVerified, string? Name, DateTimeOffset? AuthTime);

public interface IFirebaseTokenVerifier
{
    bool IsConfigured { get; }
    string? ProjectId { get; }
    Task<VerifiedFirebaseUser> VerifyAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies Firebase ID tokens on the server with the Firebase Admin SDK, using the same
/// service account the Tushpa API uses (config key "FirebaseAdminSdk" by default).
/// </summary>
public sealed class FirebaseTokenVerifier : IFirebaseTokenVerifier
{
    private const string AppName = "hobio-admin";
    private readonly Lazy<FirebaseAuth?> _auth;

    public FirebaseTokenVerifier(IConfiguration configuration, IOptions<FirebaseOptions> options, ILogger<FirebaseTokenVerifier> logger)
    {
        var serviceAccountJson = configuration[options.Value.AdminSdkConfigKey];
        ProjectId = options.Value.ProjectId ?? ReadProjectId(serviceAccountJson);

        _auth = new Lazy<FirebaseAuth?>(() =>
        {
            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                logger.LogError("Firebase Admin SDK credentials not found under configuration key {Key}.", options.Value.AdminSdkConfigKey);
                return null;
            }

            var app = FirebaseApp.GetInstance(AppName) ?? FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromJson(serviceAccountJson, JsonCredentialParameters.ServiceAccountCredentialType),
                ProjectId = ProjectId,
            }, AppName);

            return FirebaseAuth.GetAuth(app);
        });

        IsConfigured = !string.IsNullOrWhiteSpace(serviceAccountJson);
    }

    public bool IsConfigured { get; }

    public string? ProjectId { get; }

    public async Task<VerifiedFirebaseUser> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        var auth = _auth.Value ?? throw new InvalidOperationException("Firebase is not configured.");

        // checkRevoked: true also rejects tokens of disabled or signed-out users.
        var token = await auth.VerifyIdTokenAsync(idToken, checkRevoked: true, cancellationToken);

        string? email = token.Claims.TryGetValue("email", out var e) ? e?.ToString() : null;
        string? name = token.Claims.TryGetValue("name", out var n) ? n?.ToString() : null;
        bool emailVerified = token.Claims.TryGetValue("email_verified", out var v) && v is bool b && b;

        DateTimeOffset? authTime = null;
        if (token.Claims.TryGetValue("auth_time", out var at) && long.TryParse(at?.ToString(), out var seconds))
        {
            authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return new VerifiedFirebaseUser(token.Uid, email, emailVerified, name, authTime);
    }

    private static string? ReadProjectId(string? serviceAccountJson)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(serviceAccountJson);
            return doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
