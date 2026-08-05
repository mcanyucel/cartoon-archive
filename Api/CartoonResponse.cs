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
    string ThumbnailUrl
);
