namespace CartoonArchive.Sharing;

public sealed class ShareOptions
{
    public const string SectionName = "Share";

    public string? PublicBaseUrl { get; set; }
    public int TokenTtlHours { get; set; } = 1;
    public int HintWordCount { get; set; } = 6;
    public int SweepIntervalMinutes { get; set; } = 10;
}
