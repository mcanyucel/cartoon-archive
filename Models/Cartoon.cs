namespace CartoonArchive.Models;

public sealed class Cartoon
{
    public int Id { get; set; }
    public required string Sha256 { get; set; }
    public required string OriginalFileName { get; set; }
    public required string ContentType { get; set; }
    public required string Language { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// It is denormalized here so FTS5 can index it as a plain column of the content table.
    /// It should never be modified from app code; the DB triggers own it.
    /// </summary>
    public string TagsText { get; set; } = string.Empty;
    public long? PerceptualHash { get; set; }
    public CartoonStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? CaptionedAt { get; set; }
}

public enum CartoonStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}
