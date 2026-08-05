namespace CartoonArchive.Captioning;

public interface ICaptionService
{
    Task<string> CaptionAsync(
        string imagePath,
        string contentType,
        string language,
        CancellationToken ct
    );
}
