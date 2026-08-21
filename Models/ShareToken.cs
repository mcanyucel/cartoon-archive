namespace CartoonArchive.Models;

public sealed class ShareToken
{
    public required string Token { get; set; }
    public int CartoonId { get; set; }
    public Cartoon Cartoon { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ActiveUntil { get; set; }
    public DateTimeOffset MetadataUntil { get; set; }
}
