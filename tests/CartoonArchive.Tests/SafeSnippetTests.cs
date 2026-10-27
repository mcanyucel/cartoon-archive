using CartoonArchive.Api;

namespace CartoonArchive.Tests;

/// <summary>
/// Search snippets are rendered as raw HTML on the client, so SafeSnippet is an XSS
/// boundary. FTS5 is asked for %%OPEN%%/%%CLOSE%% markers instead of real tags precisely so
/// the whole string can be HTML-encoded first and the markers upgraded afterwards.
/// </summary>
public class SafeSnippetTests
{
    [Fact]
    public void Upgrades_markers_to_mark_tags()
    {
        Assert.Equal("<mark>hello</mark>", ImageEndpoints.SafeSnippet("%%OPEN%%hello%%CLOSE%%"));
    }

    [Fact]
    public void Upgrades_every_marker_pair()
    {
        Assert.Equal(
            "a <mark>b</mark> c <mark>d</mark>",
            ImageEndpoints.SafeSnippet("a %%OPEN%%b%%CLOSE%% c %%OPEN%%d%%CLOSE%%")
        );
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("</mark><script>alert(1)</script>")]
    public void Escapes_html_in_the_description(string malicious)
    {
        var result = ImageEndpoints.SafeSnippet(malicious);

        // The property that matters is that no tag can form: every angle bracket is
        // encoded, so leftover text like `onerror=alert(1)` is inert prose. The only tags
        // in the output are the <mark> pair this method introduces itself.
        Assert.DoesNotContain("<", result.Replace("<mark>", "").Replace("</mark>", ""));
        Assert.Contains("&lt;", result);
    }

    [Fact]
    public void Escapes_html_while_still_upgrading_markers()
    {
        var result = ImageEndpoints.SafeSnippet("%%OPEN%%<b>bold</b>%%CLOSE%%");

        Assert.StartsWith("<mark>", result);
        Assert.EndsWith("</mark>", result);
        Assert.DoesNotContain("<b>", result);
        Assert.Contains("&lt;b&gt;", result);
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        Assert.Equal("just a caption", ImageEndpoints.SafeSnippet("just a caption"));
    }

    /// <summary>
    /// Documents a known, benign quirk rather than asserting it is desirable: a description
    /// that literally contains the marker text gets a real mark tag. The blast radius is
    /// one highlight tag - the HTML encode still runs first, so no other markup escapes -
    /// but if the markers ever need to be collision-proof, this is the test that fails.
    /// </summary>
    [Fact]
    public void Marker_text_inside_a_description_also_becomes_a_tag()
    {
        Assert.Equal("<mark>", ImageEndpoints.SafeSnippet("%%OPEN%%"));
    }
}
