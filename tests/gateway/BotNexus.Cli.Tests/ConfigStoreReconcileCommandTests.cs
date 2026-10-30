using System.CommandLine;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[Collection("AnsiConsole")]
public sealed class ConfigStoreReconcileCommandTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-reconcile-cli-{Guid.NewGuid():N}");
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _output = new();
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);

    public ConfigStoreReconcileCommandTests()
    {
        Directory.CreateDirectory(_home);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output), Interactive = InteractionSupport.No
        });
    }

    private async Task<int> InvokeAsync(bool apply)
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new ConfigCommands(new ConfigPathResolver()).Build(verbose, target));
        var arguments = new List<string> { "config", "store", "reconcile", "--target", _home, "--verbose" };
        if (apply)
            arguments.Add("--apply");
        return await root.InvokeAsync(arguments.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_DefaultPreviewOrExplicitApply_RespectsWriteBoundary(bool apply)
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse("{\"private-key\":\"sql-secret\"}").ShouldBeOfType<JsonObject>());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        await File.WriteAllTextAsync(ConfigPath, "{\"private-key\":\"file-secret\",\"preserved\":null}");
        var before = File.ReadAllBytes(ConfigPath);
        (await InvokeAsync(apply)).ShouldBe(0);
        var output = _output.ToString();
        output.ShouldContain(apply ? "Reconciliation: applied" : "Reconciliation: preview");
        if (!apply)
            output.ShouldContain("WAL/SHM coordination");
        output.ShouldNotContain("private-key");
        output.ShouldNotContain("sql-secret");
        output.ShouldNotContain("file-secret");
        output.ShouldNotContain(_home);
        if (apply)
        {
            (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
        }
        else
        {
            File.ReadAllBytes(ConfigPath).ShouldBe(before);
            Directory.GetDirectories(_home).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_NoStore_RefusesWithoutCreatingAnything(bool apply)
    {
        (await InvokeAsync(apply)).ShouldBe(1);
        _output.ToString().ShouldContain("Reconciliation: refused");
        Directory.EnumerateFileSystemEntries(_home).ShouldBeEmpty();
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, recursive: true);
        _output.Dispose();
    }
}
