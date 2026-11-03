using System.CommandLine;
using System.Net;
using System.Text;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Pins the gateway-only CLI boundary for plugin lifecycle operations (#4150). The CLI sends wire
/// requests and deliberately has no compile-time dependency on the plugin extension assembly.
/// </summary>
public sealed class PluginCommandsTests
{
    [Theory]
    [InlineData("update", "alpha beta", "POST", "api/plugins/alpha%20beta/update", null)]
    [InlineData("remove", "alpha beta", "DELETE", "api/plugins/alpha%20beta", null)]
    [InlineData("pin", "alpha beta", "POST", "api/plugins/alpha%20beta/pin", null)]
    [InlineData("unpin", "alpha beta", "POST", "api/plugins/alpha%20beta/unpin", null)]
    public async Task Named_operations_forward_to_the_gateway_api(
        string operation,
        string name,
        string method,
        string path,
        string? expectedBody)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var root = BuildRoot(new PluginCommands(http));

        var exitCode = await root.InvokeAsync(["plugin", operation, name]);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method.Method);
        Assert.Equal(path, request.RequestUri!.GetComponents(UriComponents.Path, UriFormat.UriEscaped));
        Assert.Equal(expectedBody, request.Body);
    }

    [Fact]
    public async Task Install_forwards_source_reference_and_pin_preference_as_json()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var root = BuildRoot(new PluginCommands(http));

        var exitCode = await root.InvokeAsync([
            "plugin", "install", "https://example.test/plugins/alpha.git",
            "--reference", "release/1", "--pin"]);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("api/plugins", request.RequestUri!.GetComponents(UriComponents.Path, UriFormat.UriEscaped));
        Assert.NotNull(request.Body);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body);
        Assert.Equal("https://example.test/plugins/alpha.git", body.RootElement.GetProperty("source").GetString());
        Assert.Equal("release/1", body.RootElement.GetProperty("reference").GetString());
        Assert.False(body.RootElement.GetProperty("updatesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Install_forwards_explicit_marketplace_selection_without_changing_source()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var root = BuildRoot(new PluginCommands(http));

        var exitCode = await root.InvokeAsync([
            "plugin", "install", "https://example.test/catalog.git",
            "--marketplace-plugin", "chosen", "--reference", "release", "--pin"]);

        exitCode.ShouldBe(0);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        var payload = request.Body ?? throw new InvalidOperationException("missing install body");
        using var body = System.Text.Json.JsonDocument.Parse(payload);
        body.RootElement.GetProperty("source").GetString().ShouldBe("https://example.test/catalog.git");
        body.RootElement.GetProperty("marketplacePluginName").GetString().ShouldBe("chosen");
        body.RootElement.GetProperty("reference").GetString().ShouldBe("release");
        body.RootElement.GetProperty("updatesEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Cli_project_keeps_the_extension_dependency_on_the_far_side_of_http()
    {
        var project = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj")));

        Assert.DoesNotContain("src\\extensions", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src/extensions", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BotNexus.Extensions.Plugins", project, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("list", "GET", "api/plugins/marketplaces")]
    [InlineData("get", "GET", "api/plugins/marketplaces/community")]
    [InlineData("remove", "DELETE", "api/plugins/marketplaces/community")]
    [InlineData("enable", "POST", "api/plugins/marketplaces/community/enable")]
    [InlineData("disable", "POST", "api/plugins/marketplaces/community/disable")]
    public async Task Marketplace_commands_forward_to_registry_routes(string operation, string method, string path)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var root = BuildRoot(new PluginCommands(http));
        var arguments = operation == "list" ? new[] { "plugin", "marketplace", operation } : ["plugin", "marketplace", operation, "community"];
        (await root.InvokeAsync(arguments)).ShouldBe(0);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.Method.ShouldBe(method);
        request.RequestUri.ShouldNotBeNull().GetComponents(UriComponents.Path, UriFormat.UriEscaped).ShouldBe(path);
        request.Body.ShouldBeNull();
    }

    [Theory]
    [InlineData("add", "POST")]
    [InlineData("update", "PUT")]
    public async Task Marketplace_writes_forward_typed_source_and_optional_ref(string operation, string method)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var root = BuildRoot(new PluginCommands(http));
        (await root.InvokeAsync(["plugin", "marketplace", operation, "community", "https://example.test/catalog.git", "--reference", "release/1"])).ShouldBe(0);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.Method.ShouldBe(method);
        request.RequestUri.ShouldNotBeNull().GetComponents(UriComponents.Path, UriFormat.UriEscaped)
            .ShouldBe(operation == "add" ? "api/plugins/marketplaces" : "api/plugins/marketplaces/community");
        using var body = System.Text.Json.JsonDocument.Parse(request.Body.ShouldNotBeNull());
        body.RootElement.GetProperty("source").GetString().ShouldBe("https://example.test/catalog.git");
        body.RootElement.GetProperty("reference").GetString().ShouldBe("release/1");
        if (operation == "add") body.RootElement.GetProperty("id").GetString().ShouldBe("community");
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("../escape")]
    [InlineData("two.words")]
    public async Task Marketplace_invalid_identity_is_rejected_before_transport(string id)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        (await BuildRoot(new PluginCommands(http)).InvokeAsync(["plugin", "marketplace", "get", id])).ShouldNotBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Marketplace_gateway_rejection_returns_nonzero()
    {
        var handler = new CapturingHandler { StatusCode = HttpStatusCode.BadRequest };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        (await BuildRoot(new PluginCommands(http)).InvokeAsync(["plugin", "marketplace", "add", "community", "https://user:password@example.test/catalog.git"])).ShouldBe(1);
        handler.Requests.ShouldHaveSingleItem();
    }

    private static RootCommand BuildRoot(PluginCommands commands)
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(commands.Build(verbose, target));
        return root;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(
                    "{\"outcome\":\"installed\",\"name\":\"alpha\",\"isSuccess\":true,\"errors\":[]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
