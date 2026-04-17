using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace BotNexus.Channels.SignalR;

/// <summary>
/// Registers the SignalR hub and static file serving for the Blazor WebUI.
/// </summary>
public class SignalREndpointContributor : IEndpointContributor
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapHub<GatewayHub>("/hub/gateway");

        // Serve Blazor WASM client at /blazor/
        var blazorPath = Path.Combine(app.Environment.WebRootPath, "blazor");
        if (Directory.Exists(blazorPath))
        {
            var blazorFileProvider = new PhysicalFileProvider(blazorPath);

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = blazorFileProvider,
                RequestPath = "/blazor"
            });

            // SPA fallback: any unmatched /blazor/* route serves the Blazor index.html
            app.MapFallbackToFile("/blazor/{**path}", "blazor/index.html");
        }
    }
}
