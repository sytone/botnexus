using System.CommandLine;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[Collection("AnsiConsole")]
public sealed class ConfigStoreParityCommandTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-parity-cli-{Guid.NewGuid():N}");
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _output = new();
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);

    public ConfigStoreParityCommandTests()
    {
        Directory.CreateDirectory(_home);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Interactive = InteractionSupport.No
        });
        AnsiConsole.Console.Profile.Width = 300;
    }

    private async Task<int> InvokeAsync()
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new ConfigCommands(new ConfigPathResolver()).Build(verbose, target));
        return await root.InvokeAsync(["config", "store", "parity", "--target", _home, "--verbose"]);
    }

    private async Task SeedAsync(string sql, string? compatibility)
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse(sql).ShouldBeOfType<JsonObject>());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        if (compatibility is not null)
            await File.WriteAllTextAsync(ConfigPath, compatibility);
    }

    [Fact]
    public async Task Parity_TargetedClean_ReturnsZeroAndDoesNotMutate()
    {
        await SeedAsync("{\"x\":null}", "{\"x\":null}");
        var before = File.ReadAllBytes(ConfigPath);
        (await InvokeAsync()).ShouldBe(0);
        _output.ToString().ShouldContain("Parity: clean");
        _output.ToString().ShouldContain("WAL/SHM coordination");
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
    }

    [Fact]
    public async Task Parity_Drift_ReturnsTwoAndNeverPrintsKeysValuesOrTargetEvenWhenVerbose()
    {
        await SeedAsync("{\"[red]dynamic-secret-key[/]\":\"sql-secret\",\"onlySql\":null,\"changed\":1}",
            "{\"[red]dynamic-secret-key[/]\":\"file-secret\",\"onlyFile\":{},\"changed\":2}");
        (await InvokeAsync()).ShouldBe(2);
        var output = _output.ToString();
        output.ShouldContain("Parity: drift");
        output.ShouldContain("SQL-only: 1");
        output.ShouldContain("Compatibility-only: 1");
        output.ShouldContain("Changed: 2");
        output.ShouldNotContain("dynamic-secret-key");
        output.ShouldNotContain("sql-secret");
        output.ShouldNotContain("file-secret");
        output.ShouldNotContain("onlySql");
        output.ShouldNotContain("onlyFile");
        output.ShouldNotContain(_home);
    }

    [Theory]
    [InlineData(false, false, "absent")]
    [InlineData(false, true, "disabled")]
    [InlineData(true, false, "sql-only")]
    public async Task Parity_UnavailableComparison_ReturnsThree(bool sql, bool compatibility, string status)
    {
        if (sql)
            await SeedAsync("{}", null);
        if (compatibility)
            await File.WriteAllTextAsync(ConfigPath, "{}");
        (await InvokeAsync()).ShouldBe(3);
        _output.ToString().ShouldContain($"Parity: {status}");
        _output.ToString().ShouldContain("not compared");
        File.Exists(StorePath).ShouldBe(sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parity_UnreadableBackend_ReturnsOneWithoutExceptionPayload(bool sql)
    {
        await SeedAsync("{}", "{}");
        await File.WriteAllTextAsync(sql ? StorePath : ConfigPath, "private-payload-4117");
        (await InvokeAsync()).ShouldBe(1);
        var output = _output.ToString();
        output.ShouldContain("Parity: unreadable");
        output.ShouldContain(sql ? "SQL: unreadable" : "Compatibility: unreadable");
        output.ShouldNotContain("private-payload-4117");
        output.ShouldNotContain(_home);
        output.ShouldNotContain("Exception");
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, recursive: true);
        _output.Dispose();
    }
}
