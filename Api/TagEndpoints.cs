using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Tagging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Api;

public static class TagEndpoints
{
    private const int MaxTagsPerCartoon = 32;
    private const int DefaultAutocompleteLimit = 10;
    private const int MaxAutoCompleteLimit = 50;

    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api");

        group.MapGet("/tags", HandleAutocomplete);
        group.MapPut("images/{id:int}/tags", HandleSetTags);

        return routes;
    }

    private static async Task<IResult> HandleAutocomplete(
        [FromQuery] string? prefix,
        [FromQuery] int? limit,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var lim = Math.Clamp(limit ?? DefaultAutocompleteLimit, 1, MaxAutoCompleteLimit);
        var normalized = TagNormalizer.Normalize(prefix);

        IQueryable<Tag> query = db.Tags.AsNoTracking();

        if (normalized is not null)
        {
            var like =
                normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(t => EF.Functions.Like(t.Name, like, "\\"));
        }

        var tags = await query
            .OrderBy(t => t.Name)
            .Take(lim)
            .Select(t => new TagResponse(t.Id, t.Name))
            .ToListAsync(ct);

        return Results.Ok(new TagListResponse(tags));
    }

    private static async Task<IResult> HandleSetTags(
        int id,
        [FromBody] SetTagsRequest request,
        CartoonArchiveDbContext db,
        CancellationToken ct
    )
    {
        var cartoonExists = await db.Cartoons.AnyAsync(c => c.Id == id, ct);
        if (!cartoonExists)
            return Results.NotFound();

        // Normalize + dedupe while preserving first occurrence order
        var desired = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in request.Names ?? Array.Empty<string>())
        {
            var n = TagNormalizer.Normalize(name);
            if (n is null)
                continue;
            if (seen.Add(n))
                desired.Add(n);
        }

        if (desired.Count > MaxTagsPerCartoon)
            return Results.BadRequest(
                new { error = $"Too many tags; only {MaxTagsPerCartoon} per cartoon is allowed." }
            );

        // Look up existing tag rows for the desired names in one query
        var existing = await db
            .Tags.Where(t => desired.Contains(t.Name))
            .ToDictionaryAsync(t => t.Name, ct);

        // Insert missing tags so we have IDs to attach
        var newTags = desired
            .Where(n => !existing.ContainsKey(n))
            .Select(n => new Tag { Name = n })
            .ToList();

        if (newTags.Count > 0)
        {
            db.Tags.AddRange(newTags);
            await db.SaveChangesAsync(ct);
            foreach (var t in newTags)
                existing[t.Name] = t;
        }

        var desiredIds = desired.Select(n => existing[n].Id).ToHashSet();

        // Reconcile joins: delete stale, add new
        var currentJoins = await db.CartoonTags.Where(j => j.CartoonId == id).ToListAsync(ct);
        var currentIds = currentJoins.Select(j => j.TagId).ToHashSet();

        foreach (var j in currentJoins)
        {
            if (!desiredIds.Contains(j.TagId))
                db.CartoonTags.Remove(j);
        }
        foreach (var tid in desiredIds)
        {
            if (!currentIds.Contains(tid))
                db.CartoonTags.Add(new CartoonTag { CartoonId = id, TagId = tid });
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new CartoonTagsResponse(id, desired));
    }
}
