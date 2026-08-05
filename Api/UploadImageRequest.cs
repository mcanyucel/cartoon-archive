using Microsoft.AspNetCore.Mvc;

namespace CartoonArchive.Api;

public record UploadImageRequest([FromForm] IFormFile File, [FromForm] string Language);
