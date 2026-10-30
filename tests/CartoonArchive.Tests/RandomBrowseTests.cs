using System.Net;
using System.Net.Http.Json;
using CartoonArchive.Models;

namespace CartoonArchive.Tests;

/// <summary>
/// The two endpoints behind random browse. The shuffle itself is client-side, so what is
/// testable here is the contract it leans on: a deterministic Ready-only id list, and a
/// batch hydrate that tolerates the bag naming cartoons that have since been deleted.
/// </summary>
public class RandomBrowseTests
{
    private sealed record IdsResponse(IReadOnlyList<int> Ids);

    private sealed record BatchItem(
        int Id,
        string Language,
        CartoonStatus Status,
        string? Description,
        IReadOnlyList<string> Tags
    );

    private sealed record BatchResponse(IReadOnlyList<BatchItem> Items);

    private static async Task<IdsResponse> GetIdsAsync(ShareAppFactory app)
    {
        var response = await app.CreateClient().GetAsync("/api/images/ids");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IdsResponse>();
        Assert.NotNull(body);
        return body!;
    }

    [Fact]
    public async Task Ids_returns_only_ready_cartoons()
    {
        using var app = new ShareAppFactory();
        var ready = await app.SeedCartoonAsync();
        await app.SeedCartoonAsync(CartoonStatus.Pending, description: null);
        await app.SeedCartoonAsync(CartoonStatus.Failed, description: null);
        var alsoReady = await app.SeedCartoonAsync();

        var body = await GetIdsAsync(app);

        Assert.Equal(new[] { ready.Id, alsoReady.Id }, body.Ids);
    }

    /// <summary>
    /// The client shuffles this list with a seeded PRNG, so a stable order is what makes a
    /// given seed reproduce a given walk. Insertion order happens to be ascending here;
    /// the point is that the endpoint sorts rather than relying on that.
    /// </summary>
    [Fact]
    public async Task Ids_are_ascending()
    {
        using var app = new ShareAppFactory();
        for (var i = 0; i < 5; i++)
            await app.SeedCartoonAsync();

        var body = await GetIdsAsync(app);

        Assert.Equal(5, body.Ids.Count);
        Assert.Equal(body.Ids.OrderBy(id => id), body.Ids);
    }

    [Fact]
    public async Task Ids_is_empty_for_an_empty_archive()
    {
        using var app = new ShareAppFactory();

        var body = await GetIdsAsync(app);

        Assert.Empty(body.Ids);
    }

    [Fact]
    public async Task Batch_hydrates_the_requested_ids_with_their_tags()
    {
        using var app = new ShareAppFactory();
        var first = await app.SeedCartoonAsync(tags: ["xkcd", "programming"]);
        var second = await app.SeedCartoonAsync(description: "A cat refuses to move");

        var response = await app.CreateClient()
            .GetAsync($"/api/images/batch?ids={first.Id},{second.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        Assert.Equal(["programming", "xkcd"], body.Items[0].Tags);
        Assert.Equal("A cat refuses to move", body.Items[1].Description);
    }

    [Fact]
    public async Task Batch_preserves_the_requested_order()
    {
        using var app = new ShareAppFactory();
        var a = await app.SeedCartoonAsync();
        var b = await app.SeedCartoonAsync();
        var c = await app.SeedCartoonAsync();

        // Deliberately not ascending - a shuffle bag asks in permuted order.
        var response = await app.CreateClient()
            .GetAsync($"/api/images/batch?ids={c.Id},{a.Id},{b.Id}");

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        Assert.Equal([c.Id, a.Id, b.Id], body!.Items.Select(i => i.Id));
    }

    /// <summary>
    /// A bag built minutes ago can name a cartoon deleted since. The client skips the hole,
    /// which only works if the server treats it as absence rather than an error.
    /// </summary>
    [Fact]
    public async Task Batch_silently_skips_unknown_ids()
    {
        using var app = new ShareAppFactory();
        var known = await app.SeedCartoonAsync();

        var response = await app.CreateClient()
            .GetAsync($"/api/images/batch?ids={known.Id},999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        Assert.Equal([known.Id], body!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Batch_rejects_more_than_a_hundred_ids()
    {
        using var app = new ShareAppFactory();

        var ids = string.Join(",", Enumerable.Range(1, 101));
        var response = await app.CreateClient().GetAsync($"/api/images/batch?ids={ids}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_accepts_exactly_a_hundred_ids()
    {
        using var app = new ShareAppFactory();

        var ids = string.Join(",", Enumerable.Range(1, 100));
        var response = await app.CreateClient().GetAsync($"/api/images/batch?ids={ids}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Batch_rejects_a_non_numeric_id()
    {
        using var app = new ShareAppFactory();

        var response = await app.CreateClient().GetAsync("/api/images/batch?ids=1,abc,3");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_with_no_ids_returns_an_empty_list()
    {
        using var app = new ShareAppFactory();
        await app.SeedCartoonAsync();

        var response = await app.CreateClient().GetAsync("/api/images/batch");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        Assert.Empty(body!.Items);
    }

    /// <summary>
    /// Duplicates would otherwise hydrate the same row twice and inflate the response past
    /// what the window asked for.
    /// </summary>
    [Fact]
    public async Task Batch_collapses_duplicate_ids()
    {
        using var app = new ShareAppFactory();
        var cartoon = await app.SeedCartoonAsync();

        var response = await app.CreateClient()
            .GetAsync($"/api/images/batch?ids={cartoon.Id},{cartoon.Id},{cartoon.Id}");

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        Assert.Single(body!.Items);
    }
}
