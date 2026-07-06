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
