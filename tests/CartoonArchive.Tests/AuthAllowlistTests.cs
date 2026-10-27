using CartoonArchive.Auth;
using Microsoft.AspNetCore.Http;

namespace CartoonArchive.Tests;

/// <summary>
/// The allowlist decides what stays reachable without the reverse proxy's auth header.
/// Two commits have already gone into getting it right (PWA install probes, then
/// MapStaticAssets fingerprints), so the edges are worth pinning down.
/// </summary>
public class AuthAllowlistTests
{
    [Theory]
    [InlineData("/s/abc123")] // share links are the whole point of the allowlist
    [InlineData("/s/abc123/image")]
    [InlineData("/css/site.css")]
    [InlineData("/js/gallery.js")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    [InlineData("/favicon.ico")]
    [InlineData("/favicon.svg")]
    [InlineData("/favicon-96x96.png")]
    [InlineData("/apple-touch-icon.png")]
    [InlineData("/manifest.json")]
    [InlineData("/sw.js")]
    [InlineData("/web-app-manifest-192x192.png")]
    [InlineData("/web-app-manifest-512x512.png")]
    public void Allows_public_assets_and_share_links(string path)
    {
        Assert.True(ReverseProxyAuthMiddleware.IsAllowlisted(new PathString(path)));
    }

    /// <summary>
    /// User content and every mutating endpoint must stay behind auth. /images and
    /// /thumbnails are deliberately excluded - they are only meant to be reached
    /// indirectly through /s/{token}.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/Upload")]
    [InlineData("/Search")]
    [InlineData("/api/images")]
    [InlineData("/api/images/1")]
    [InlineData("/api/tags")]
    [InlineData("/images/abc.jpg")]
    [InlineData("/thumbnails/abc.webp")]
    public void Blocks_private_paths(string path)
    {
        Assert.False(ReverseProxyAuthMiddleware.IsAllowlisted(new PathString(path)));
    }

    /// <summary>
    /// StartsWithSegments is segment-aware, so a prefix must not leak to a sibling path
    /// that merely starts with the same letters.
    /// </summary>
    [Theory]
    [InlineData("/secret")]
    [InlineData("/sensitive/data")]
    [InlineData("/cssfake")]
    [InlineData("/jsonly")]
    public void Prefix_match_is_segment_aware(string path)
    {
        Assert.False(ReverseProxyAuthMiddleware.IsAllowlisted(new PathString(path)));
    }

    /// <summary>
    /// MapStaticAssets rewrites hrefs to include a content hash. The allowlist stays
    /// declarative by stripping that suffix before matching.
    /// </summary>
    [Theory]
    [InlineData("/manifest.49ong59m6e.json", "/manifest.json")]
    [InlineData("/sw.abcdef123456.js", "/sw.js")]
    [InlineData("/css/site.0123456789.css", "/css/site.css")]
    public void Strips_fingerprints_before_matching(string hashed, string expected)
    {
        Assert.Equal(expected, ReverseProxyAuthMiddleware.StripFingerprint(hashed).Value);
        Assert.True(ReverseProxyAuthMiddleware.IsAllowlisted(new PathString(hashed)));
    }

    [Theory]
    [InlineData("/manifest.json")] // nothing to strip
    [InlineData("/css/site.css")]
    [InlineData("/s/abc123")] // no extension at all
    [InlineData("/manifest.ab.json")] // hash segment too short to be a fingerprint
    public void Leaves_unfingerprinted_paths_untouched(string path)
    {
        Assert.Equal(path, ReverseProxyAuthMiddleware.StripFingerprint(path).Value);
    }

    /// <summary>
    /// Stripping must not become a bypass: a fingerprint-shaped suffix on a private path
    /// still has to resolve to that private path.
    /// </summary>
    [Theory]
    [InlineData("/api/secrets.abcdef123456.json")]
    [InlineData("/images/abcdef123456.jpg")]
    public void Fingerprint_stripping_is_not_an_auth_bypass(string path)
    {
        Assert.False(ReverseProxyAuthMiddleware.IsAllowlisted(new PathString(path)));
    }
}
