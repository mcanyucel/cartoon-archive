using CartoonArchive.Models;

namespace CartoonArchive.Api;

public record CartoonResponse(
    int Id,
    string Sha256,
    string OriginalFileName,
    string Language,
    CartoonStatus Status,
    DateTimeOffset UploadedAt,
    string ImageUrl,
    string ThumbnailUrl,
    string? Description,
    string? ErrorMessage,
    string? Snippet = null
);

public record NearDuplicate(int Id, string ThumbnailUrl, int DistanceBits);

public record UploadResponse(CartoonResponse Cartoon, IReadOnlyList<NearDuplicate> NearDuplicates);

public record EditRequest(string? Description, string? Language);

public record RegenerateRequest(string? Language);
