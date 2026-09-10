using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using NSubstitute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the two mechanisms that let the portal be a safe last-resort handler: contributor
/// ordering, and the endpoint-aware passthrough.
/// </summary>
/// <remarks>
/// The first test verifies an ASP.NET behaviour the design DEPENDS on rather than a behaviour this
/// repo implements - that routing has already matched by the time contributor middleware runs, so
/// a catch-all can tell "nobody claimed this" from "someone did" without naming paths. That
/// assumption is the whole basis for deleting the per-extension allowlist, so it is asserted
/// against a real pipeline instead of reasoned about.
/// </remarks>
public sealed class EndpointFallbackOrderingTests
{
    [Fact]
    public async Task Middleware_registered_before_MapGet_still_sees_the_matched_endpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        bool? sawEndpoint = null;

        // Registered BEFORE the endpoint, exactly as MapExtensionEndpoints runs before
        // MapControllers in Program.cs.
        app.Use(async (context, next) =>
        {
            sawEndpoint = context.GetEndpoint() is not null;
            await next();
        });
        app.MapGet("/claimed", () => "ok");

        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.GetAsync("/claimed");
        Assert.True(sawEndpoint, "routing must have matched before contributor middleware runs");

        // The negative half: an unclaimed path leaves GetEndpoint null, which is what keeps the
        // portal serving its SPA document for client-side routes.
        sawEndpoint = null;
        await client.GetAsync("/unclaimed-spa-route");
        Assert.False(sawEndpoint, "an unmatched path must leave the endpoint null");

        await app.StopAsync();
    }

    [Fact]
    public void Contributors_map_in_declared_order_not_registration_order()
    {
        var log = new List<string>();
        var services = new ServiceCollection();

        // Registered fallback-first, which is the order that used to decide the pipeline.
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("fallback", int.MaxValue, log));
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("ui", 0, log));

        using var provider = services.BuildServiceProvider();

        foreach (var contributor in provider.GetServices<IEndpointContributor>().OrderBy(c => c.Order))
            contributor.MapEndpoints(null!);

        Assert.Equal(["ui", "fallback"], log);
    }

    // Contributors that express no preference keep their relative registration order, so adding an
    // ordering signal cannot silently reshuffle a pipeline that was previously fine.
    [Fact]
    public void Contributors_sharing_an_order_keep_their_registration_sequence()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("first", 0, log));
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("second", 0, log));
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("third", 0, log));

        using var provider = services.BuildServiceProvider();

        foreach (var contributor in provider.GetServices<IEndpointContributor>().OrderBy(c => c.Order))
            contributor.MapEndpoints(null!);

        Assert.Equal(["first", "second", "third"], log);
    }

    // The default keeps every existing contributor where it was; only something that asks to move
    // moves.
    [Fact]
    public void A_contributor_that_declares_nothing_defaults_to_zero()
    {
        IEndpointContributor contributor = new DefaultOrderContributor();

        Assert.Equal(0, contributor.Order);
    }

    // Third-party code must not land ahead of the gateway's authentication. The two passes are what
    // make that possible without moving the portal's own contributor, which has to stay in front to
    // serve the page a user authenticates from.
    [Fact]
    public void An_extension_declaring_post_auth_maps_only_in_the_post_auth_pass()
    {
        var log = new List<string>();
        var app = AppWith(log);

        AssemblyLoadContextExtensionLoader.MapExtensionEndpoints(app);
        Assert.Equal(["Pre"], log);

        AssemblyLoadContextExtensionLoader.MapExtensionEndpointsAfterAuthentication(app);
        Assert.Equal(["Pre", "Post"], log);
    }

    // The default keeps everything that was working where it was. A contributor the gateway
    // registered directly matches no extension, and must still map before authentication.
    [Fact]
    public void A_contributor_belonging_to_no_extension_maps_before_authentication()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEndpointContributor>(new RecordingContributor("Unattributed", 0, log));
        var app = BuildApp(services);

        AssemblyLoadContextExtensionLoader.MapExtensionEndpoints(app);

        Assert.Equal(["Unattributed"], log);
    }

    /// <summary>
    /// Builds an application whose contributors are attributed to extensions declaring the given
    /// phases, mirroring how the loader records implementation type names at registration.
    /// </summary>
    private static WebApplication AppWith(List<string> log)
    {
        var services = new ServiceCollection();
        IEndpointContributor pre = new PreAuthContributor(log);
        IEndpointContributor post = new PostAuthContributor(log);
        services.AddSingleton(pre);
        services.AddSingleton(post);

        var loader = Substitute.For<IExtensionLoader>();
        loader.GetLoaded().Returns(new List<LoadedExtension>
        {
            Extension("pre", ExtensionEndpointPhase.BeforeAuthentication, pre.GetType().FullName!),
            Extension("post", ExtensionEndpointPhase.AfterAuthentication, post.GetType().FullName!),
        });
        services.AddSingleton(loader);

        return BuildApp(services);
    }

    private static LoadedExtension Extension(string id, ExtensionEndpointPhase phase, string typeName) => new()
    {
        ExtensionId = id,
        Name = id,
        Version = "1.0.0",
        DirectoryPath = "/tmp/" + id,
        EntryAssemblyPath = "/tmp/" + id + "/x.dll",
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        EndpointPhase = phase,
        RegisteredImplementationTypes = [typeName],
    };

    private sealed class PreAuthContributor(List<string> log) : IEndpointContributor
    {
        public void MapEndpoints(WebApplication app) => log.Add("Pre");
    }

    private sealed class PostAuthContributor(List<string> log) : IEndpointContributor
    {
        public void MapEndpoints(WebApplication app) => log.Add("Post");
    }

    private static WebApplication BuildApp(ServiceCollection services)
    {
        var builder = WebApplication.CreateBuilder();
        foreach (var descriptor in services)
            builder.Services.Add(descriptor);
        return builder.Build();
    }

    private sealed class RecordingContributor(string name, int order, List<string> log) : IEndpointContributor
    {
        public int Order => order;

        public void MapEndpoints(WebApplication app) => log.Add(name);
    }

    private sealed class DefaultOrderContributor : IEndpointContributor
    {
        public void MapEndpoints(WebApplication app)
        {
        }
    }
}
