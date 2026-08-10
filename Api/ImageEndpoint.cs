using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Api;

public static class ImageEndpoints
{
    private static readonly HashSet<string> AllowedContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/images");

        group.MapPost("/", HandleUpload).DisableAntiforgery();
        group.MapGet("/", HandleList);
        group.MapGet("/search", HandleSearch);
        group.MapPost("/{id:int}/retry", HandleRetry);
        group.MapPost("/{id:int}/regenerate", HandleRegenerate);
        group.MapPatch("/{id:int}", HandleEdit);
        group.MapDelete("/{id:int}", HandleDelete);

        return routes;
    }

    private static async Task<IResult> HandleUpload(
        [FromForm] UploadImageRequest request,
        ImageStorage storage,
        ThumbnailGenerator thumbnailGenerator,
        PerceptualHasher hasher,
        UrlImageFetcher urlFetcher,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var language = request.Language?.Trim();
        if (string.IsNullOrEmpty(language) || language.Length > 16)
        {
            return Results.BadRequest(
                new { error = "Language must be a non-empty BCP-47 tag under 16 characters. " }
            );
        }

        // Either File or Url must be provided
        var hasFile = request.File is { Length: > 0 };
        var hasUrl = !string.IsNullOrWhiteSpace(request.Url);

        if (hasFile == hasUrl)
            return Results.BadRequest(new { error = "Provide either a file or a URL." });

        Stream content;
        string contentType;
        string originalFileName;

        if (hasFile)
        {
            var f = request.File!;
            if (!AllowedContentTypes.Contains(f.ContentType))
                return Results.BadRequest(
                    new { error = $"Unsupported content type '{f.ContentType}'." }
                );
            content = f.OpenReadStream();
            contentType = f.ContentType;
            originalFileName = f.FileName;
        }
        else
        {
            try
            {
                var (stream, ct2) = await urlFetcher.FetchAsync(request.Url!, ct);
                content = stream;
                contentType = ct2;
                // use the last URL segment as the 'filename' - cheap and good enough
                originalFileName = Path.GetFileName(new Uri(request.Url!).LocalPath);
                if (string.IsNullOrWhiteSpace(originalFileName))
                    originalFileName = "downloaded";
            }
            catch (UrlFetchException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }

        string sha256;
        await using (content)
        {
            sha256 = await storage.StoreAsync(content, contentType, ct);
        }

        var sourcePath = storage.GetPath(sha256, contentType);
        await thumbnailGenerator.GenerateAsync(sha256, sourcePath, ct);

        // Exact SHA matching
        var existing = await db
            .Cartoons.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Sha256 == sha256, ct);
        if (existing is not null)
            return Results.Conflict(ToResponse(existing));

        // Perceptual hash matching. dHash tends to give ~15 bits distance for
        // heavily-downsampled duplicates and ~32 bits for unrelated images.
        // 18 catches shrinks/re-encodes with margin; crops need a different algorithm.
        var pHash = await hasher.HashAsync(sourcePath, ct);
        const int ThresholdBits = 18;
        var existingHashes = await db
            .Cartoons.Where(c => c.PerceptualHash != null)
            .Select(c => new
            {
                c.Id,
                c.Sha256,
                Hash = c.PerceptualHash!.Value,
            })
            .ToListAsync(ct);
        var nearDuplicates = existingHashes
            .Select(c => new
            {
                c.Id,
                c.Sha256,
                Distance = PerceptualHasher.HammingDistance(pHash, c.Hash),
            })
            .Where(x => x.Distance <= ThresholdBits)
            .OrderBy(x => x.Distance)
            .Select(x => new NearDuplicate(x.Id, $"/thumbnails/{x.Sha256}.webp", x.Distance))
            .ToList();

        var cartoon = new Cartoon
        {
            Sha256 = sha256,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            Language = language,
            PerceptualHash = pHash,
            Status = CartoonStatus.Pending,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        db.Cartoons.Add(cartoon);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/images/{cartoon.Id}",
            new UploadResponse(ToResponse(cartoon), nearDuplicates)
        );
    }

    private const int DefaultPageSize = 24;
    private const int MaxPageSize = 100;

    private static async Task<IResult> HandleList(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.Cartoons.OrderByDescending(c => c.Id); // SQLite cannot sort by DateTimeOffset. Id should be auto-incrementing anyways
        var total = await query.CountAsync(ct);

        var cartoons = await query
            .Skip((p - 1) * size)
            .Take(size)
            .AsNoTracking()
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = cartoons.Select(c => ToResponse(c)),
            page = p,
            pageSize = size,
            total,
        });
    }

    private static async Task<IResult> HandleSearch(
        [FromQuery] string? q,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
            return Results.Ok(Array.Empty<CartoonResponse>());

        var match = BuildFtsMatch(q);
        if (match.Length == 0)
            return Results.Ok(Array.Empty<CartoonResponse>());

        var cartoons = await db
            .Cartoons.FromSql(
                $"""
                SELECT c.*
                FROM Cartoons c
                JOIN CartoonsFts f ON f.rowid = c.Id
                WHERE CartoonsFts MATCH {match}
                ORDER BY rank
                LIMIT 200
                """
            )
            .AsNoTracking()
            .ToListAsync(ct);

        // Second query for FTS5 snippet() output. Uses HTML-safe placeholder markers
        // so we can HTML-escape the whole snippet server-side, then upgrade the
        // markers to real <mark> tags — descriptions can't inject HTML this way.
        var snippets = await db
            .Database.SqlQuery<SnippetRow>(
                $"""
                SELECT c.Id, snippet(CartoonsFts, 0, '%%OPEN%%', '%%CLOSE%%', '…', 15) AS Snippet
                FROM Cartoons c
                JOIN CartoonsFts f ON f.rowid = c.Id
                WHERE CartoonsFts MATCH {match}
                """
            )
            .ToDictionaryAsync(x => x.Id, x => SafeSnippet(x.Snippet), ct);

        return Results.Ok(cartoons.Select(c => ToResponse(c, snippets.GetValueOrDefault(c.Id))));
    }

    private sealed class SnippetRow
    {
        public int Id { get; set; }
        public string Snippet { get; set; } = "";
    }

    private static string SafeSnippet(string raw) =>
        System
            .Text.Encodings.Web.HtmlEncoder.Default.Encode(raw)
            .Replace("%%OPEN%%", "<mark>")
            .Replace("%%CLOSE%%", "</mark>");

    /// <summary>
    /// Turn user input into a safe FTS5 MATCH expression:
    ///     input: firavun robin
    ///     output: "firavun" * "robin" * (implicit AND; each token is a literal phrase)
    /// Quoting each token neutralizes FTS5 syntax chars (":", "()", operator words, etc.)
    /// The trailing `*` is FTS5's prefix operator - it makes 'firavun' also match Turkish declined and inflated words
    /// such as 'firavunun' 'firavuna' 'firavundan'.
    /// Note that it cannot handle stem changes and reverse search (finding stem from a declined word)
    /// </summary>
    private static string BuildFtsMatch(string q)
    {
        var tokens = q.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", "")) // double-quote is the only char we can't escape inside a phrase
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\" *");

        return string.Join(" ", tokens);
    }

    private static async Task<IResult> HandleDelete(
        int id,
        CartoonArchiveDbContext db,
        ImageStorage storage,
        ThumbnailGenerator thumbnails,
        CancellationToken ct
    )
    {
        var cartoon = await db.Cartoons.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cartoon is null)
            return Results.NotFound();

        var imagePath = storage.GetPath(cartoon.Sha256, cartoon.ContentType);
        var thumbPath = thumbnails.GetPath(cartoon.Sha256);

        db.Cartoons.Remove(cartoon);
        await db.SaveChangesAsync(ct);

        // Best-effort file cleanup after DB commit; DB is the source of truth.
        // Sha256 is unique per Cartoon (unique index), so no other row references these files.
        if (File.Exists(imagePath))
            File.Delete(imagePath);
        if (File.Exists(thumbPath))
            File.Delete(thumbPath);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleRetry(
        int id,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var cartoon = await db.Cartoons.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cartoon is null)
            return Results.NotFound();
        if (cartoon.Status != CartoonStatus.Failed)
            return Results.Conflict(new { error = "Only failed cartoons can be retried." });

        cartoon.Status = CartoonStatus.Pending;
        cartoon.ErrorMessage = null;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(cartoon));
    }

    private static async Task<IResult> HandleEdit(
        int id,
        [FromBody] EditRequest request,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var cartoon = await db.Cartoons.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cartoon is null)
            return Results.NotFound();

        var description = request.Description?.Trim();
        var language = request.Language?.Trim();

        if (description is null && language is null)
            return Results.BadRequest(
                new { error = "Provide description and/or language to update." }
            );

        if (language is not null)
        {
            if (language.Length == 0 || language.Length > 16)
                return Results.BadRequest(
                    new { error = "Language must be a non-empty tag under 16 characters." }
                );
            cartoon.Language = language;
        }

        if (description is not null)
        {
            if (description.Length == 0 || description.Length > 10_000)
                return Results.BadRequest(
                    new { error = "Description must be between 1 and 10,000 characters." }
                );
            cartoon.Description = description;
            // FTS trigger fires on Description UPDATE, so the index re-syncs automatically.
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(cartoon));
    }

    private static async Task<IResult> HandleRegenerate(
        int id,
        [FromBody] RegenerateRequest? request,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var cartoon = await db.Cartoons.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cartoon is null)
            return Results.NotFound();
        if (cartoon.Status != CartoonStatus.Ready)
            return Results.Conflict(
                new { error = "Only ready cartoons can be regenerated. Use retry for failed ones." }
            );

        var language = request?.Language?.Trim();
        if (!string.IsNullOrEmpty(language))
        {
            if (language.Length > 16)
                return Results.BadRequest(new { error = "Language must be under 16 characters." });
            cartoon.Language = language;
        }

        // Clear description so a failed regen doesn't leave stale text alongside Failed status.
        // FTS trigger deletes the old FTS row on Description UPDATE.
        cartoon.Description = null;
        cartoon.CaptionedAt = null;
        cartoon.ErrorMessage = null;
        cartoon.Status = CartoonStatus.Pending;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(cartoon));
    }

    private static CartoonResponse ToResponse(Cartoon c, string? snippet = null) =>
        new(
            Id: c.Id,
            Sha256: c.Sha256,
            OriginalFileName: c.OriginalFileName,
            Language: c.Language,
            Status: c.Status,
            UploadedAt: c.UploadedAt,
            ImageUrl: $"/images/{c.Sha256}{ImageStorage.ExtensionFor(c.ContentType)}",
            ThumbnailUrl: $"/thumbnails/{c.Sha256}.webp",
            Description: c.Description,
            ErrorMessage: c.ErrorMessage,
            Snippet: snippet
        );
}
