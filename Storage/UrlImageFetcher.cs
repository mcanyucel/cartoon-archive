using System.Net;
using System.Net.Sockets;

namespace CartoonArchive.Storage;

public sealed class UrlFetchException(string message) : Exception(message);

public sealed class UrlImageFetcher(HttpClient http)
{
    private static readonly HashSet<string> AllowedContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    private const long MaxBytes = 20L * 1024 * 1024; // 20 MB

    public async Task<(Stream Content, string contentType)> FetchAsync(
        string urlString,
        CancellationToken ct
    )
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
            throw new UrlFetchException("Invalid URL.");
        if (url.Scheme is not ("http" or "https"))
            throw new UrlFetchException("Only http and https URLs are allowed.");

        // SSRF protection happens inside HttpClient via SocketsHttpHandler.ConnectCallback.
        // It rejects private/loopback IPs at connect time (defeats DNS rebinding).
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is UrlFetchException inner)
        {
            // ConnectCallback throws UrlFetchException; HttpClient rewraps it in HttpRequestException.
            throw inner;
        }
        catch (HttpRequestException ex)
        {
            throw new UrlFetchException($"Could not fetch URL: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UrlFetchException("URL fetch timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UrlFetchException(
                    $"Remote returned {(int)response.StatusCode} {response.ReasonPhrase}."
                );

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentType) || !AllowedContentTypes.Contains(contentType))
                throw new UrlFetchException($"Unsupported content type: {contentType ?? "(none)"}");

            var length = response.Content.Headers.ContentLength;
            if (length > MaxBytes)
                throw new UrlFetchException($"Image too large ({length} bytes; max {MaxBytes}).");

            // Stream to a buffer with a hard cap in case Content-Length was missing or lied
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            var buffered = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    throw new UrlFetchException($"Image exceeds max size of {MaxBytes} bytes.");
                await buffered.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            buffered.Position = 0;
            return (buffered, contentType);
        }
    }
}

/// <summary>
/// Attached to the HttpClient as SocketsHttpHandler.ConnectCallback in Program.cs.
/// Resolves the hostname, rejects any private/loopback/link-local IP, then connects
/// </summary>
public static class SsrfGuard
{
    public static async ValueTask<Stream> SafeConnectAsync(
        SocketsHttpConnectionContext ctx,
        CancellationToken ct
    )
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0)
            throw new UrlFetchException($"Could not resolve {host}.");

        foreach (var addr in addresses)
        {
            if (IsPrivateOrLocal(addr))
                throw new UrlFetchException(
                    $"Refusing to connect to {host}: resolved address {addr} is private/local."
                );
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPrivateOrLocal(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr))
            return true;

        // Normalize IPv4-mapped IPv6 (::ffff:1.2.3.4) to IPv4 so the IPv4 checks below fire.
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            return b[0] switch
            {
                0 => true, // 0.0.0.0/8
                10 => true, // 10.0.0.0/8
                127 => true, // 127.0.0.0/8
                169 when b[1] == 254 => true, // 169.254.0.0/16 (link-local + cloud metadata)
                172 when b[1] >= 16 && b[1] <= 31 => true, // 172.16.0.0/12
                192 when b[1] == 168 => true, // 192.168.0.0/16
                192 when b[1] == 0 && b[2] == 0 => true, // 192.0.0.0/24
                100 when b[1] >= 64 && b[1] <= 127 => true, // 100.64.0.0/10 (CGNAT)
                _ when b[0] >= 224 => true, // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
                _ => false,
            };
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal)
                return true;
            if (addr.IsIPv6SiteLocal)
                return true;
            if (addr.IsIPv6Multicast)
                return true;
            // fc00::/7 unique local addresses — .NET has no built-in check for this.
            var b = addr.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
                return true;
            return false;
        }

        // Unknown address family — refuse rather than allow.
        return true;
    }
}
