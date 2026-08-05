using System.Net.Mime;
using System.Runtime.Intrinsics.Arm;
using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
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

        return routes;
    }

    private static async Task<IResult> HandleUpload(
        [FromForm] UploadImageRequest request,
        ImageStorage storage,
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

        var existing = await db.Cartoons.FirstOrDefaultAsync(c => c.Sha256 == sha256, ct);
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

    private static CartoonResponse ToResponse(Cartoon c) =>
        new(c.Id, c.Sha256, c.OriginalFileName, c.Language, c.Status, c.UploadedAt);
}
