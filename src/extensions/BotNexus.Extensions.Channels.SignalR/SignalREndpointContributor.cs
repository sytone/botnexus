using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Registers the SignalR hub and Blazor WASM client hosting.
/// All web surface for this channel is self-contained in this extension.
/// </summary>
public class SignalREndpointContributor : IEndpointContributor
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapHub<GatewayHub>("/hub/gateway");

        var extensionDir = Path.GetDirectoryName(typeof(SignalREndpointContributor).Assembly.Location)!;
        var blazorPath = Path.Combine(extensionDir, "blazor");
        var mobilePath = Path.Combine(extensionDir, "blazor-mobile");

        if (Directory.Exists(blazorPath))
            MapBlazorApp(app, blazorPath, pathPrefix: null);

        if (Directory.Exists(mobilePath))
            MapBlazorApp(app, mobilePath, pathPrefix: "/mobile");
    }

    private static void MapBlazorApp(WebApplication app, string blazorPath, string? pathPrefix)
    {
        var indexHtmlPath = Path.Combine(blazorPath, "index.html");
        if (!File.Exists(indexHtmlPath))
        {
            app.Services.GetService<ILogger<SignalREndpointContributor>>()?.LogWarning(
                "Blazor client index.html not found at {Path} — skipping endpoint registration", indexHtmlPath);
            return;
        }

        var fileProvider = new PhysicalFileProvider(blazorPath);
        var indexBytes = File.ReadAllBytes(indexHtmlPath);
        var prefix = pathPrefix ?? string.Empty;

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            // Only handle requests under this prefix
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }
                // Strip prefix for file lookup
                path = path[prefix.Length..];
                if (string.IsNullOrEmpty(path)) path = "/";
            }
            else
            {
                // Desktop: let API/hub/health/swagger pass through
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/mobile", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }
            }

            var subPath = path == "/" ? "/index.html" : path;
            var fileInfo = fileProvider.GetFileInfo(subPath);

            if (fileInfo.Exists && !fileInfo.IsDirectory)
            {
                var contentType = GetContentType(subPath);

                // Prefer a precompressed sibling (.br/.gz) when the client accepts it.
                // Blazor publish emits these next to each asset; serving them keeps the
                // large runtime wasm/js small over the wire and avoids mid-flight load
                // failures on mobile over slow/proxied links. The Content-Type stays the
                // original payload type; Content-Encoding tells the browser how to decode.
                var (encodedFile, encoding) = SelectEncodedFile(
                    fileProvider, subPath, context.Request.Headers.AcceptEncoding);
                var fileToServe = encodedFile ?? fileInfo;

                context.Response.ContentType = contentType;
                if (encoding is not null)
                    context.Response.Headers.ContentEncoding = encoding;
                context.Response.Headers.Vary = "Accept-Encoding";
                context.Response.Headers.CacheControl = ResolveCacheControl(subPath);
                context.Response.ContentLength = fileToServe.Length;
                await using var stream = fileToServe.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
                return;
            }

            // SPA fallback for client-side routes
            if (!subPath.Contains('.'))
            {
                context.Response.ContentType = "text/html";
                await context.Response.Body.WriteAsync(indexBytes);
                return;
            }

            await next();
        });
    }

    // Chooses a Cache-Control policy for a served asset. Blazor publish content-hashes
    // every _framework asset (e.g. dotnet.native.veuqw8a0w9.wasm), so those bytes are
    // immutable: a content change produces a NEW filename, never a mutated one. Those
    // can be cached aggressively (a year, immutable) so repeat loads skip the ~1.6 MB
    // runtime re-download entirely. Everything else (index.html, appsettings.json,
    // manifests, hand-authored css/js) is served under a stable path and MUST revalidate
    // so a new deployment is picked up immediately -> no-cache (store but always
    // revalidate). We intentionally do not emit ETags: the immutable set does not need
    // them, and the mutable set already round-trips a conditional GET via no-cache.
    internal static string ResolveCacheControl(string subPath)
    {
        // Fingerprinted framework assets live under _framework/ and carry a content hash
        // in the filename. index.html itself is not fingerprinted and stays revalidated.
        var fileName = subPath[(subPath.LastIndexOf('/') + 1)..];
        var isFramework = subPath.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase);
        var isFingerprinted = isFramework
            && !fileName.Equals("blazor.boot.json", StringComparison.OrdinalIgnoreCase)
            && HasContentHash(fileName);

        return isFingerprinted
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }

    // Blazor fingerprints assets by inserting a base36 content hash segment between the
    // base name and the extension, e.g. "dotnet.native.veuqw8a0w9.wasm" or
    // "System.Private.CoreLib.s1cucomlii.wasm". We treat a file as fingerprinted when it
    // has at least three dot-separated segments and the penultimate segment looks like a
    // hash: >=8 lowercase-alphanumeric chars AND containing at least one digit. The digit
    // requirement is what separates a real content hash (always has digits, e.g.
    // "veuqw8a0w9") from a word-like segment ("webassembly" in blazor.webassembly.js),
    // so loader entry points stay revalidated. This is deliberately conservative: an
    // unrecognized file simply falls back to the safe no-cache policy.
    private static bool HasContentHash(string fileName)
    {
        var segments = fileName.Split('.');
        if (segments.Length < 3)
            return false;

        var hash = segments[^2];
        if (hash.Length < 8)
            return false;

        var hasDigit = false;
        foreach (var ch in hash)
        {
            if (ch is >= '0' and <= '9')
                hasDigit = true;
            else if (ch is not (>= 'a' and <= 'z'))
                return false;
        }

        return hasDigit;
    }

    // Returns the precompressed sibling file and its Content-Encoding token when the
    // client accepts an encoding for which a sibling exists; otherwise (null, null).
    // Brotli is preferred over gzip. Already-compressed requests are left untouched.
    internal static (IFileInfo? File, string? Encoding) SelectEncodedFile(
        IFileProvider fileProvider, string subPath, StringValues acceptEncoding)
    {
        if (subPath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ||
            subPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var accepts = acceptEncoding.ToString();

        if (accepts.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            var br = fileProvider.GetFileInfo(subPath + ".br");
            if (br.Exists && !br.IsDirectory)
                return (br, "br");
        }

        if (accepts.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            var gz = fileProvider.GetFileInfo(subPath + ".gz");
            if (gz.Exists && !gz.IsDirectory)
                return (gz, "gzip");
        }

        return (null, null);
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        ".dll" => "application/octet-stream",
        ".pdb" => "application/octet-stream",
        ".dat" => "application/octet-stream",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".map" => "application/json",
        ".gz" => "application/gzip",
        ".br" => "application/brotli",
        _ => "application/octet-stream"
    };
}
