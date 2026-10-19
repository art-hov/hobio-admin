namespace HobioAdmin.Web.Hosting;

/// <summary>
/// Sends browsers that reach the load balancer over plain HTTP (port 80) to the HTTPS address.
///
/// The app itself only listens on HTTP behind nginx and the load balancer, so ASP.NET's
/// UseHttpsRedirection can't work out an HTTPS port and silently does nothing. Instead we look at
/// the X-Forwarded-Proto header the load balancer adds. Requests without that header (the load
/// balancer's own health checks, nginx on the instance) are never redirected.
/// </summary>
public static class HttpsRedirect
{
    public static string? GetRedirectUrl(string? forwardedProto, string? host, string? pathBase, string? path, string? query)
    {
        if (string.IsNullOrWhiteSpace(forwardedProto) || string.IsNullOrWhiteSpace(host))
            return null;

        // With several proxies the header can be a list; the first entry is what the browser used.
        var original = forwardedProto.Split(',')[0].Trim();
        if (!original.Equals("http", StringComparison.OrdinalIgnoreCase))
            return null;

        // Drop an explicit :80 so we land on the default HTTPS port.
        var hostOnly = host.EndsWith(":80", StringComparison.Ordinal) ? host[..^3] : host;
        return $"https://{hostOnly}{pathBase}{path}{query}";
    }

    public static IApplicationBuilder UseLoadBalancerHttpsRedirect(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var target = GetRedirectUrl(
                request.Headers["X-Forwarded-Proto"].ToString(),
                request.Headers.Host.ToString(),
                request.PathBase.Value,
                request.Path.Value,
                request.QueryString.Value);

            if (target is null)
            {
                await next();
                return;
            }

            // 308 keeps the method, so a form post isn't turned into a GET.
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = target;
        });
}
