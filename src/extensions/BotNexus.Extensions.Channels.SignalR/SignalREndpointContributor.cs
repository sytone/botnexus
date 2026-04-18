using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

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

        if (!Directory.Exists(blazorPath))
            return;

        var blazorFileProvider = new PhysicalFileProvider(blazorPath);
        var indexBytes = File.ReadAllBytes(Path.Combine(blazorPath, "index.html"));

        // Single middleware handles both static files and SPA fallback for /blazor/
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!path.StartsWith("/blazor/", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/blazor", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // Strip /blazor prefix to resolve the file
            var subPath = path.Length > 7 ? path[7..] : "/";
            var fileInfo = blazorFileProvider.GetFileInfo(subPath);

            if (fileInfo.Exists && !fileInfo.IsDirectory)
            {
                // Serve the actual file
                var contentType = GetContentType(subPath);
                context.Response.ContentType = contentType;
                context.Response.ContentLength = fileInfo.Length;
                await using var stream = fileInfo.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
                return;
            }

            // SPA fallback for client-side routes (paths without file extensions)
            if (!subPath.Contains('.'))
            {
                context.Response.ContentType = "text/html";
                await context.Response.Body.WriteAsync(indexBytes);
                return;
            }

            // File with extension that doesn't exist — 404
            context.Response.StatusCode = 404;
        });
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
