namespace CartoonArchive.Captioning;

public sealed class CaptionOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public int MaxTokens { get; set; } = 500;
}
