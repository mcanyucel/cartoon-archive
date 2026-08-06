namespace CartoonArchive.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public AuthMode Mode { get; set; } = AuthMode.None;
    public string HeaderName { get; set; } = "X-Remote-User";
}

public enum AuthMode
{
    None = 0,
    ReverseProxy = 1,
}
