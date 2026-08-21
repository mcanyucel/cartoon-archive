using CartoonArchive.Data;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Tagging;

public static class TagLoader
{
    /// <summary>
    /// Batch-loads tag names grouped by cartoon id. Empty dictionary when no ids.
    /// Names are sorted alphabetically within each cartoon for stable UI ordering
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> LoadAsync(
        CartoonArchiveDbContext db,
        IReadOnlyCollection<int> cartoonIds,
        CancellationToken ct
    )
    {
        if (cartoonIds.Count == 0)
            return new Dictionary<int, IReadOnlyList<string>>();

        var rows = await db
            .CartoonTags.Where(j => cartoonIds.Contains(j.CartoonId))
            .Join(db.Tags, j => j.TagId, t => t.Id, (j, t) => new { j.CartoonId, t.Name })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.GroupBy(r => r.CartoonId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Name).OrderBy(n => n).ToList()
            );
    }
}
