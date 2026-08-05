using SkiaSharp;

namespace CartoonArchive.Storage;

public sealed class ThumbnailGenerator(IWebHostEnvironment env)
{
    private const int MaxDimension = 512;
    private const int WebpQuality = 80;
    private readonly string _thumbnailsDirectory = Path.Combine(
        env.ContentRootPath,
        "data",
        "thumbnails"
    );

    public async Task GenerateAsync(string sha256, string sourcePath, CancellationToken ct)
    {
        Directory.CreateDirectory(_thumbnailsDirectory);
        var destPath = Path.Combine(_thumbnailsDirectory, $"{sha256}.webp");

        await Task.Run(
            () =>
            {
                using var input =
                    SKBitmap.Decode(sourcePath)
                    ?? throw new InvalidOperationException(
                        $"Could not decode image at {sourcePath}"
                    );

                var (targetW, targetH) = ResizeToFit(input.Width, input.Height, MaxDimension);
                using var resized = input.Resize(
                    new SKImageInfo(targetW, targetH),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                );
                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
                using var output = File.Create(destPath);
                data.SaveTo(output);
            },
            ct
        );
    }

    public string GetPath(string sha256) => Path.Combine(_thumbnailsDirectory, $"{sha256}.webp");

    private static (int width, int height) ResizeToFit(int width, int height, int maxDim)
    {
        if (width <= maxDim && height <= maxDim)
            return (width, height);

        var ratio = (double)maxDim / Math.Max(width, height);
        return ((int)(width * ratio), (int)(height * ratio));
    }
}
