namespace CartoonArchive.Models;

public sealed class Cartoon
{
    public int Id { get; set; }
    public required string Sha256 { get; set; }
    public required string OriginalFileName { get; set; }
    public required string ContentType { get; set; }
    public required string Language { get; set; }
    public string? Description { get; set; }
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
