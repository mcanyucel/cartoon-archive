namespace CartoonArchive.Storage;

/// <summary>
/// Thrown when bytes on disk cannot be decoded as an image, i.e. the uploader's declared
/// content type was a lie. Distinct from environmental failures (disk full, unwritable
/// directory) on purpose: callers delete the stored file when they see this, and that is
/// only safe when the bytes are known to be garbage.
/// </summary>
public sealed class ImageDecodeException(string message) : Exception(message);
