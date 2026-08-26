using System.Globalization;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

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

            // A client-side route (no file extension, no file on disk) is served the SPA
            // document. This used to be a separate branch writing a byte[] captured with
            // File.ReadAllBytes at startup, which meant every deep link served whatever build
            // was on disk when the gateway last started: "/" and "/index.html" read from disk
            // and picked a redeployment up immediately, while "/configuration" kept booting the
            // previous runtime and its assembly list. The snapshot also bypassed the content
            // type, Cache-Control and ETag handling below, so the one document that MUST
            // revalidate was the one served without a validator. Rewriting the path instead of
            // duplicating the response keeps all of that in one place.
            if ((!fileInfo.Exists || fileInfo.IsDirectory) && !subPath.Contains('.'))
            {
                subPath = "/index.html";
                fileInfo = fileProvider.GetFileInfo(subPath);
            }

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

                var cacheControl = ResolveCacheControl(subPath);

                context.Response.ContentType = contentType;
                if (encoding is not null)
                    context.Response.Headers.ContentEncoding = encoding;
                context.Response.Headers.Vary = "Accept-Encoding";
                context.Response.Headers.CacheControl = cacheControl;

                // Fingerprinted assets are immutable and never revalidated, so a validator
                // would be dead weight. Everything else is served under no-cache, which only
                // *permits* a conditional GET -- without a validator the browser has nothing
                // to put in If-None-Match, so every revalidation re-downloads the full body.
                // Emitting one lets us answer 304 instead. See #2413.
                if (cacheControl == RevalidateCacheControl)
                {
                    var etag = BuildEntityTag(fileToServe);
                    context.Response.Headers.ETag = etag;
                    context.Response.Headers.LastModified =
                        fileToServe.LastModified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

                    if (IsNotModified(context.Request, etag, fileToServe.LastModified))
                    {
                        // A 304 carries validators and caching headers but no body, and must
                        // not advertise a Content-Length/Content-Encoding for a body it omits.
                        context.Response.StatusCode = StatusCodes.Status304NotModified;
                        context.Response.Headers.Remove(HeaderNames.ContentEncoding);
                        context.Response.ContentType = null;
                        context.Response.ContentLength = null;
                        return;
                    }
                }

                context.Response.ContentLength = fileToServe.Length;
                await using var stream = fileToServe.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
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
    // revalidate). The immutable set carries no validator (it is never revalidated); the
    // mutable set gets an ETag + Last-Modified so the revalidation no-cache mandates can
    // actually settle as a 304 rather than a full re-download (#2413).
    internal const string RevalidateCacheControl = "no-cache";

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
            : RevalidateCacheControl;
    }

    // Builds a weak validator for a revalidated asset. It is derived from the file that is
    // ACTUALLY served (the .br/.gz sibling when one was selected), not the identity file, so
    // two encodings of the same resource never share a tag. Weak is correct here: last-write
    // plus length identifies the deployed bytes without hashing megabytes on every request.
    internal static string BuildEntityTag(IFileInfo file) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"W/\"{file.LastModified.ToUniversalTime().Ticks:x}-{file.Length:x}\"");

    // Honours If-None-Match, falling back to If-Modified-Since only when no If-None-Match was
    // sent (RFC 9110: an entity-tag comparison always wins over a date comparison). Both the
    // stored and request timestamps are truncated to whole seconds because HTTP-date has
    // one-second resolution -- comparing raw ticks would never match a date we just emitted.
    internal static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            foreach (var value in ifNoneMatch)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                foreach (var candidate in value.Split(','))
                {
                    var trimmed = candidate.Trim();
                    if (trimmed == "*" || string.Equals(trimmed, etag, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        var ifModifiedSince = request.Headers.IfModifiedSince.ToString();
        if (!string.IsNullOrWhiteSpace(ifModifiedSince)
            && DateTimeOffset.TryParse(
                ifModifiedSince, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var since))
        {
            var stored = new DateTimeOffset(
                lastModified.ToUniversalTime().Ticks - (lastModified.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond),
                TimeSpan.Zero);
            return stored <= since;
        }

        return false;
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

    internal static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".webmanifest" => "application/manifest+json",
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
