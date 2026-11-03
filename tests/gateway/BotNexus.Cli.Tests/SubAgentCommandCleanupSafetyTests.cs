using System.CommandLine;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

/// <summary>Exercises the generated offline handlers without ambient stores or physical workspace access.</summary>
public sealed class SubAgentCommandCleanupSafetyTests
{
    public static IEnumerable<object[]> ReadFailures()
    {
        foreach (var error in new[] { "invalid", "access", "io" })
        foreach (var route in new[] { "prune", "dry-run", "list" })
        foreach (var sessionsExist in new[] { false, true })
            yield return [error, route, sessionsExist];
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task Handler_ConfigReadFails_RejectsWithoutDeleting(string error, string route, bool sessionsExist)
    {
        using var fixture = new CleanupCommandFixture();
        if (sessionsExist) fixture.CreateSessions();
        var before = fixture.WorkspaceSnapshot();
        var reads = 0;
        var sessionReads = 0;
        Exception failure = error switch
        {
            "invalid" => new InvalidDataException("Invalid authority"),
            "access" => new UnauthorizedAccessException("Authority access denied"),
            _ => new IOException("Authority read failed")
        };
        var command = new SubAgentCommand(fixture.Workspaces, (path, _) =>
        {
            reads++;
            path.ShouldBe(fixture.ConfigPath);
            throw failure;
        }, _ =>
        {
            sessionReads++;
            return fixture.SessionsPath;
        });

        var result = await fixture.InvokeAsync(command, route);

        result.ShouldNotBe(0);
        reads.ShouldBe(1);
        sessionReads.ShouldBe(0, "configuration must succeed before any cleanup/session planning");
        fixture.WorkspaceSnapshot().ShouldBe(before, "zero directories or sentinel bytes may change");
    }

    [Theory]
    [InlineData("prune", false)]
    [InlineData("prune", true)]
    [InlineData("dry-run", false)]
    [InlineData("dry-run", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    public async Task Handler_CorruptSqliteWithValidJson_RejectsWithoutFallback(string route, bool sessionsExist)
    {
        using var fixture = new CleanupCommandFixture();
        if (sessionsExist) fixture.CreateSessions();
        var storePath = ConfigStoreBootstrap.ResolveStorePath(fixture.ConfigPath, fixture.Physical);
        File.WriteAllBytes(storePath, [1, 2, 3, 4, 5]);
        File.WriteAllText(fixture.ConfigPath, fixture.CustomDocument().ToJsonString());
        var authorityBefore = File.ReadAllBytes(storePath);
        var jsonBefore = File.ReadAllBytes(fixture.ConfigPath);
        var before = fixture.WorkspaceSnapshot();
        var reads = 0;
        var sessionReads = 0;
        Exception? authorityFailure = null;
        var command = new SubAgentCommand(fixture.Workspaces, (path, _) =>
        {
            reads++;
            path.ShouldBe(fixture.ConfigPath);
            try
            {
                // Exercise physical corruption through the production canonical accessor, not a
                // synthetic failure. Capture only that read's exception and preserve its stack.
                return PlatformConfigAccessor.Shared.Get(path, fixture.Physical);
            }
            catch (Exception failure)
            {
                authorityFailure = failure;
                throw;
            }
        }, _ =>
        {
            sessionReads++;
            return fixture.SessionsPath;
        });

        var result = await fixture.InvokeAsync(command, route);

        result.ShouldNotBe(0);
        reads.ShouldBe(1, "the actual handler must attempt the canonical configuration read exactly once");
        var corruption = authorityFailure.ShouldBeOfType<SqliteException>(
            "a bootstrap, identity or setup failure must not masquerade as physical SQLite corruption");
        corruption.SqliteErrorCode.ShouldBe(26, "SQLITE_NOTADB: the authority bytes are not a database");
        corruption.SqliteExtendedErrorCode.ShouldBe(26);
        sessionReads.ShouldBe(0, "physical authority corruption must stop the handler before session resolution");
        fixture.WorkspaceSnapshot().ShouldBe(before);
        File.ReadAllBytes(storePath).ShouldBe(authorityBefore);
        File.ReadAllBytes(fixture.ConfigPath).ShouldBe(jsonBefore);
    }

    [Theory]
    [InlineData("missing", "prune")]
    [InlineData("null", "prune")]
    [InlineData("whitespace", "prune")]
    [InlineData("missing", "dry-run")]
    [InlineData("null", "list")]
    public async Task Handler_SuccessfulOptionalRootRead_UsesDefault(string rootState, string route)
    {
        using var fixture = new CleanupCommandFixture();
        fixture.CreateSessions();
        var document = rootState switch
        {
            "null" => JsonNode.Parse("{\"gateway\":{\"subAgents\":{\"workspaceRoot\":null}}}") as JsonObject,
            "whitespace" => JsonNode.Parse("{\"gateway\":{\"subAgents\":{\"workspaceRoot\":\"  \"}}}") as JsonObject,
            _ => new JsonObject()
        };
        document.ShouldNotBeNull();
        await fixture.SeedAuthorityAsync(document);
        // A misleading projection must not replace an initialized, even empty, authority.
        File.WriteAllText(fixture.ConfigPath, fixture.CustomDocument().ToJsonString());
        var before = fixture.WorkspaceSnapshot();
        var command = fixture.Command();
        command.ResolveWorkspaceRoot(fixture.Home).ShouldBe(fixture.DefaultRoot);

        (await fixture.InvokeAsync(command, route)).ShouldBe(0);

        if (route == "prune")
        {
            fixture.Workspaces.Directory.Exists(fixture.Workspaces.Path.Combine(fixture.DefaultRoot, "orphan")).ShouldBeFalse();
            fixture.AssertSentinel(fixture.CustomRoot, "orphan");
        }
        else fixture.WorkspaceSnapshot().ShouldBe(before);
    }

    [Theory]
    [InlineData("prune")]
    [InlineData("dry-run")]
    [InlineData("list")]
    public async Task Handler_HealthyCustomAuthority_ProtectsDefaultRunningAndUnknown(string route)
    {
        using var fixture = new CleanupCommandFixture();
        fixture.CreateSessions(("running", "Active"), ("unknown", "FutureStatus"), ("terminal", "Completed"));
        fixture.AddSentinel(fixture.CustomRoot, "running");
        fixture.AddSentinel(fixture.CustomRoot, "unknown");
        fixture.AddSentinel(fixture.CustomRoot, "terminal");
        await fixture.SeedAuthorityAsync(fixture.CustomDocument());
        var before = fixture.WorkspaceSnapshot();
        var command = fixture.Command();
        command.ResolveWorkspaceRoot(fixture.Home).ShouldBe(fixture.CustomRoot);

        (await fixture.InvokeAsync(command, route)).ShouldBe(0);

        fixture.AssertSentinel(fixture.DefaultRoot, "orphan");
        fixture.AssertSentinel(fixture.CustomRoot, "running");
        fixture.AssertSentinel(fixture.CustomRoot, "unknown");
        if (route == "prune")
        {
            fixture.Workspaces.Directory.Exists(fixture.Workspaces.Path.Combine(fixture.CustomRoot, "orphan")).ShouldBeFalse();
            fixture.Workspaces.Directory.Exists(fixture.Workspaces.Path.Combine(fixture.CustomRoot, "terminal")).ShouldBeFalse();
        }
        else fixture.WorkspaceSnapshot().ShouldBe(before);
    }
}

// Physical configuration and sessions are confined to a GUID home. All workspace I/O uses a mock
// filesystem, including the default temp root: even a broken fallback cannot touch OS workspaces.
internal sealed class CleanupCommandFixture : IDisposable
{
    internal string Home { get; } = Path.Combine(Path.GetTempPath(), "botnexus-cleanup-safety-" + Guid.NewGuid().ToString("N"));
    internal IFileSystem Physical { get; } = new FileSystem();
    internal MockFileSystem Workspaces { get; } = new();
    internal string ConfigPath => Path.Combine(Home, "config.json");
    internal string SessionsPath => Path.Combine(Home, "isolated-sessions.sqlite");
    internal string DefaultRoot => Workspaces.Path.Combine(Workspaces.Path.GetTempPath(), SubAgentWorkspaceRootResolver.DefaultDirectoryName);
    internal string CustomRoot => Workspaces.Path.Combine(Workspaces.Path.GetTempPath(), "custom-cleanup-" + Path.GetFileName(Home));

    internal CleanupCommandFixture()
    {
        Directory.CreateDirectory(Home);
        AddSentinel(DefaultRoot, "orphan");
        AddSentinel(CustomRoot, "orphan");
    }

    internal SubAgentCommand Command() => new(Workspaces,
        (path, _) => PlatformConfigAccessor.Shared.Get(path, Physical), _ => SessionsPath);

    internal JsonObject CustomDocument() => new()
    {
        ["gateway"] = new JsonObject { ["subAgents"] = new JsonObject { ["workspaceRoot"] = CustomRoot } }
    };

    internal async Task SeedAuthorityAsync(JsonObject document)
    {
        var storePath = ConfigStoreBootstrap.ResolveStorePath(ConfigPath, Physical);
        await ConfigStoreBootstrap.PopulateAsync(storePath, document);
        File.Exists(storePath).ShouldBeTrue();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM config_entries";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBeGreaterThanOrEqualTo(0);
    }

    internal void CreateSessions(params (string Agent, string Status)[] statuses)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SessionsPath, Pooling = false
        }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE sub_agent_sessions (child_agent_id TEXT, status TEXT)";
        create.ExecuteNonQuery();
        foreach (var (agent, status) in statuses)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO sub_agent_sessions VALUES ($agent, $status)";
            insert.Parameters.AddWithValue("$agent", agent);
            insert.Parameters.AddWithValue("$status", status);
            insert.ExecuteNonQuery();
        }
    }

    internal void AddSentinel(string root, string agent)
    {
        var workspace = Workspaces.Path.Combine(root, agent, "workspace");
        Workspaces.Directory.CreateDirectory(workspace);
        Workspaces.File.WriteAllBytes(Workspaces.Path.Combine(workspace, "sentinel.bin"), [0, 7, 42, 255]);
    }

    internal void AssertSentinel(string root, string agent) => Workspaces.File.ReadAllBytes(
        Workspaces.Path.Combine(root, agent, "workspace", "sentinel.bin")).ShouldBe(new byte[] { 0, 7, 42, 255 });

    internal string[] WorkspaceSnapshot() => Workspaces.AllDirectories.Select(path => "directory:" + path)
        .Concat(Workspaces.AllFiles.Select(path => "file:" + path + ":" + Convert.ToHexString(Workspaces.File.ReadAllBytes(path))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal Task<int> InvokeAsync(SubAgentCommand command, string route)
    {
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(target);
        root.AddCommand(command.Build(target));
        var args = new List<string> { "--target", Home, "subagent", "workspace", route == "dry-run" ? "prune" : route };
        if (route == "dry-run") args.Add("--dry-run");
        return root.InvokeAsync(args.ToArray());
    }

    public void Dispose()
    {
        var storePath = ConfigStoreBootstrap.ResolveStorePath(ConfigPath, Physical);
        if (File.Exists(storePath)) ConfigStoreBootstrap.ReleaseConnections(storePath);
        Directory.Delete(Home, recursive: true);
    }
}
