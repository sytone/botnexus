using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>Prevents either Portal client from selecting an application from device geometry.</summary>
public sealed class ExplicitClientViewArchitectureTests : ArchitectureTest
{
    private static readonly Regex DeviceSignal = new(
        @"matchMedia\s*\(|maxTouchPoints|isMobileView|\b_isMobile\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Portal_clients_do_not_select_views_from_device_signals()
    {
        var clientRoot = Path.Combine(Repository.Root, "src", "extensions");
        var roots = new[]
        {
            Path.Combine(clientRoot, "BotNexus.Extensions.Channels.SignalR.BlazorClient"),
            Path.Combine(clientRoot, "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile")
        };

        var files = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .ToList();

        files.Count.ShouldBeGreaterThan(20, "the client source scan found suspiciously few files");

        var violations = files
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                if (DeviceSignal.IsMatch(source))
                    return true;

                // innerWidth remains valid for bounded popup positioning. It is forbidden in
                // component code and the two chat helper modules, where it previously selected
                // the desktop/mobile application and route.
                return source.Contains("window.innerWidth", StringComparison.Ordinal)
                    && (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("chat.js", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("chatScroll.js", StringComparison.OrdinalIgnoreCase));
            })
            .Select(path => Path.GetRelativePath(Repository.Root, path).Replace('\\', '/'))
            .ToList();

        violations.ShouldBeEmpty(
            "Desktop and mobile are explicit applications. Geometry or touch capability must never select a layout, route, or component tree. "
            + "Responsive CSS and diagnostic-only user-agent capture remain permitted. Offending files: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void Desktop_and_mobile_clients_use_the_shared_build_info_service()
    {
        var service = Path.Combine(
            Repository.Root,
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services",
            "GatewayInfoService.cs");

        File.Exists(service).ShouldBeTrue("client build identity must have one shared implementation");
        File.ReadAllText(service).ShouldContain("BuildTimestamp");

        var desktop = File.ReadAllText(Path.Combine(
            Repository.Root,
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "Layout",
            "MainLayout.razor"));
        var mobile = File.ReadAllText(Path.Combine(
            Repository.Root,
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
            "Pages",
            "Chat.razor"));

        desktop.ShouldContain("GatewayInfoService");
        mobile.ShouldContain("GatewayInfoService");
    }
}
