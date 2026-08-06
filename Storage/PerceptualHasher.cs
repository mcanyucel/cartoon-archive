using SkiaSharp;

namespace CartoonArchive.Storage;

public sealed class PerceptualHasher
{
    // 9x8 => 8 adjacent-pixel comparisons per row x 8 rows = 64 bits
    private const int Width = 9;
    private const int Height = 8;

    public async Task<long> HashAsync(string imagePath, CancellationToken ct)
    {
        return await Task.Run(
            () =>
            {
                using var input =
                    SKBitmap.Decode(imagePath)
                    ?? throw new InvalidOperationException(
                        $"Could not decode image at {imagePath}"
                    );

                var info = new SKImageInfo(Width, Height);
                using var resized = input.Resize(
                    info,
                    new SKSamplingOptions(SKCubicResampler.Mitchell)
                );
                if (resized is null)
                    throw new InvalidOperationException("Failed to resize image for hashing");

                // Convert each pixel to a grayscale luminance byte (BT.601 coefficients)
                var gray = new byte[Width * Height];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        var c = resized.GetPixel(x, y);
                        gray[y * Width + x] = (byte)(
                            0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue
                        );
                    }
                }

                // dHash: bit is 1 where pixel > its right neighbor.
                long hash = 0;
                int bit = 0;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width - 1; x++)
                    {
                        if (gray[y * Width + x] > gray[y * Width + x + 1])
                            hash |= 1L << bit;
                        bit++;
                    }
                }
                return hash;
            },
            ct
        );
    }

    public static int HammingDistance(long a, long b) =>
        System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));
}
