using System.Net;
using System.Net.Http.Json;
using CartoonArchive.Models;

namespace CartoonArchive.Tests;

/// <summary>
/// The share-link lifecycle has three stages - viewable, metadata-only, gone - gated on two
/// timestamps. Before the clock was injectable, exercising the second and third stages
/// meant waiting out TokenTtlHours, so they were effectively untested.
/// </summary>
public class ShareLinkTests
{
    private sealed record ShareCreated(string Token, DateTimeOffset ExpiresAt, string Url);

    private static readonly TimeSpan PastActive = TimeSpan.FromHours(1);
    private static readonly TimeSpan PastMetadata = TimeSpan.FromHours(169);

    private static async Task<ShareCreated> CreateShareAsync(ShareAppFactory app, int cartoonId)
    {
        var client = app.CreateClient();
        var response = await client.PostAsync($"/api/images/{cartoonId}/share", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ShareCreated>();
        Assert.NotNull(created);
        return created!;
    }

    /// <summary>
    /// Guard rail, not a feature test: if the config override ever stops winning over
    /// appsettings.json, the suite would run against the real archive database. Fail loudly
    /// here rather than quietly migrating someone's data.
    /// </summary>
    [Fact]
    public void Test_host_uses_a_throwaway_database()
    {
        using var app = new ShareAppFactory();
        _ = app.CreateClient();

        Assert.StartsWith(Path.GetTempPath(), app.DatabasePath);
        Assert.DoesNotContain("cartoons.db", app.DatabasePath);
    }

    [Fact]
    public async Task Creating_a_share_uses_the_configured_public_base_url()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();

        var created = await CreateShareAsync(app, cartoon.Id);

        Assert.Equal($"https://share.example.test/s/{created.Token}", created.Url);
        Assert.Equal(app.Clock.GetUtcNow().Add(PastActive), created.ExpiresAt);
    }

    [Fact]
    public async Task Only_ready_cartoons_can_be_shared()
    {
        using var app = new ShareAppFactory();
        var pending = await app.SeedCartoonAsync(CartoonStatus.Pending, description: null);
        var failed = await app.SeedCartoonAsync(CartoonStatus.Failed, description: null);
        var client = app.CreateClient();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/images/{pending.Id}/share", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/images/{failed.Id}/share", null)).StatusCode
        );
    }

    [Fact]
    public async Task Sharing_a_missing_cartoon_is_not_found()
    {
        using var app = new ShareAppFactory();
        var client = app.CreateClient();

        var response = await client.PostAsync("/api/images/9999/share", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Stage 1: inside the active window ----

    [Fact]
    public async Task Active_share_renders_the_image_with_open_graph_tags()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync(description: "A pharaoh hails a taxi");
        var created = await CreateShareAsync(app, cartoon.Id);

        var response = await app.CreateClient().GetAsync($"/s/{created.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("og:image", html);
        Assert.Contains($"https://share.example.test/s/{created.Token}/image", html);
        Assert.Contains("A pharaoh hails a taxi", html);
        Assert.Equal("no-store, must-revalidate", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Active_share_serves_image_bytes_without_leaking_the_sha()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();
        var created = await CreateShareAsync(app, cartoon.Id);

        var response = await app.CreateClient().GetAsync($"/s/{created.Token}/image");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition?.ToString() ?? "";
        Assert.DoesNotContain(cartoon.Sha256, disposition);
        Assert.Contains("a.webp", disposition);
    }

    // ---- Stage 2: past ActiveUntil, still inside MetadataUntil ----

    [Fact]
    public async Task Expired_share_shows_metadata_but_not_the_image()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync(
            description: "A pharaoh hails a taxi",
            tags: ["firavun", "taksi"]
        );
        var created = await CreateShareAsync(app, cartoon.Id);

        app.Clock.Advance(PastActive);
        var response = await app.CreateClient().GetAsync($"/s/{created.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Link expired", html);
        Assert.Contains("A pharaoh hails a taxi", html);
        Assert.Contains("firavun", html);
        Assert.Contains("taksi", html);
        // The placeholder must not reference the bytes it is replacing.
        Assert.DoesNotContain("og:image", html);
        Assert.DoesNotContain($"/s/{created.Token}/image", html);
    }

    /// <summary>
    /// The important one. The image sub-route enforces ActiveUntil on its own, so the
    /// redaction performed by the HTML page cannot be sidestepped by requesting the bytes
    /// directly - which is exactly what a chat client's link preview cache would do.
    /// </summary>
    [Fact]
    public async Task Expired_share_refuses_to_serve_the_image_directly()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();
        var created = await CreateShareAsync(app, cartoon.Id);

        app.Clock.Advance(PastActive);
        var response = await app.CreateClient().GetAsync($"/s/{created.Token}/image");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Active_window_is_exclusive_at_its_upper_bound()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();
        var created = await CreateShareAsync(app, cartoon.Id);

        // One tick short of expiry: still live.
        app.Clock.Advance(PastActive - TimeSpan.FromSeconds(1));
        var client = app.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/s/{created.Token}/image")).StatusCode
        );

        // Exactly at ActiveUntil: gone.
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            HttpStatusCode.Gone,
            (await client.GetAsync($"/s/{created.Token}/image")).StatusCode
        );
    }

    // ---- Stage 3: past MetadataUntil ----

    [Fact]
    public async Task Share_is_fully_gone_once_metadata_expires()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync(description: "A pharaoh hails a taxi");
        var created = await CreateShareAsync(app, cartoon.Id);

        app.Clock.Advance(PastMetadata);
        var response = await app.CreateClient().GetAsync($"/s/{created.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("A pharaoh hails a taxi", html);
        Assert.Contains("no longer available", html);
    }

    /// <summary>
    /// An unknown token and a swept one return the same 410 with the same body, so a
    /// guesser can't tell "never existed" from "existed and expired".
    /// </summary>
    [Fact]
    public async Task Unknown_token_is_indistinguishable_from_an_expired_one()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();
        var created = await CreateShareAsync(app, cartoon.Id);
        var client = app.CreateClient();

        app.Clock.Advance(PastMetadata);
        var expired = await client.GetAsync($"/s/{created.Token}");
        var unknown = await client.GetAsync("/s/doesnotexist");

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, unknown.StatusCode);
        Assert.Equal(
            await expired.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task Image_route_is_gone_when_no_file_exists_on_disk()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync(withThumbnailFile: false);
        var created = await CreateShareAsync(app, cartoon.Id);

        var response = await app.CreateClient().GetAsync($"/s/{created.Token}/image");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
