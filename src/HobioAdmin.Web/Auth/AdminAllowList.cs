using Microsoft.Extensions.Options;

namespace HobioAdmin.Web.Auth;

public interface IAdminAllowList
{
    bool IsAllowed(string? email);
    bool IsConfigured { get; }
}

/// <summary>
/// Email allow-list for the admin portal. Fails closed: with no emails configured, nobody can sign in.
/// Reads the current options on every call, so removing an email takes effect without a restart
/// (and existing sessions are rejected on their next request, see Program.cs).
/// </summary>
public sealed class AdminAllowList(IOptionsMonitor<AdminAccessOptions> options) : IAdminAllowList
{
    public bool IsConfigured => Parse(options.CurrentValue.AllowedEmails).Count > 0;

    public bool IsAllowed(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        return Parse(options.CurrentValue.AllowedEmails).Contains(Normalize(email));
    }

    internal static HashSet<string> Parse(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
