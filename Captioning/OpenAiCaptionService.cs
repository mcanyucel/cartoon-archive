using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CartoonArchive.Captioning;

public sealed class OpenAiCaptionService(HttpClient http, IOptions<CaptionOptions> options)
    : ICaptionService
{
    public async Task<string> CaptionAsync(
        string imagePath,
        string contentType,
        string language,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            throw new InvalidOperationException("Caption:ApiKey is not configured");

        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        var base64 = Convert.ToBase64String(bytes);
        var languageName = LanguageNames.Get(language);

        var prompt =
            $"Describe this image in {languageName}. Include any text visible in "
            + "speech bubbles or captions, the characters, their expressions, the setting, "
            + "and the joke or message if it is a cartoon.";

        var requestBody = new
        {
            model = options.Value.Model,
            max_tokens = options.Value.MaxTokens,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:{contentType};base64,{base64}" },
                        },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            options.Value.ApiKey
        );

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Vision API returned {(int)response.StatusCode}: {errorBody}"
            );
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var text = doc.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return text?.Trim()
            ?? throw new InvalidOperationException("Vision API returned an empty caption.");
    }
}
