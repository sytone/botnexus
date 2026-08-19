using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Skills.Security;
using Shouldly;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Pins plugin-shipped skill discovery at the global/shared precedence tier (#2684).
/// </summary>
/// <remarks>
/// Four things are load-bearing here and each has its own test:
/// <list type="number">
/// <item><description>A plugin's skills are discovered at all, and carry <see cref="SkillSource.Plugin"/>.</description></item>
/// <item><description>Every higher scope - global, agent, workspace - still wins a name collision.</description></item>
/// <item><description>With no plugins installed the pre-existing three-scope outcome is byte-identical.</description></item>
/// <item><description>Under <see cref="SkillTrustMode.Enforce"/> an untrusted plugin skill is not surfaced.</description></item>
/// </list>
/// </remarks>
public sealed class PluginSkillDiscoveryTests
{
    private readonly MockFileSystem _fileSystem = new();
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-skill-tests");

    private static string PluginRoot => Path.Combine(Root, "plugins");
    private static string GlobalDir => Path.Combine(Root, "skills");
    private static string AgentDir => Path.Combine(Root, "agent-skills");
    private static string WorkspaceDir => Path.Combine(Root, "workspace-skills");

    // ── clause 1: plugin skills are discovered ──────────────────────────────

    [Fact]
    public void Discover_InstalledPluginSkill_IsSurfacedWithPluginSource()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Deploy the service"));

        var skills = Discover();

        var skill = skills.ShouldHaveSingleItem();
        skill.Name.ShouldBe("deploy-runbook");
        skill.Description.ShouldBe("Deploy the service");
        skill.Source.ShouldBe(SkillSource.Plugin);
    }

    [Fact]
    public void Discover_MultiplePlugins_SurfacesSkillsFromEach()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Deploy"));
        InstallPlugin("beta-tools", ("rollback-runbook", "Rollback"));

        var skills = Discover();

        skills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["deploy-runbook", "rollback-runbook"]);
        skills.ShouldAllBe(s => s.Source == SkillSource.Plugin);
    }

    [Fact]
    public void Discover_PluginDirectoryNotRecordedAsInstalled_IsIgnored()
    {
        // Content dropped next to real plugins was never installed, so nothing vouches for its
        // provenance and it must not reach agent context.
        CreateSkill(Path.Combine(PluginRoot, "smuggled", "skills"), "smuggled-skill", "Not installed");
        WriteState();

        Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_PluginWithoutSkillsDirectory_ContributesNothing()
    {
        WriteState("agents-only");
        _fileSystem.Directory.CreateDirectory(Path.Combine(PluginRoot, "agents-only", "agents"));

        Discover().ShouldBeEmpty();
    }

    // ── clause 2: precedence - every higher scope still wins ────────────────

    [Fact]
    public void Discover_AgentSkill_OverridesSamePluginSkill()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Plugin version"));
        CreateSkill(AgentDir, "deploy-runbook", "Agent version");

        var skill = Discover().ShouldHaveSingleItem();

        skill.Source.ShouldBe(SkillSource.Agent);
        skill.Description.ShouldBe("Agent version");
    }

    [Fact]
    public void Discover_GlobalSkill_OverridesSamePluginSkill()
    {
        // Plugin and global share a tier, but operator-authored shared content is the one that
        // must survive: a plugin may never displace a skill the operator wrote themselves.
        InstallPlugin("acme-tools", ("deploy-runbook", "Plugin version"));
        CreateSkill(GlobalDir, "deploy-runbook", "Global version");

        var skill = Discover().ShouldHaveSingleItem();

        skill.Source.ShouldBe(SkillSource.Global);
        skill.Description.ShouldBe("Global version");
    }

    [Fact]
    public void Discover_WorkspaceSkill_OverridesSamePluginSkill()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Plugin version"));
        CreateSkill(WorkspaceDir, "deploy-runbook", "Workspace version");

        var skill = Discover().ShouldHaveSingleItem();

        skill.Source.ShouldBe(SkillSource.Workspace);
        skill.Description.ShouldBe("Workspace version");
    }

    [Fact]
    public void Discover_PluginSkill_DoesNotDisplaceUnrelatedScopes()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Plugin"));
        CreateSkill(GlobalDir, "email-triage", "Global");
        CreateSkill(AgentDir, "calendar", "Agent");
        CreateSkill(WorkspaceDir, "notes", "Workspace");

        var skills = Discover();

        skills.Count.ShouldBe(4);
        skills.Single(s => s.Name == "deploy-runbook").Source.ShouldBe(SkillSource.Plugin);
        skills.Single(s => s.Name == "email-triage").Source.ShouldBe(SkillSource.Global);
        skills.Single(s => s.Name == "calendar").Source.ShouldBe(SkillSource.Agent);
        skills.Single(s => s.Name == "notes").Source.ShouldBe(SkillSource.Workspace);
    }

    // ── clause 3: no-plugin parity (the non-vacuity anchor) ─────────────────

    [Fact]
    public void Discover_WithNoPluginsInstalled_IsByteIdenticalToPrePluginResolution()
    {
        // The pre-existing three-scope fixture: the same name at all three scopes plus one
        // distinct name per scope. If this slice perturbs precedence in any way, the projection
        // below changes and this test fails.
        CreateSkill(GlobalDir, "email-triage", "Global collide");
        CreateSkill(AgentDir, "email-triage", "Agent collide");
        CreateSkill(WorkspaceDir, "email-triage", "Workspace collide");
        CreateSkill(GlobalDir, "global-only", "Global only");
        CreateSkill(AgentDir, "agent-only", "Agent only");
        CreateSkill(WorkspaceDir, "workspace-only", "Workspace only");

        var baseline = Project(SkillDiscovery.Discover(GlobalDir, AgentDir, WorkspaceDir, _fileSystem));

        // The exact pre-#2684 outcome, written out literally rather than recomputed, so the
        // assertion cannot drift with the implementation it is guarding.
        baseline.ShouldBe(
        [
            "agent-only|Agent|Agent only",
            "email-triage|Workspace|Workspace collide",
            "global-only|Global|Global only",
            "workspace-only|Workspace|Workspace only",
        ]);

        // Every route into the new parameter that a machine with no plugins can take must land on
        // exactly that same outcome.
        Project(SkillDiscovery.Discover(GlobalDir, AgentDir, WorkspaceDir, _fileSystem, pluginSkillsDirs: null))
            .ShouldBe(baseline);
        Project(SkillDiscovery.Discover(GlobalDir, AgentDir, WorkspaceDir, _fileSystem, pluginSkillsDirs: []))
            .ShouldBe(baseline);
        Project(SkillDiscovery.Discover(
                GlobalDir, AgentDir, WorkspaceDir, _fileSystem,
                pluginSkillsDirs: PluginSkillRootResolver.Resolve(PluginRoot, _fileSystem)))
            .ShouldBe(baseline);
    }

    [Fact]
    public void Resolve_WithNoPluginRoot_ReturnsNoRoots()
    {
        PluginSkillRootResolver.Resolve(PluginRoot, _fileSystem).ShouldBeEmpty();
        PluginSkillRootResolver.Resolve((string?)null, _fileSystem).ShouldBeEmpty();
    }

    // ── clause 4: trust enforcement ─────────────────────────────────────────

    [Fact]
    public void Discover_UntrustedPluginSkill_UnderEnforce_IsNotSurfaced()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Deploy the service"));
        var skillDir = Path.Combine(PluginRoot, "acme-tools", "skills", "deploy-runbook");
        WriteScript(skillDir, "Write-Output 'tampered'"u8.ToArray());
        WriteTrustCatalog(skillDir, "scripts/run.ps1", sha256: new string('0', 64));

        var logger = new CapturingLogger();
        var skills = Discover(SkillTrustMode.Enforce, logger);

        skills.ShouldBeEmpty();
        logger.Warnings.ShouldContain(w => w.Contains("trust verification failed") && w.Contains("deploy-runbook"));
    }

    [Fact]
    public void Discover_TrustedPluginSkill_UnderEnforce_IsSurfaced()
    {
        // Mutation guard for the test above: Enforce must reject only the UNTRUSTED plugin skill,
        // not every plugin skill. Without this, "skills is empty" would pass for the wrong reason.
        InstallPlugin("acme-tools", ("deploy-runbook", "Deploy the service"));
        var skillDir = Path.Combine(PluginRoot, "acme-tools", "skills", "deploy-runbook");
        var scriptBytes = "Write-Output 'trusted'"u8.ToArray();
        WriteScript(skillDir, scriptBytes);
        WriteTrustCatalog(skillDir, "scripts/run.ps1", SkillTrustVerifier.ComputeSha256(scriptBytes));

        var skills = Discover(SkillTrustMode.Enforce);

        skills.ShouldHaveSingleItem().Source.ShouldBe(SkillSource.Plugin);
    }

    [Fact]
    public void Discover_UntrustedPluginSkill_UnderWarn_IsSurfacedWithWarning()
    {
        InstallPlugin("acme-tools", ("deploy-runbook", "Deploy the service"));
        var skillDir = Path.Combine(PluginRoot, "acme-tools", "skills", "deploy-runbook");
        WriteScript(skillDir, "Write-Output 'tampered'"u8.ToArray());
        WriteTrustCatalog(skillDir, "scripts/run.ps1", sha256: new string('0', 64));

        var logger = new CapturingLogger();
        var skills = Discover(SkillTrustMode.Warn, logger);

        skills.ShouldHaveSingleItem().Source.ShouldBe(SkillSource.Plugin);
        logger.Warnings.ShouldContain(w => w.Contains("trust violations"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private IReadOnlyList<SkillDefinition> Discover(
        SkillTrustMode trustMode = SkillTrustMode.Disabled,
        CapturingLogger? logger = null)
        => SkillDiscovery.Discover(
            GlobalDir,
            AgentDir,
            WorkspaceDir,
            _fileSystem,
            logger,
            trustMode,
            PluginSkillRootResolver.Resolve(PluginRoot, _fileSystem));

    /// <summary>
    /// Projects a resolution to a stable, ordered string form so two resolutions can be compared
    /// exactly rather than by spot-checking individual fields.
    /// </summary>
    private static IReadOnlyList<string> Project(IReadOnlyList<SkillDefinition> skills)
        => skills
            .Select(s => $"{s.Name}|{s.Source}|{s.Description}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>Materialises a plugin's skills AND records it as installed, as install does.</summary>
    private void InstallPlugin(string pluginName, params (string Name, string Description)[] skills)
    {
        var skillsDir = Path.Combine(PluginRoot, pluginName, "skills");
        foreach (var (name, description) in skills)
        {
            CreateSkill(skillsDir, name, description);
        }

        var recorded = ReadStateNames().Append(pluginName).Distinct(StringComparer.Ordinal).ToArray();
        WriteState(recorded);
    }

    private IEnumerable<string> ReadStateNames()
    {
        var statePath = Path.Combine(PluginRoot, PluginStateStore.StateFileName);
        if (!_fileSystem.File.Exists(statePath))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(_fileSystem.File.ReadAllText(statePath));
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
    }

    private void WriteState(params string[] pluginNames)
    {
        var records = pluginNames.Select(name => new InstalledPlugin
        {
            Name = name,
            Source = $"https://example.com/{name}.git",
            ResolvedVersion = "abc123",
            InstalledAtUtc = DateTimeOffset.UnixEpoch,
            Files = [],
        }).ToList();

        new PluginStateStore(PluginRoot, _fileSystem).Write(records);
    }

    /// <summary>
    /// Writes a skill's <c>scripts/run.ps1</c>. The parent directory is created explicitly:
    /// <see cref="MockFileSystem"/> does NOT create intermediate directories on write, unlike the
    /// real filesystem's behaviour under <c>File.WriteAllText</c> in these fixtures.
    /// </summary>
    private void WriteScript(string skillDir, byte[] content)
    {
        var scriptsDir = Path.Combine(skillDir, "scripts");
        _fileSystem.Directory.CreateDirectory(scriptsDir);
        _fileSystem.File.WriteAllBytes(Path.Combine(scriptsDir, "run.ps1"), content);
    }

    private void WriteTrustCatalog(string skillDir, string relativePath, string sha256)
    {
        _fileSystem.File.WriteAllText(
            Path.Combine(skillDir, SkillTrustVerifier.CatalogFileName),
            $$"""
            {
              "version": 1,
              "generatedAt": "2026-01-01T00:00:00Z",
              "entries": [
                { "path": "{{relativePath}}", "sha256": "{{sha256}}", "updatedAt": "2026-01-01T00:00:00Z" }
              ]
            }
            """);
    }

    private void CreateSkill(string parentDir, string skillName, string description)
    {
        var skillDir = Path.Combine(parentDir, skillName);
        _fileSystem.Directory.CreateDirectory(skillDir);
        _fileSystem.File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {skillName}
            description: {description}
            ---
            # {skillName}

            Skill instructions.
            """);
    }
}
