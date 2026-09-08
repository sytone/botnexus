using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Both remaining CLI call sites that resolve <c>gateway.subAgents.workspaceRoot</c> must observe the
/// same effective configuration as the running gateway - JSON plus the SQLite store, store winning
/// (#3824).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these use a real filesystem.</b> The store is a SQLite database, so it cannot be seeded
/// into a <c>MockFileSystem</c>; proving that the store is actually consulted requires a real file.
/// The mock-backed tests in <see cref="SubAgentCommandWorkspaceRootTests"/> still pin the JSON-only
/// and no-config behaviour and are untouched.
/// </para>
/// <para>
/// <b>Why the values differ deliberately.</b> Today JSON is a complete redundant copy of the store,
/// so a test seeded from one document would pass against either source and prove nothing. Each case
/// below seeds a store value the JSON file does not contain, so a read that goes to the file is
/// distinguishable from one that goes through the pipeline.
/// </para>
/// </remarks>
public sealed class SubAgentConfigStoreVisibilityTests : IDisposable
{
    private readonly string _home;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public SubAgentConfigStoreVisibilityTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"botnexus-3824-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        _configPath = Path.Combine(_home, "config.json");
    }

    public void Dispose()
    {
        var storePath = ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);
        if (File.Exists(storePath))
            ConfigStoreBootstrap.ReleaseConnections(storePath);

        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best effort - a pooled handle must not fail the run */ }
    }

    private void WriteJsonWorkspaceRoot(string root)
        => File.WriteAllText(
            _configPath,
            $$"""{ "gateway": { "subAgents": { "workspaceRoot": {{ToJson(root)}} } } }""");

    private async Task SeedStoreWorkspaceRootAsync(string root)
    {
        var storePath = ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);
        var document = JsonNode.Parse(
            $$"""{ "gateway": { "subAgents": { "workspaceRoot": {{ToJson(root)}} } } }""")!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(storePath, document);
        File.Exists(storePath).ShouldBeTrue("seeding the store must create config.db");
    }

    private static string ToJson(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private string PathUnderHome(string leaf) => Path.Combine(_home, leaf);

    // ── subagent workspace list|prune ──

    [Fact]
    public async Task SubAgentCommand_WhenStoreDivergesFromJson_UsesStoreValue()
    {
        var jsonRoot = PathUnderHome("from-json");
        var storeRoot = PathUnderHome("from-store");
        WriteJsonWorkspaceRoot(jsonRoot);
        await SeedStoreWorkspaceRootAsync(storeRoot);

        var command = new SubAgentCommand(_fileSystem);

        command.ResolveWorkspaceRoot(_home).ShouldBe(
            Path.GetFullPath(storeRoot),
            "the store wins over config.json, exactly as it does in the gateway");
    }

    [Fact]
    public async Task SubAgentCommand_WhenConfigJsonIsAbsent_StillResolvesFromStore()
    {
        var storeRoot = PathUnderHome("store-only");
        await SeedStoreWorkspaceRootAsync(storeRoot);
        File.Exists(_configPath).ShouldBeFalse("this case is config.db without config.json (#3823)");

        var command = new SubAgentCommand(_fileSystem);

        command.ResolveWorkspaceRoot(_home).ShouldBe(
            Path.GetFullPath(storeRoot),
            "a missing config.json must not silently degrade to code defaults when a store exists");
    }

    // ── doctor: subagent-workspaces ──

    [Fact]
    public async Task DoctorCheck_WhenStoreDivergesFromJson_ReconcilesTheStoreRoot()
    {
        // The JSON root is empty (a check reading it would report Healthy); the store root holds an
        // orphan workspace (a check reading it must warn). The outcome names which source was read.
        var jsonRoot = PathUnderHome("doctor-from-json");
        var storeRoot = PathUnderHome("doctor-from-store");
        Directory.CreateDirectory(jsonRoot);
        Directory.CreateDirectory(Path.Combine(storeRoot, "orphan-agent"));
        WriteJsonWorkspaceRoot(jsonRoot);
        await SeedStoreWorkspaceRootAsync(storeRoot);

        var check = new SubAgentWorkspaceCheck(_fileSystem);

        var result = await check.RunAsync(
            new DoctorCheckContext(_configPath, _home, Verbose: false), CancellationToken.None);

        result.Outcome.ShouldBe(
            DoctorOutcome.Warning,
            "the check must reconcile the store's workspace root, not the file's");
        result.Summary.ShouldContain("reclaimable");
    }

    [Fact]
    public async Task DoctorCheck_WhenConfigJsonIsAbsent_StillReconcilesTheStoreRoot()
    {
        var storeRoot = PathUnderHome("doctor-store-only");
        Directory.CreateDirectory(Path.Combine(storeRoot, "orphan-agent"));
        await SeedStoreWorkspaceRootAsync(storeRoot);
        File.Exists(_configPath).ShouldBeFalse("this case is config.db without config.json (#3823)");

        var check = new SubAgentWorkspaceCheck(_fileSystem);

        var result = await check.RunAsync(
            new DoctorCheckContext(_configPath, _home, Verbose: false), CancellationToken.None);

        result.Outcome.ShouldBe(
            DoctorOutcome.Warning,
            "a doctor check that reports on configuration the gateway is not using is worse than none");
        result.Summary.ShouldContain("reclaimable");
    }
}
