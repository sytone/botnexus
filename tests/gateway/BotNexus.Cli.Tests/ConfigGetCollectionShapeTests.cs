using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[CollectionDefinition("Config get console", DisableParallelization = true)]
public sealed class ConfigGetConsoleCollection;

[Collection("Config get console")]
public sealed class ConfigGetCollectionShapeTests
{
    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("null", "null")]
    public async Task ExecuteGetAsync_SqliteOnly_PrintsPersistedArrayShape(string shape, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-cli-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var originalConsole = AnsiConsole.Console;
        try
        {
            var configPath = Path.Combine(directory, "config.json");
            var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
            var document = JsonNode.Parse("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + shape + "}}}")
                .ShouldBeOfType<JsonObject>();
            await ConfigStoreBootstrap.PopulateAsync(storePath, document);
            File.Exists(configPath).ShouldBeFalse();
            using var output = new StringWriter();
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output)
            });
            var command = new ConfigCommands(new ConfigPathResolver());
            (await command.ExecuteGetAsync("gateway.fileAccess.allowedReadPaths", configPath,
                verbose: false, CancellationToken.None)).ShouldBe(0);
            output.ToString().Trim().ShouldBe(expected);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            ConfigStoreBootstrap.ReleaseConnections(ConfigStoreBootstrap.ResolveStorePath(
                Path.Combine(directory, "config.json"), new FileSystem()));
            Directory.Delete(directory, recursive: true);
        }
    }
}
