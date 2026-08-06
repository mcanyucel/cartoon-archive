using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Sharing;
using CartoonArchive.Storage;
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
        var expiresAt = DateTimeOffset.UtcNow.AddHours(opts.TokenTtlHours);
        var hint = ExtractHint(cartoon.Description, opts.HintWordCount);

        db.ShareTokens.Add(
            new ShareToken
            {
                Token = token,
                CartoonId = cartoon.Id,
                ExpiresAt = expiresAt,
            }
        );
        await db.SaveChangesAsync(ct);

        var baseUrl =
            opts.PublicBaseUrl?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}";

        var url = string.IsNullOrEmpty(hint)
            ? $"{baseUrl}/s/{token}"
            : $"{baseUrl}/s/{token}?k={Uri.EscapeDataString(hint)}";

        return Results.Created(
            url,
            new
            {
                token,
                expiresAt,
                url,
            }
        );
    }

    private static async Task<IResult> HandleView(
        string token,
        [FromQuery(Name = "k")] string? hint,
        CartoonArchiveDbContext db,
        ImageStorage storage,
        HttpContext http,
        CancellationToken ct
    )
    {
        var share = await db
            .ShareTokens.Include(s => s.Cartoon)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct);

        if (share is null || share.ExpiresAt < DateTimeOffset.UtcNow)
            return ExpiredResponse(hint);

        var path = storage.GetPath(share.Cartoon.Sha256, share.Cartoon.ContentType);
        if (!File.Exists(path))
            return ExpiredResponse(hint);

        // Keep browsers from caching the image past token expiry
        http.Response.Headers.CacheControl = "no-store, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        // Generic filename if user saves - do not leak SHA256
        var ext = ImageStorage.ExtensionFor(share.Cartoon.ContentType);
        http.Response.Headers.ContentDisposition = $"inline; filename=\"a{ext}\"";

        return Results.File(path, share.Cartoon.ContentType);
    }

    private static IResult ExpiredResponse(string? hint) =>
        Results.Content(
            BuildExpiredHtml(hint),
            "text/html",
            Encoding.UTF8,
            StatusCodes.Status410Gone
        );

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ExtractHint(string? description, int wordCount)
    {
        if (string.IsNullOrWhiteSpace(description) || wordCount <= 0)
            return string.Empty;

        return string.Join(
            ' ',
            description
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Take(wordCount)
        );
    }

    private static string BuildExpiredHtml(string? hint)
    {
        var hintBlock = string.IsNullOrWhiteSpace(hint)
            ? ""
            : $"<p class=\"hint\">Hint: &ldquo;{HtmlEncoder.Default.Encode(hint)}&rdquo;</p>";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Link expired</title>
                <style>
                    body { font-family: system-ui, -apple-system, sans-serif;
                           max-width: 520px; margin: 4em auto; padding: 0 1em;
                           color: #333; background: #fff; line-height: 1.5; }
                    h1 { font-size: 1.4em; margin-bottom: 0.5em; }
                    .hint { font-style: italic; color: #666; margin-top: 1.5em; }
                    @media (prefers-color-scheme: dark) {
                        body { color: #e0e0e0; background: #1a1a1a; }
                        .hint { color: #999; }
                    }
                </style>
            </head>
            <body>
                <h1>Link expired</h1>
                <p>This shared cartoon is no longer available. Ask the sender to reshare.</p>
                {{hintBlock}}
            </body>
            </html>
            """;
    }
}
