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
    public void Cli_project_keeps_the_extension_dependency_on_the_far_side_of_http()
    {
        var project = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj")));

        Assert.DoesNotContain("src\\extensions", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src/extensions", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BotNexus.Extensions.Plugins", project, StringComparison.OrdinalIgnoreCase);
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
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
