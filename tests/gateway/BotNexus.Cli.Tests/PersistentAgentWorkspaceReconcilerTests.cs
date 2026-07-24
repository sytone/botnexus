using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

public sealed class PersistentAgentWorkspaceReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-doctor-agents-" + Guid.NewGuid().ToString("N"));

    public PersistentAgentWorkspaceReconcilerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void BuildPlan_ClassifiesRegisteredOrphanedMixedAndEmptyDirectories()
    {
        var agents = Path.Combine(_root, "agents");
        Directory.CreateDirectory(Path.Combine(agents, "REGISTERED"));
        Directory.CreateDirectory(Path.Combine(agents, "orphan"));
        var config = new PlatformConfig
        {
            Agents = new(StringComparer.OrdinalIgnoreCase)
            {
                [" registered "] = new(),
                ["disabled"] = new() { Enabled = false },
                ["defaults"] = new()
            }
        };

        var plan = new PersistentAgentWorkspaceReconciler().BuildPlan(agents, config);

        plan.Count.ShouldBe(2);
        plan.Single(x => x.DirectoryName == "REGISTERED").IsOrphaned.ShouldBeFalse();
        plan.Single(x => x.DirectoryName == "orphan").IsOrphaned.ShouldBeTrue();
        new PersistentAgentWorkspaceReconciler().BuildPlan(Path.Combine(_root, "missing"), config).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("doctor --cleanup-orphans")]
    [InlineData("doctor agents")]
    [InlineData("doctor agents --cleanup-orphans")]
    public void Build_AcceptsBareAndDedicatedAgentCommandShapes(string commandLine)
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new DoctorCommand().Build(verbose, target));

        root.Parse(commandLine).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveAgentsRoot_HonorsConfiguredDirectoryRelativeToTarget()
    {
        var configured = PersistentAgentWorkspaceReconciler.ResolveAgentsRoot(_root, "custom/agents");
        configured.ShouldBe(Path.GetFullPath(Path.Combine(_root, "custom", "agents")));
        PersistentAgentWorkspaceReconciler.ResolveAgentsRoot(_root, null).ShouldBe(Path.Combine(Path.GetFullPath(_root), "agents"));
    }

    [Fact]
    public void DeleteOrphans_DeletesOnlyOrphansInsideRoot()
    {
        var agents = Path.Combine(_root, "agents");
        var registered = Directory.CreateDirectory(Path.Combine(agents, "registered")).FullName;
        var orphan = Directory.CreateDirectory(Path.Combine(agents, "orphan")).FullName;
        var plan = new[]
        {
            new PersistentAgentWorkspaceEntry("registered", registered, false, false),
            new PersistentAgentWorkspaceEntry("orphan", orphan, true, false)
        };

        new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, plan);

        Directory.Exists(registered).ShouldBeTrue();
        Directory.Exists(orphan).ShouldBeFalse();
    }

    [Fact]
    public void DeleteOrphans_RejectsPathOutsideRoot()
    {
        var agents = Directory.CreateDirectory(Path.Combine(_root, "agents")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var plan = new[] { new PersistentAgentWorkspaceEntry("outside", outside, true, false) };
        Should.Throw<InvalidOperationException>(() => new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, plan));
        Directory.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void DeleteOrphans_RejectsNestedDirectoryEvenWhenItIsInsideRoot()
    {
        var agents = Directory.CreateDirectory(Path.Combine(_root, "agents")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(agents, "registered", "nested")).FullName;
        var plan = new[] { new PersistentAgentWorkspaceEntry("nested", nested, true, false) };

        Should.Throw<InvalidOperationException>(() => new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, plan));

        Directory.Exists(nested).ShouldBeTrue();
    }

    [Fact]
    public void DeleteOrphans_RechecksReparsePointAtDeletionTime()
    {
        var agents = Directory.CreateDirectory(Path.Combine(_root, "agents")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(agents, "orphan");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        var plan = new[] { new PersistentAgentWorkspaceEntry("orphan", link, true, false) };

        Should.Throw<InvalidOperationException>(() => new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, plan));

        Directory.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void DeleteOrphans_ValidatesEntireBatchBeforeDeletingAnything()
    {
        var agents = Directory.CreateDirectory(Path.Combine(_root, "agents")).FullName;
        var safeOrphan = Directory.CreateDirectory(Path.Combine(agents, "a-safe-orphan")).FullName;
        var unsafeOrphan = Directory.CreateDirectory(Path.Combine(agents, "z-unsafe-orphan")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(unsafeOrphan, "nested-link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var plan = new[]
        {
            new PersistentAgentWorkspaceEntry("a-safe-orphan", safeOrphan, true, false),
            new PersistentAgentWorkspaceEntry("z-unsafe-orphan", unsafeOrphan, true, false)
        };

        Should.Throw<InvalidOperationException>(() => new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, plan));

        Directory.Exists(safeOrphan).ShouldBeTrue();
        Directory.Exists(unsafeOrphan).ShouldBeTrue();
        Directory.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void BuildPlan_MarksReparsePointAsUnsafe()
    {
        var agents = Directory.CreateDirectory(Path.Combine(_root, "agents")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(agents, "linked-orphan");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var entry = new PersistentAgentWorkspaceReconciler().BuildPlan(agents, new PlatformConfig()).Single();
        entry.IsOrphaned.ShouldBeTrue();
        entry.IsUnsafeLink.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => new PersistentAgentWorkspaceReconciler().DeleteOrphans(agents, [entry]));
    }

    [Fact]
    public async Task ExecuteAgentsAsync_NonInteractiveDoesNotDeleteWithoutOptIn()
    {
        var agents = Path.Combine(_root, "agents");
        var orphan = Directory.CreateDirectory(Path.Combine(agents, "orphan")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{}");

        var result = await new DoctorCommand().ExecuteAgentsAsync(_root, false, false, CancellationToken.None);

        result.ShouldBe(1);
        Directory.Exists(orphan).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAgentsAsync_InteractiveDeclineDoesNotDelete()
    {
        var orphan = Directory.CreateDirectory(Path.Combine(_root, "agents", "orphan")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{}");

        var result = await new DoctorCommand().ExecuteAgentsAsync(_root, false, true, CancellationToken.None, _ => false);

        result.ShouldBe(1);
        Directory.Exists(orphan).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAgentsAsync_ExplicitCleanupDeletesOrphanFromConfiguredRoot()
    {
        var custom = Path.Combine(_root, "custom-agents");
        var orphan = Directory.CreateDirectory(Path.Combine(custom, "orphan")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{\"gateway\":{\"agentsDirectory\":\"custom-agents\"}}");

        var result = await new DoctorCommand().ExecuteAgentsAsync(_root, true, false, CancellationToken.None);

        result.ShouldBe(0);
        Directory.Exists(orphan).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAgentsAsync_InteractiveApproveDeletesOrphan()
    {
        var orphan = Directory.CreateDirectory(Path.Combine(_root, "agents", "orphan")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{}");

        var result = await new DoctorCommand().ExecuteAgentsAsync(_root, false, true, CancellationToken.None, _ => true);

        result.ShouldBe(0);
        Directory.Exists(orphan).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAgentsAsync_RegisteredWorkspaceIsNeverDeleted()
    {
        var registered = Directory.CreateDirectory(Path.Combine(_root, "agents", "keeper")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{\"agents\":{\"keeper\":{}}}");

        var result = await new DoctorCommand().ExecuteAgentsAsync(_root, true, false, CancellationToken.None);

        result.ShouldBe(0);
        Directory.Exists(registered).ShouldBeTrue();
    }
}
