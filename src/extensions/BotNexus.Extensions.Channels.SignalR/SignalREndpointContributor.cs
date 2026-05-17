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
                context.Response.ContentType = contentType;
                context.Response.ContentLength = fileInfo.Length;
                await using var stream = fileInfo.CreateReadStream();
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
