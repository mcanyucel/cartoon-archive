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

        return routes;
    }

    private static async Task<IResult> HandleUpload(
        [FromForm] UploadImageRequest request,
        ImageStorage storage,
        ThumbnailGenerator thumbnailGenerator,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        if (request.File.Length == 0)
            return Results.BadRequest(new { error = "File is empty." });
        if (!AllowedContentTypes.Contains(request.File.ContentType))
        {
            return Results.BadRequest(
                new { error = $"Unsopported content type '{request.File.ContentType}." }
            );
        }

        var language = request.Language?.Trim();
        if (string.IsNullOrEmpty(language) || language.Length > 16)
        {
            return Results.BadRequest(
                new { error = "Language must be a non-empty BCP-47 tag under 16 characters. " }
            );
        }

        string sha256;
        await using (var stream = request.File.OpenReadStream())
        {
            sha256 = await storage.StoreAsync(stream, request.File.ContentType, ct);
        }

        var sourcePath = storage.GetPath(sha256, request.File.ContentType);
        await thumbnailGenerator.GenerateAsync(sha256, sourcePath, ct);

        var existing = await db
            .Cartoons.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Sha256 == sha256, ct);

        if (existing is not null)
        {
            return Results.Conflict(ToResponse(existing));
        }

        var cartoon = new Cartoon
        {
            Sha256 = sha256,
            OriginalFileName = request.File.FileName,
            ContentType = request.File.ContentType,
            Language = language,
            Status = CartoonStatus.Pending,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        db.Cartoons.Add(cartoon);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/images/{cartoon.Id}", ToResponse(cartoon));
    }

    private static async Task<IResult> HandleList(CartoonArchiveDbContext db, CancellationToken ct)
    {
        var cartoons = await db
            .Cartoons.OrderByDescending(c => c.Id) // SQLite cannot sort by DateTimeOffset. Id should be auto-incrementing anyways
            .Take(200)
            .AsNoTracking()
            .ToListAsync(ct);

        return Results.Ok(cartoons.Select(ToResponse));
    }

    // Stub search: LIKE over description. Will be replaced by FTS5.
    private static async Task<IResult> HandleSearch(
        [FromQuery] string? q,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
            return Results.Ok(Array.Empty<CartoonResponse>());

        var pattern = $"%{q}%";
        var cartoons = await db
            .Cartoons.Where(c =>
                c.Description != null && EF.Functions.Like(c.Description, pattern)
            )
            .OrderByDescending(c => c.Id)
            .Take(200)
            .AsNoTracking()
            .ToListAsync(ct);

        return Results.Ok(cartoons.Select(ToResponse));
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

    private static CartoonResponse ToResponse(Cartoon c) =>
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
            ErrorMessage: c.ErrorMessage
        );
}
