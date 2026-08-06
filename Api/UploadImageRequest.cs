using Microsoft.AspNetCore.Mvc;

namespace CartoonArchive.Api;

public class UploadImageRequest
{
    [FromForm]
    public IFormFile? File { get; set; }

    [FromForm]
    public string Language { get; set; } = "";

    [FromForm]
    public string? Url { get; set; }
}
