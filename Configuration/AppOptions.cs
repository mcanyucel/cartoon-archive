namespace CartoonArchive.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Set to true when running behind a reverse proxy so X-Forwarded-* headers are respected and 
    /// Request.IsHttps reflects what the client actually saw. Leave false for bare-metal / no-proxy.
    /// </summary>
    public bool TrustProxyHeaders { get; set; } = false;
}
