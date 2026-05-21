using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components.Config;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests.Components.Config;

public sealed class ExtensionsConfigPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void ShowsLoadedExtensions_WithNameAndVersion()
    {
        RegisterFakeExtensionsApi(new[]
        {
            BuildExtension("ext-a", "Test Extension", "1.0.0", enabled: true, types: ["channel"]),
        });

        var cut = _ctx.Render<ExtensionsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".ext-name").TextContent.ShouldBe("Test Extension");
            cut.Find(".ext-version").TextContent.ShouldContain("1.0.0");
        });
    }

    [Fact]
    public void ShowsDisabledBadge_WhenExtensionIsDisabled()
    {
        RegisterFakeExtensionsApi(new[]
        {
            BuildExtension("ext-b", "Disabled Ext", "2.0.0", enabled: false, types: []),
        });

        var cut = _ctx.Render<ExtensionsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".ext-badge--disabled").ShouldNotBeNull();
        });
    }

    [Fact]
    public void ShowsEmptyState_WhenNoExtensionsLoaded()
    {
        RegisterFakeExtensionsApi([]);

        var cut = _ctx.Render<ExtensionsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("No extensions loaded");
        });
    }

    [Fact]
    public void ShowsConfigSchema_WhenExtensionHasFields()
    {
        RegisterFakeExtensionsApi(new[]
        {
            BuildExtensionWithSchema("ext-c", "Schema Ext", "3.0.0", new[]
            {
                new { Id = "apiKey", Type = "string", Required = true, Sensitive = true, Default = (string?)null, Description = "API key" }
            })
        });

        var cut = _ctx.Render<ExtensionsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("apiKey");
            cut.Markup.ShouldContain("🔒"); // sensitive indicator
            cut.Markup.ShouldContain("API key");
        });
    }

    [Fact]
    public void ShowsErrorMessage_WhenApiCallFails()
    {
        RegisterFailingExtensionsApi();

        var cut = _ctx.Render<ExtensionsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Failed to load extensions");
        });
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private void RegisterFakeExtensionsApi(object[] extensions)
    {
        var json = JsonSerializer.Serialize(extensions);
        var handler = new FakeHttpHandler(json, HttpStatusCode.OK);
        _ctx.Services.AddSingleton<HttpClient>(_ => new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        });
    }

    private void RegisterFailingExtensionsApi()
    {
        var handler = new FakeHttpHandler("{}", HttpStatusCode.InternalServerError);
        _ctx.Services.AddSingleton<HttpClient>(_ => new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        });
    }

    private static object BuildExtension(string id, string name, string version, bool enabled, string[] types)
        => new
        {
            id,
            name,
            version,
            enabled,
            extensionTypes = types,
            registeredServices = Array.Empty<string>(),
            configSchema = Array.Empty<object>(),
            assemblyFileName = $"{id}.dll"
        };

    private static object BuildExtensionWithSchema(string id, string name, string version, dynamic[] schema)
        => new
        {
            id,
            name,
            version,
            enabled = true,
            extensionTypes = new[] { "tool" },
            registeredServices = Array.Empty<string>(),
            configSchema = schema,
            assemblyFileName = $"{id}.dll"
        };

    private sealed class FakeHttpHandler(string responseJson, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
    }
}
