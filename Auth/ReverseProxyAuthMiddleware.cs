using Microsoft.Extensions.Options;

namespace CartoonArchive.Auth;

public sealed class ReverseProxyAuthMiddleware(
    RequestDelegate next,
    IOptions<AuthOptions> options,
    ILogger<ReverseProxyAuthMiddleware> logger
)
{
    /// <summary>
    /// Paths that must be reachable without auth even in ReverseProxy mode.
    /// Deliberately NOT including /images or /thumbnails - those hold user content
    /// and are only meant to be reached indirectly via /s/{token}.
    /// </summary>
    private static readonly string[] AllowlistedPrefixes =
    {
        "/s",
        "/css",
        "/js",
        "/lib",
        // Favicons and PWA install assets. Browsers fetch these
        // unauthenticated when deciding whether the site is installable,
        // and iOS/Android probe well-known filenames at the site root.
        // All are public static content, so no auth required.
        "/favicon.ico",
        "/favicon-96x96.png",
        "/favicon.svg",
        "/apple-touch-icon.png",
        "/manifest.json",
        "/sw.js",
        "/web-app-manifest-192x192.png",
        "/web-app-manifest-512x512.png",
    };

    private readonly AuthOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.Mode == AuthMode.None || IsAllowlisted(context.Request.Path))
        {
            await next(context);
            return;
        }

        var user = context.Request.Headers[_options.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(user))
        {
            logger.LogWarning(
                "Unauthenticated request to {Path} (missing header {Header})",
                context.Request.Path,
                _options.HeaderName
            );
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(
                $"Unauthorized: missing {_options.HeaderName} header."
            );
            return;
        }

        // Stash the user for the downstream code taht may want it
        context.Items["User"] = user;
        await next(context);
    }

    private static bool IsAllowlisted(PathString path)
    {
        foreach (var prefix in AllowlistedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
