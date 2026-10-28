using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Plugins.Api.Tests;

public sealed class MarketplaceEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-marketplace-api", Guid.NewGuid().ToString("N"));
    private MarketplaceRegistryService Registry => new(Path.Combine(_root, "config.json"), new FileSystem());

    [Fact]
    public async Task Crud_ReturnsTypedPersistedRowsAndStatusCodes()
    {
        var request = new MarketplaceRegistrationRequest("https://example.test/catalog.git", "main");
        var added = (await PluginsEndpointContributor.AddMarketplace(new("community", request.Source, request.Reference), Registry, default))
            .ShouldBeOfType<Created<MarketplaceRegistrationInfo>>();
        added.Value.ShouldNotBeNull().Id.ShouldBe("community");
        added.Location.ShouldBe("/api/plugins/marketplaces/community");
        var duplicate = await PluginsEndpointContributor.AddMarketplace(new("community", request.Source, null), Registry, default);
        Status(duplicate).ShouldBe(409);
        var updated = (await PluginsEndpointContributor.UpdateMarketplace("community", new(request.Source, null), Registry, default))
            .ShouldBeOfType<Ok<MarketplaceRegistrationInfo>>();
        updated.Value.ShouldNotBeNull().Reference.ShouldBeNull();
        Status(await PluginsEndpointContributor.SetMarketplaceEnabled("community", false, Registry, default)).ShouldBe(200);
        (await Registry.GetAsync(MarketplaceId.From("community"))).Enabled.ShouldBeFalse();
        Status(await PluginsEndpointContributor.SetMarketplaceEnabled("community", true, Registry, default)).ShouldBe(200);
        Status(await PluginsEndpointContributor.GetMarketplace("community", Registry, default)).ShouldBe(200);
        (await PluginsEndpointContributor.ListMarketplaces(Registry, default)).ShouldBeOfType<Ok<IReadOnlyList<MarketplaceRegistrationInfo>>>()
            .Value.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe("community");
        Status(await PluginsEndpointContributor.RemoveMarketplace("community", Registry, default)).ShouldBe(204);
        Status(await PluginsEndpointContributor.GetMarketplace("community", Registry, default)).ShouldBe(404);
        Status(await PluginsEndpointContributor.UpdateMarketplace("missing", request, Registry, default)).ShouldBe(404);
        Status(await PluginsEndpointContributor.SetMarketplaceEnabled("missing", false, Registry, default)).ShouldBe(404);
        Status(await PluginsEndpointContributor.RemoveMarketplace("missing", Registry, default)).ShouldBe(404);
    }

    [Theory]
    [InlineData("../escape", "https://example.test/catalog.git")]
    [InlineData("UPPER", "https://example.test/catalog.git")]
    [InlineData("community", "https://user:password@example.test/catalog.git")]
    [InlineData("community", "file:///catalog")]
    public async Task InvalidInput_IsBadRequestWithoutRegistration(string id, string source)
    {
        var result = await PluginsEndpointContributor.AddMarketplace(new(id, source, null), Registry, default);
        Status(result).ShouldBe(400);
        (await Registry.ListAsync()).ShouldBeEmpty();
        Status(await PluginsEndpointContributor.GetMarketplace("../escape", Registry, default)).ShouldBe(400);
    }

    [Fact]
    public async Task ServiceComposition_UsesConfigurationHomeNotWritableDataRoot()
    {
        var fileSystem = new FileSystem();
        var dataRoot = Path.Combine(_root, "data");
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fileSystem);
        services.AddSingleton(new BotNexusHome(fileSystem, Path.Combine(_root, "different-home"), dataRoot));
        services.AddSingleton(ConfigWriterFactory.Create(Path.Combine(_root, "config.json"), fileSystem));
        new PluginLifecycleServiceContributor().ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MarketplaceRegistryService>().AddAsync(
            MarketplaceId.From("community"), "https://example.test/catalog.git", null);
        (await Registry.ListAsync()).ShouldHaveSingleItem().Id.ShouldBe("community");
        File.Exists(Path.Combine(dataRoot, "config.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task StaticCollectionRoutes_AreSeparateFromInstalledPluginRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        new PluginLifecycleServiceContributor().ConfigureServices(builder.Services);
        await using var app = builder.Build();
        new PluginsEndpointContributor().MapEndpoints(app);
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .Select(x => (Route: x.RoutePattern.RawText, Methods: x.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods)).ToArray();
        routes.ShouldContain(x => x.Route == "/api/plugins/marketplaces/" && x.Methods != null && x.Methods.Contains("GET"));
        routes.ShouldContain(x => x.Route == "/api/plugins/marketplaces/{id}" && x.Methods != null && x.Methods.Contains("DELETE"));
        // A one-segment static marketplace URL must not fall into installed-plugin handlers for writes.
        routes.ShouldContain(x => x.Route == "/api/plugins/marketplaces/" && x.Methods != null && x.Methods.Contains("DELETE"));
    }

    [Fact]
    public async Task HttpBinding_ValidatesMissingFieldsAndReservesInstalledPluginWrites()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IFileSystem>(new FileSystem());
        builder.Services.AddSingleton(new BotNexusHome(new FileSystem(), _root, Path.Combine(_root, "data")));
        builder.Services.AddSingleton(ConfigWriterFactory.Create(Path.Combine(_root, "config.json"), new FileSystem()));
        new PluginLifecycleServiceContributor().ConfigureServices(builder.Services);
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use((context, next) =>
        {
            context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = "operator" };
            return next(context);
        });
        new PluginsEndpointContributor().MapEndpoints(app);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        addresses.ShouldNotBeNull();
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.ShouldHaveSingleItem()) };
        foreach (var body in new object[] { new { source = "https://example.test/catalog.git" }, new { id = "community" }, new { } })
        {
            using var rejected = await http.PostAsJsonAsync("/api/plugins/marketplaces", body);
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        using var added = await http.PostAsJsonAsync("/api/plugins/marketplaces", new MarketplaceAddRequest("update-preference", "https://example.test/catalog.git"));
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var updated = await http.PutAsJsonAsync("/api/plugins/marketplaces/update-preference", new MarketplaceRegistrationRequest("https://example.test/replaced.git"));
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Registry.GetAsync(MarketplaceId.From("update-preference"))).Source.ShouldBe("https://example.test/replaced.git");
        using var missingSource = await http.PutAsJsonAsync("/api/plugins/marketplaces/update-preference", new { });
        missingSource.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var deleted = await http.DeleteAsync("/api/plugins/marketplaces");
        deleted.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        foreach (var verb in new[] { "update", "pin", "unpin" })
        {
            using var reserved = await http.PostAsync($"/api/plugins/marketplaces/{verb}", null);
            reserved.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }
        (await Registry.ListAsync()).ShouldHaveSingleItem().Id.ShouldBe("update-preference");
        await app.StopAsync();
    }

    [Fact]
    public void Manifest_DeclaresWorldOnlyConfigurationScope()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "extensions")))
            directory = directory.Parent;
        directory.ShouldNotBeNull();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "src", "extensions", "BotNexus.Extensions.Plugins.Api", "botnexus-extension.json")));
        manifest.RootElement.GetProperty("configurationScopes").EnumerateArray().Select(x => x.GetString()).ShouldBe(["world"]);
        manifest.RootElement.GetProperty("configSchema").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Theory]
    [InlineData("operator", false, false, true)]
    [InlineData("operator", true, false, true)]
    [InlineData("restricted", false, true, false)]
    [InlineData("restricted-admin", true, true, false)]
    [InlineData("satellite:remote", true, false, false)]
    [InlineData(null, false, false, false)]
    public async Task GlobalRoutes_RequireUnrestrictedOperatorEvenWithAllowedAgentQuery(
        string? callerId, bool admin, bool restricted, bool allowed)
    {
        await Registry.AddAsync(MarketplaceId.From("community"), "https://example.test/original.git", null);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IFileSystem>(new FileSystem());
        builder.Services.AddSingleton(new BotNexusHome(new FileSystem(), _root, Path.Combine(_root, "data")));
        builder.Services.AddSingleton(ConfigWriterFactory.Create(Path.Combine(_root, "config.json"), new FileSystem()));
        new PluginLifecycleServiceContributor().ConfigureServices(builder.Services);
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use((context, next) =>
        {
            if (callerId is not null)
                context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity
                { CallerId = callerId, IsAdmin = admin, AllowedAgents = restricted ? ["allowed"] : [] };
            return next(context);
        });
        new PluginsEndpointContributor().MapEndpoints(app);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().ShouldNotBeNull();
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.ShouldHaveSingleItem()) };
        foreach (var query in new[] { "", "?agent=allowed", "?agentId=allowed" })
        {
            foreach (var (method, path, expected) in new[]
            {
                (HttpMethod.Get, "", HttpStatusCode.OK),
                (HttpMethod.Get, "/community", HttpStatusCode.OK),
                (HttpMethod.Post, "", HttpStatusCode.Conflict),
                (HttpMethod.Put, "/community", HttpStatusCode.OK),
                (HttpMethod.Post, "/community/disable", HttpStatusCode.OK),
                (HttpMethod.Post, "/community/enable", HttpStatusCode.OK),
                (HttpMethod.Delete, "/missing", HttpStatusCode.NotFound)
            })
            {
                using var request = new HttpRequestMessage(method, "/api/plugins/marketplaces" + path + query);
                if (method == HttpMethod.Put || method == HttpMethod.Post && path.Length == 0)
                    request.Content = JsonContent.Create(new MarketplaceAddRequest("community", "https://example.test/original.git"));
                using var response = await http.SendAsync(request);
                response.StatusCode.ShouldBe(allowed ? expected : HttpStatusCode.Forbidden);
            }
            if (!allowed)
            {
                using var create = await http.PostAsJsonAsync("/api/plugins/marketplaces" + query,
                    new MarketplaceAddRequest("unauthorized", "https://example.test/unauthorized.git"));
                create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                using var replace = await http.PutAsJsonAsync("/api/plugins/marketplaces/community" + query,
                    new MarketplaceRegistrationRequest("https://example.test/unauthorized.git"));
                replace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                using var remove = await http.DeleteAsync("/api/plugins/marketplaces/community" + query);
                remove.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }
        }
        var row = (await Registry.ListAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe("community");
        row.Source.ShouldBe("https://example.test/original.git");
        row.Enabled.ShouldBeTrue();
        await app.StopAsync();
    }

    [Theory]
    [InlineData("null", "marketplaces")]
    [InlineData("{\"valid\":{\"source\":\"https://example.test/valid.git\"},\"broken\":null}", "marketplaces.broken")]
    [InlineData("{\"valid\":{\"source\":\"https://example.test/valid.git\"},\"broken\":{}}", "marketplaces.broken.source")]
    public async Task MalformedRegistry_ReturnsStructuredConfigurationDiagnostic(string marketplaces, string path)
    {
        var root = JsonNode.Parse("{\"version\":1,\"world\":{\"extensions\":{\"botnexus-plugins-api\":{}}}}").ShouldNotBeNull().AsObject();
        root["world"].ShouldNotBeNull()["extensions"].ShouldNotBeNull()[MarketplaceRegistryService.ExtensionId]
            .ShouldNotBeNull()["marketplaces"] = JsonNode.Parse(marketplaces);
        var store = ConfigStoreBootstrap.ResolveStorePath(Path.Combine(_root, "config.json"), new FileSystem());
        Directory.CreateDirectory(_root);
        await ConfigStoreBootstrap.PopulateAsync(store, root);
        ConfigStoreBootstrap.ReleaseConnections(store);
        foreach (var result in new[]
        {
            await PluginsEndpointContributor.ListMarketplaces(Registry, default),
            await PluginsEndpointContributor.GetMarketplace("valid", Registry, default),
            await PluginsEndpointContributor.SetMarketplaceEnabled("broken", true, Registry, default)
        })
        {
            Status(result).ShouldBe(409);
            var error = result.ShouldBeAssignableTo<IValueHttpResult>().Value.ShouldBeOfType<MarketplaceError>();
            error.Error.ShouldContain($"world.extensions.{MarketplaceRegistryService.ExtensionId}.{path}");
        }
    }

    private static int? Status(IResult result) => result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode;
    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(ConfigStoreBootstrap.ResolveStorePath(Path.Combine(_root, "config.json"), new FileSystem()));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
