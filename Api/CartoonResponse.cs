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
    IReadOnlyList<string> Tags,
    string? Snippet = null
);

public record NearDuplicate(int Id, string ThumbnailUrl, int DistanceBits);

public record UploadResponse(CartoonResponse Cartoon, IReadOnlyList<NearDuplicate> NearDuplicates);

public record EditRequest(string? Description, string? Language);

public record RegenerateRequest(string? Language);

public record TagResponse(int Id, string Name);

public record TagListResponse(IReadOnlyList<TagResponse> Items);

public record SetTagsRequest(IReadOnlyList<string>? Names);

public record CartoonTagsResponse(int CartoonId, IReadOnlyList<string> Tags);
