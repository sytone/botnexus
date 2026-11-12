using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Cli.Commands;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[Collection("AnsiConsole")]
public sealed class PluginPartialSupportOutputTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("get")]
    [InlineData("list")]
    public async Task StructuredOutput_PreservesPartialSupportWarnings(string operation)
    {
        const string row = """{"name":"mixed","skillsOnly":true,"skippedComponents":["agents","mcp"],"warnings":["Skills-only support: foreign components remain inactive.","Skipped agents: preserved as inactive content (partial support)."]}""";
        var payload = operation == "list" ? "[" + row + "]" : row;
        using var http = new HttpClient(new ResponseHandler(payload)) { BaseAddress = new Uri("http://localhost:5000/") };
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new PluginCommands(http).Build(verbose, target));
        var original = AnsiConsole.Console;
        using var output = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output), Interactive = InteractionSupport.No,
        });
        try
        {
            var args = operation == "install" ? new[] { "plugin", operation, "https://example.invalid/repo.git" }
                : operation == "list" ? ["plugin", operation] : ["plugin", operation, "mixed"];
            (await root.InvokeAsync(args)).ShouldBe(0);
            using var json = JsonDocument.Parse(output.ToString());
            var receipt = operation == "list" ? json.RootElement[0] : json.RootElement;
            receipt.GetProperty("skillsOnly").GetBoolean().ShouldBeTrue();
            receipt.GetProperty("skippedComponents")[0].GetString().ShouldBe("agents");
            receipt.GetProperty("warnings")[0].GetString().ShouldBe("Skills-only support: foreign components remain inactive.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private sealed class ResponseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
    }
}
