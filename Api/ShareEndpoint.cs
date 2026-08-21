using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Sharing;
using CartoonArchive.Storage;
using CartoonArchive.Tagging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CartoonArchive.Api;

public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/images/{id:int}/share", HandleCreate);
        routes.MapGet("/s/{token}", HandleView);
        routes.MapGet("/s/{token}/image", HandleImage);
        return routes;
    }

    private static async Task<IResult> HandleCreate(
        int id,
        CartoonArchiveDbContext db,
        HttpContext http,
        IOptions<ShareOptions> options,
        CancellationToken ct
    )
    {
        var cartoon = await db.Cartoons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cartoon is null)
            return Results.NotFound();
        if (cartoon.Status != CartoonStatus.Ready)
            return Results.Conflict(new { error = "Only ready cartoons can be shared. " });

        var opts = options.Value;
        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow;
        var activeUntil = now.AddHours(opts.TokenTtlHours);
        var metadataUntil = activeUntil.AddHours(opts.MetadataTtlHours);

        db.ShareTokens.Add(
            new ShareToken
            {
                Token = token,
                CartoonId = cartoon.Id,
                CreatedAt = now,
                ActiveUntil = activeUntil,
                MetadataUntil = metadataUntil,
            }
        );
        await db.SaveChangesAsync(ct);

        var baseUrl =
            opts.PublicBaseUrl?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var url = $"{baseUrl}/s/{token}";

        return Results.Created(
            url,
            new
            {
                token,
                expiresAt = activeUntil,
                url,
            }
        );
    }

    private static async Task<IResult> HandleView(
        string token,
        CartoonArchiveDbContext db,
        HttpContext http,
        IOptions<ShareOptions> options,
        CancellationToken ct
    )
    {
        var share = await db
            .ShareTokens.Include(s => s.Cartoon)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct);

        // Unknown token indistinguishable from swept — same 410 either way.
        if (share is null)
            return HtmlContent(BuildGoneHtml(), StatusCodes.Status410Gone);

        var now = DateTimeOffset.UtcNow;
        if (now >= share.MetadataUntil)
            return HtmlContent(BuildGoneHtml(), StatusCodes.Status410Gone);

        NoStore(http);

        if (now < share.ActiveUntil)
        {
            var baseUrl =
                options.Value.PublicBaseUrl?.TrimEnd('/')
                ?? $"{http.Request.Scheme}://{http.Request.Host}";
            return HtmlContent(BuildActiveHtml(share, baseUrl), StatusCodes.Status200OK);
        }

        // Active window over, but row still alive → placeholder with metadata.
        var tagsMap = await TagLoader.LoadAsync(db, new[] { share.CartoonId }, ct);
        var tags = tagsMap.GetValueOrDefault(share.CartoonId) ?? (IReadOnlyList<string>)Array.Empty<string>();
        return HtmlContent(BuildExpiredHtml(share, tags), StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleImage(
        string token,
        CartoonArchiveDbContext db,
        ThumbnailGenerator thumbnails,
        ImageStorage storage,
        HttpContext http,
        CancellationToken ct
    )
    {
        var share = await db
            .ShareTokens.Include(s => s.Cartoon)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct);

        // Sub-route must enforce ActiveUntil independently. Serving bytes past
        // the active window would defeat the redaction the HTML page performs.
        if (share is null || DateTimeOffset.UtcNow >= share.ActiveUntil)
            return Results.StatusCode(StatusCodes.Status410Gone);

        var thumbPath = thumbnails.GetPath(share.Cartoon.Sha256);
        string filePath;
        string contentType;
        string ext;
        if (File.Exists(thumbPath))
        {
            filePath = thumbPath;
            contentType = "image/webp";
            ext = ".webp";
        }
        else
        {
            filePath = storage.GetPath(share.Cartoon.Sha256, share.Cartoon.ContentType);
            if (!File.Exists(filePath))
                return Results.StatusCode(StatusCodes.Status410Gone);
            contentType = share.Cartoon.ContentType;
            ext = ImageStorage.ExtensionFor(share.Cartoon.ContentType);
        }

        NoStore(http);
        // Generic filename if the user saves — do not leak SHA256.
        http.Response.Headers.ContentDisposition = $"inline; filename=\"a{ext}\"";
        return Results.File(filePath, contentType);
    }

    private static string GenerateToken()
    {
        // 7 bytes = 56 bits; base64url without padding = 10 chars.
        var bytes = RandomNumberGenerator.GetBytes(7);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static IResult HtmlContent(string html, int status) =>
        Results.Content(html, "text/html", Encoding.UTF8, status);

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string BuildActiveHtml(ShareToken share, string baseUrl)
    {
        var caption = share.Cartoon.Description ?? "";
        var imageUrl = $"{baseUrl}/s/{share.Token}/image";
        var ogTitle = HtmlEncoder.Default.Encode(Truncate(caption, 80));
        var ogDesc = HtmlEncoder.Default.Encode(Truncate(caption, 200));
        var encodedImageUrl = HtmlEncoder.Default.Encode(imageUrl);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Shared cartoon</title>
                <meta property="og:type" content="website">
                <meta property="og:title" content="{{ogTitle}}">
                <meta property="og:description" content="{{ogDesc}}">
                <meta property="og:image" content="{{encodedImageUrl}}">
                <meta name="twitter:card" content="summary_large_image">
                <meta name="twitter:image" content="{{encodedImageUrl}}">
                <style>
                    html, body { margin: 0; padding: 0; height: 100%; background: #111; }
                    body { display: flex; align-items: center; justify-content: center;
                           padding: 1rem; box-sizing: border-box; }
                    img { max-width: 100%; max-height: 100vh; height: auto; width: auto;
                          display: block; border-radius: 4px;
                          box-shadow: 0 4px 24px rgba(0,0,0,0.6); }
                </style>
            </head>
            <body>
                <img src="{{encodedImageUrl}}" alt="">
            </body>
            </html>
            """;
    }

    private static string BuildExpiredHtml(ShareToken share, IReadOnlyList<string> tags)
    {
        var caption = share.Cartoon.Description ?? "";
        var ogTitle = HtmlEncoder.Default.Encode(Truncate(caption, 80));
        var ogDesc = HtmlEncoder.Default.Encode(Truncate(caption, 200));

        var captionBlock = string.IsNullOrWhiteSpace(caption)
            ? ""
            : $"<p class=\"caption\">{HtmlEncoder.Default.Encode(caption)}</p>";

        var tagsBlock = tags.Count == 0
            ? ""
            : "<p class=\"tags\">" + string.Join(
                " ",
                tags.Select(t =>
                    $"<span class=\"tag\">{HtmlEncoder.Default.Encode(t)}</span>"
                )
            ) + "</p>";

        var sharedAt = share.CreatedAt.ToString("yyyy-MM-dd");
        var expiredAt = share.ActiveUntil.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var metadataGoneAt = share.MetadataUntil.ToString("yyyy-MM-dd");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Link expired</title>
                <meta property="og:type" content="website">
                <meta property="og:title" content="{{ogTitle}}">
                <meta property="og:description" content="{{ogDesc}}">
                <style>
                    body { font-family: system-ui, -apple-system, sans-serif;
                           max-width: 560px; margin: 4em auto; padding: 0 1em;
                           color: #333; background: #fff; line-height: 1.55; }
                    h1 { font-size: 1.4em; margin-bottom: 0.5em; }
                    .caption { font-style: italic; color: #444; margin: 1em 0; }
                    .tags { margin: 1em 0; }
                    .tag { display: inline-block; background: #eee; color: #333;
                           padding: 0.15em 0.55em; margin-right: 0.35em;
                           border-radius: 0.75em; font-size: 0.9em; }
                    .meta { color: #777; font-size: 0.9em; margin-top: 1.5em; }
                    @media (prefers-color-scheme: dark) {
                        body { color: #e0e0e0; background: #1a1a1a; }
                        .caption { color: #bbb; }
                        .tag { background: #333; color: #ddd; }
                        .meta { color: #888; }
                    }
                </style>
            </head>
            <body>
                <h1>Link expired</h1>
                <p>This shared cartoon is no longer viewable. The image has been redacted; only the metadata below survives.</p>
                {{captionBlock}}
                {{tagsBlock}}
                <p class="meta">
                    Shared on {{sharedAt}} · expired {{expiredAt}} · this info disappears on {{metadataGoneAt}}.
                </p>
            </body>
            </html>
            """;
    }

    private static string BuildGoneHtml() =>
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Not available</title>
            <style>
                body { font-family: system-ui, -apple-system, sans-serif;
                       max-width: 480px; margin: 4em auto; padding: 0 1em;
                       color: #333; background: #fff; line-height: 1.55; }
                h1 { font-size: 1.4em; margin-bottom: 0.5em; }
                @media (prefers-color-scheme: dark) {
                    body { color: #e0e0e0; background: #1a1a1a; }
                }
            </style>
        </head>
        <body>
            <h1>Not available</h1>
            <p>This share is no longer available.</p>
        </body>
        </html>
        """;
}
