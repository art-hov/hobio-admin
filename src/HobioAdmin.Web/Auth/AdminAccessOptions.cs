namespace HobioAdmin.Web.Auth;

/// <summary>Section "Admin". Who may sign in to the portal.</summary>
public sealed class AdminAccessOptions
{
    public const string SectionName = "Admin";

    /// <summary>Comma separated email addresses, e.g. "vardan@hobio.ca,artur@hobio.ca".</summary>
    public string AllowedEmails { get; set; } = string.Empty;
}

/// <summary>Section "Firebase". Web (client) settings plus where the Admin SDK service account lives.</summary>
public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    /// <summary>The Firebase Web API key (public, safe to show in the browser).</summary>
    public string WebApiKey { get; set; } = string.Empty;

    /// <summary>Defaults to "{ProjectId}.firebaseapp.com".</summary>
    public string? AuthDomain { get; set; }

    /// <summary>Defaults to the project_id in the Admin SDK service account.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Configuration key that holds the Admin SDK service account JSON. Tushpa uses "FirebaseAdminSdk".
    /// </summary>
    public string AdminSdkConfigKey { get; set; } = "FirebaseAdminSdk";

    /// <summary>Reject ID tokens whose sign-in happened longer ago than this.</summary>
    public int MaxSignInAgeMinutes { get; set; } = 10;
}

/// <summary>Section "Auth".</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Send the session cookie only over HTTPS. Keep true in any deployed environment.</summary>
    public bool RequireHttps { get; set; } = true;

    public int SessionHours { get; set; } = 8;
}
