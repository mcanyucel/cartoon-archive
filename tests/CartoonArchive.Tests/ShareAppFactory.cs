using CartoonArchive.Data;
using CartoonArchive.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CartoonArchive.Tests;

/// <summary>
/// Boots the real app with three things swapped out: the clock (so share expiry can be
/// driven instantly), the database (a throwaway file per fixture), and the content root
/// (so image/thumbnail writes land in temp rather than the developer's own archive).
/// Hosted services are removed so the cleanup sweeps can't race the assertions.
/// </summary>
public sealed class ShareAppFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cartoon-archive-tests",
        Guid.NewGuid().ToString("n")
    );

    /// <summary>Fixed instant so expiry maths in the tests reads as plain arithmetic.</summary>
    public FakeTimeProvider Clock { get; } =
        new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public string ThumbnailsDirectory => Path.Combine(_root, "data", "thumbnails");

    public string ImagesDirectory => Path.Combine(_root, "data", "images");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(ImagesDirectory);

        builder.UseContentRoot(_root);
        builder.UseSetting(WebHostDefaults.StaticWebAssetsKey, "false");

        // Appended last so it beats appsettings.json. Getting this wrong would point the
        // test run at the real archive database, so DatabasePath is asserted in the tests.
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = $"Data Source={DatabasePath}",
                    ["Share:TokenTtlHours"] = "1",
                    ["Share:MetadataTtlHours"] = "168",
                    ["Share:PublicBaseUrl"] = "https://share.example.test",
                }
            )
        );

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public string DatabasePath => Path.Combine(_root, "data", "test.db");

    /// <summary>
    /// Inserts a cartoon directly, bypassing upload and captioning. Writes a thumbnail
    /// file too unless suppressed, since the share image route serves from disk.
    /// </summary>
    public async Task<Cartoon> SeedCartoonAsync(
        CartoonStatus status = CartoonStatus.Ready,
        string? description = "A pharaoh argues with a traffic warden",
        string[]? tags = null,
        bool withThumbnailFile = true
    )
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();

        var sha = Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n");
        var cartoon = new Cartoon
        {
            Sha256 = sha,
            OriginalFileName = "seed.png",
            ContentType = "image/png",
            Language = "en",
            Description = description,
            Status = status,
            UploadedAt = Clock.GetUtcNow(),
            CaptionedAt = status == CartoonStatus.Ready ? Clock.GetUtcNow() : null,
        };
        db.Cartoons.Add(cartoon);
        await db.SaveChangesAsync();

        foreach (var name in tags ?? [])
        {
            var tag = new Tag { Name = name };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            db.CartoonTags.Add(new CartoonTag { CartoonId = cartoon.Id, TagId = tag.Id });
            await db.SaveChangesAsync();
        }

        if (withThumbnailFile)
        {
            // Content is irrelevant - the route only checks File.Exists and streams bytes.
            await File.WriteAllBytesAsync(
                Path.Combine(ThumbnailsDirectory, $"{sha}.webp"),
                [0x52, 0x49, 0x46, 0x46]
            );
        }

        return cartoon;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            { /* a stray temp dir is not worth failing a test run over */
            }
        }
    }
}
