using System.IO.Abstractions.TestingHelpers;
using BotNexus.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Tests for the on-demand sub-agent workspace reaper (#1942). Covers the happy path (a terminal
/// sub-agent workspace is listed and pruned), and the safety-critical sad paths (a running
/// sub-agent workspace is NEVER pruned, a non-existent root is handled gracefully, and a dry run
/// lists but deletes nothing).
/// </summary>
public sealed class SubAgentWorkspaceReaperTests
{
    private const string Root = @"C:\temp\botnexus-subagent-workspaces";

    private static MockFileSystem NewFsWithDirs(params string[] agentDirs)
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(Root);
        foreach (var dir in agentDirs)
        {
            var workspace = fs.Path.Combine(Root, dir, "workspace");
            fs.Directory.CreateDirectory(workspace);
            // Drop a bulky file inside so a real recursive delete has something to reclaim.
            fs.File.WriteAllText(fs.Path.Combine(workspace, "scratch.txt"), "data");
        }

        return fs;
    }

    [Fact]
    public void BuildPlan_TerminalRecord_IsMarkedPrunable()
    {
        var fs = NewFsWithDirs("agent--subagent--coder--abc");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>
        {
            ["agent--subagent--coder--abc"] = "Completed"
        });

        var entry = plan.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(SubAgentWorkspaceDisposition.Terminal);
        entry.IsPrunable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Killed")]
    [InlineData("TimedOut")]
    public void Prune_TerminalWorkspace_IsDeleted(string status)
    {
        var fs = NewFsWithDirs("agent--subagent--coder--abc");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>
        {
            ["agent--subagent--coder--abc"] = status
        });

        var deleted = reaper.Prune(plan, dryRun: false);

        deleted.ShouldBe(1);
        fs.Directory.Exists(fs.Path.Combine(Root, "agent--subagent--coder--abc")).ShouldBeFalse();
    }

    [Fact]
    public void BuildPlan_RunningRecord_IsNotPrunable()
    {
        var fs = NewFsWithDirs("agent--subagent--coder--live");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>
        {
            ["agent--subagent--coder--live"] = "Active"
        });

        var entry = plan.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(SubAgentWorkspaceDisposition.Running);
        entry.IsPrunable.ShouldBeFalse();
    }

    [Fact]
    public void Prune_RunningWorkspace_IsNeverDeleted()
    {
        var fs = NewFsWithDirs("agent--subagent--coder--live", "agent--subagent--coder--done");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>
        {
            ["agent--subagent--coder--live"] = "Active",
            ["agent--subagent--coder--done"] = "Completed"
        });

        var deleted = reaper.Prune(plan, dryRun: false);

        deleted.ShouldBe(1);
        // The running workspace survives; the terminal one is reclaimed.
        fs.Directory.Exists(fs.Path.Combine(Root, "agent--subagent--coder--live")).ShouldBeTrue();
        fs.Directory.Exists(fs.Path.Combine(Root, "agent--subagent--coder--done")).ShouldBeFalse();
    }

    [Fact]
    public void BuildPlan_NoMatchingRecord_IsOrphanAndPrunable()
    {
        var fs = NewFsWithDirs("agent--subagent--coder--ghost");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>());

        var entry = plan.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(SubAgentWorkspaceDisposition.Orphan);
        entry.IsPrunable.ShouldBeTrue();
        entry.Status.ShouldBeNull();
    }

    [Fact]
    public void BuildPlan_NonExistentRoot_ReturnsEmptyPlan()
    {
        var fs = new MockFileSystem();
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(@"C:\temp\does-not-exist", new Dictionary<string, string>());

        plan.ShouldBeEmpty();
    }

    [Fact]
    public void Prune_DryRun_DeletesNothingButCountsPrunable()
    {
        var fs = NewFsWithDirs("agent--subagent--coder--done");
        var reaper = new SubAgentWorkspaceReaper(fs);

        var plan = reaper.BuildPlan(Root, new Dictionary<string, string>
        {
            ["agent--subagent--coder--done"] = "Completed"
        });

        var wouldDelete = reaper.Prune(plan, dryRun: true);

        wouldDelete.ShouldBe(1);
        // Dry run must not touch the filesystem.
        fs.Directory.Exists(fs.Path.Combine(Root, "agent--subagent--coder--done")).ShouldBeTrue();
    }

    [Fact]
    public void SanitizeAgentDirectoryName_MatchesGatewayScheme()
    {
        // Child agent ids never contain invalid file name chars in practice, so sanitization is a
        // pass-through of the trimmed id. This guards the CLI reaper stays aligned with the gateway.
        SubAgentWorkspaceReaper.SanitizeAgentDirectoryName(" agent--subagent--coder--abc ")
            .ShouldBe("agent--subagent--coder--abc");
    }
}

/// <summary>
/// Tests for the <see cref="SubAgentCommand"/> execution surface: the sessions.db read path that
/// maps persisted sub-agent statuses onto workspace directories, and the list/prune exit codes.
/// </summary>
public sealed class SubAgentCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SubAgentCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-subagent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sessions.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateDatabase(params (string ChildAgentId, string Status)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE sub_agent_sessions (
                    id TEXT PRIMARY KEY,
                    parent_session_id TEXT NOT NULL,
                    parent_agent_id TEXT NOT NULL,
                    child_agent_id TEXT NOT NULL,
                    archetype TEXT,
                    started_at TEXT NOT NULL,
                    ended_at TEXT,
                    status TEXT NOT NULL DEFAULT 'Active'
                );
                """;
            create.ExecuteNonQuery();
        }

        var i = 0;
        foreach (var (child, status) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO sub_agent_sessions (id, parent_session_id, parent_agent_id, child_agent_id, started_at, status) VALUES (@id, 'p', 'pa', @child, '2026-01-01T00:00:00Z', @status)";
            insert.Parameters.AddWithValue("@id", $"sa-{i++}");
            insert.Parameters.AddWithValue("@child", child);
            insert.Parameters.AddWithValue("@status", status);
            insert.ExecuteNonQuery();
        }
    }

    private static MockFileSystem NewFsWithWorkspaces(string root, params string[] agentDirs)
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(root);
        foreach (var dir in agentDirs)
        {
            var workspace = fs.Path.Combine(root, dir, "workspace");
            fs.Directory.CreateDirectory(workspace);
            fs.File.WriteAllText(fs.Path.Combine(workspace, "scratch.txt"), "data");
        }

        return fs;
    }

    [Fact]
    public void ResolveSessionsDb_WithTarget_ReturnsTargetPath()
    {
        var path = SubAgentCommand.ResolveSessionsDb(_tempDir);
        path.ShouldBe(Path.Combine(_tempDir, "sessions.db"));
    }

    [Fact]
    public void ExecuteList_MixedWorkspaces_ReturnsZero()
    {
        CreateDatabase(
            ("agent--subagent--coder--done", "Completed"),
            ("agent--subagent--coder--live", "Active"));
        const string root = @"C:\ws";
        var fs = NewFsWithWorkspaces(root, "agent--subagent--coder--done", "agent--subagent--coder--live", "agent--subagent--coder--ghost");
        var command = new SubAgentCommand(fs);

        var exit = command.ExecuteList(_dbPath, root);

        exit.ShouldBe(0);
    }

    [Fact]
    public void ExecutePrune_TerminalAndOrphanPruned_RunningRetained()
    {
        CreateDatabase(
            ("agent--subagent--coder--done", "Completed"),
            ("agent--subagent--coder--live", "Active"));
        const string root = @"C:\ws";
        var fs = NewFsWithWorkspaces(root, "agent--subagent--coder--done", "agent--subagent--coder--live", "agent--subagent--coder--ghost");
        var command = new SubAgentCommand(fs);

        var exit = command.ExecutePrune(_dbPath, root, dryRun: false);

        exit.ShouldBe(0);
        fs.Directory.Exists(fs.Path.Combine(root, "agent--subagent--coder--done")).ShouldBeFalse();  // terminal
        fs.Directory.Exists(fs.Path.Combine(root, "agent--subagent--coder--ghost")).ShouldBeFalse(); // orphan
        fs.Directory.Exists(fs.Path.Combine(root, "agent--subagent--coder--live")).ShouldBeTrue();   // running retained
    }

    [Fact]
    public void ExecutePrune_DryRun_DeletesNothing()
    {
        CreateDatabase(("agent--subagent--coder--done", "Completed"));
        const string root = @"C:\ws";
        var fs = NewFsWithWorkspaces(root, "agent--subagent--coder--done");
        var command = new SubAgentCommand(fs);

        var exit = command.ExecutePrune(_dbPath, root, dryRun: true);

        exit.ShouldBe(0);
        fs.Directory.Exists(fs.Path.Combine(root, "agent--subagent--coder--done")).ShouldBeTrue();
    }

    [Fact]
    public void ExecutePrune_MissingDatabase_TreatsAllAsOrphansAndPrunes()
    {
        const string root = @"C:\ws";
        var fs = NewFsWithWorkspaces(root, "agent--subagent--coder--ghost");
        var command = new SubAgentCommand(fs);

        // No database created - every directory is an orphan and therefore prunable.
        var exit = command.ExecutePrune(_dbPath, root, dryRun: false);

        exit.ShouldBe(0);
        fs.Directory.Exists(fs.Path.Combine(root, "agent--subagent--coder--ghost")).ShouldBeFalse();
    }

    [Fact]
    public void ExecuteList_NoWorkspaces_ReturnsZero()
    {
        CreateDatabase();
        const string root = @"C:\empty";
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(root);
        var command = new SubAgentCommand(fs);

        var exit = command.ExecuteList(_dbPath, root);

        exit.ShouldBe(0);
    }
}
