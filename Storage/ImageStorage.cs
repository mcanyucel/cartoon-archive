using System.Security.Cryptography;

namespace CartoonArchive.Storage;

public sealed class ImageStorage(IWebHostEnvironment env)
{
    private readonly string _imagesDirectory = Path.Combine(env.ContentRootPath, "data", "images");

    public async Task<string> StoreAsync(Stream input, string contentType, CancellationToken ct)
    {
        // Check on every request to prevent DirectoryNotFound error if something happens to directory mid-run
        Directory.CreateDirectory(_imagesDirectory);
        var extension = ExtensionFor(contentType);
        var tempPath = Path.Combine(_imagesDirectory, Path.GetRandomFileName());

        try
        {
            string sha256;
            await using (var tempStream = File.Create(tempPath))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await tempStream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            var finalPath = Path.Combine(_imagesDirectory, $"{sha256}{extension}");

            if (File.Exists(finalPath))
            {
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
            return sha256;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                { /* nothing else to do */
                }
            }
            throw;
        }
    }

    public string GetPath(string sha256, string contentType) =>
        Path.Combine(_imagesDirectory, $"{sha256}{ExtensionFor(contentType)}");

    public static string ExtensionFor(string contentType) =>
        contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new ArgumentException(
                $"Unsupported content type: {contentType}",
                nameof(contentType)
            ),
        };
}
