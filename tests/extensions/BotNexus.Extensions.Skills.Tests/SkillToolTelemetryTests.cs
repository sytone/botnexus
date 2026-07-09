using System.Collections.Concurrent;
using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Skills.Telemetry;
using Shouldly;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Verifies that the skill tools record usage telemetry (#1833): <see cref="SkillTool"/> records
/// views on list/view_file and uses on load, and <see cref="SkillManagerTool"/> records patches and
/// creation provenance. Uses an in-memory fake sink so the assertions are deterministic and do not
/// depend on SQLite.
/// </summary>
public sealed class SkillToolTelemetryTests
{
    private sealed class FakeTelemetry : ISkillUsageTelemetry
    {
        public ConcurrentDictionary<string, int> Views { get; } = new();
        public ConcurrentDictionary<string, int> Uses { get; } = new();
        public ConcurrentDictionary<string, int> Patches { get; } = new();
        public ConcurrentDictionary<string, string> Created { get; } = new();

        public Task RecordViewAsync(string skillName, CancellationToken ct = default)
        {
            Views.AddOrUpdate(skillName, 1, (_, v) => v + 1);
            return Task.CompletedTask;
        }

        public Task RecordUseAsync(string skillName, CancellationToken ct = default)
        {
            Uses.AddOrUpdate(skillName, 1, (_, v) => v + 1);
            return Task.CompletedTask;
        }

        public Task RecordPatchAsync(string skillName, CancellationToken ct = default)
        {
            Patches.AddOrUpdate(skillName, 1, (_, v) => v + 1);
            return Task.CompletedTask;
        }

        public Task RecordCreatedAsync(string skillName, string createdBy, CancellationToken ct = default)
        {
            Created[skillName] = createdBy;
            return Task.CompletedTask;
        }

        public Task SetPinnedAsync(string skillName, bool pinned, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SkillUsageRecord>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SkillUsageRecord>>([]);
        public Task<SkillUsageRecord?> GetAsync(string skillName, CancellationToken ct = default)
            => Task.FromResult<SkillUsageRecord?>(null);
    }

    /// <summary>A telemetry sink that always throws, to prove tool recording is best-effort.</summary>
    private sealed class ThrowingTelemetry : ISkillUsageTelemetry
    {
        public Task RecordViewAsync(string skillName, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task RecordUseAsync(string skillName, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task RecordPatchAsync(string skillName, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task RecordCreatedAsync(string skillName, string createdBy, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task SetPinnedAsync(string skillName, bool pinned, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<SkillUsageRecord>> GetAllAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<SkillUsageRecord?> GetAsync(string skillName, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }

    private static SkillDefinition MakeSkill(string name)
        => new()
        {
            Name = name,
            Description = $"{name} skill description",
            Content = $"Content for {name}",
            Source = SkillSource.Global,
            SourcePath = $"/skills/{name}"
        };

    private static IReadOnlyDictionary<string, object?> Args(string action, string? skillName = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (skillName is not null)
            dict["skillName"] = skillName;
        return dict;
    }

    // ── list records views ───────────────────────────────────────────────────

    [Fact]
    public async Task List_RecordsViewForEverySurfacedSkill()
    {
        var telemetry = new FakeTelemetry();
        var tool = new SkillTool(new[] { MakeSkill("email-triage"), MakeSkill("calendar") }, config: null, telemetry: telemetry);

        await tool.ExecuteAsync("call-1", Args("list"));

        telemetry.Views["email-triage"].ShouldBe(1);
        telemetry.Views["calendar"].ShouldBe(1);
    }

    // ── load records use ───────────────────────────────────────────────────────

    [Fact]
    public async Task Load_RecordsUseForLoadedSkill()
    {
        var telemetry = new FakeTelemetry();
        var tool = new SkillTool(new[] { MakeSkill("git-workflow") }, config: null, telemetry: telemetry);

        await tool.ExecuteAsync("call-1", Args("load", "git-workflow"));

        telemetry.Uses["git-workflow"].ShouldBe(1);
    }

    [Fact]
    public async Task Load_UnknownSkill_DoesNotRecordUse()
    {
        var telemetry = new FakeTelemetry();
        var tool = new SkillTool(new[] { MakeSkill("git-workflow") }, config: null, telemetry: telemetry);

        await tool.ExecuteAsync("call-1", Args("load", "does-not-exist"));

        telemetry.Uses.ShouldBeEmpty();
    }

    // ── best-effort: telemetry failure must not break the tool ─────────────────

    [Fact]
    public async Task Load_WhenTelemetryThrows_StillReturnsSkill()
    {
        var tool = new SkillTool(new[] { MakeSkill("resilient") }, config: null, telemetry: new ThrowingTelemetry());

        var result = await tool.ExecuteAsync("call-1", Args("load", "resilient"));
        var text = string.Join("", result.Content.Select(c => c.Value));

        text.ShouldContain("Content for resilient");
    }

    // ── manage tool records patch + created ────────────────────────────────────

    [Fact]
    public async Task Create_RecordsCreatedProvenance()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var telemetry = new FakeTelemetry();
        var config = new SkillsConfig { AllowSkillCreation = true };
        var tool = new SkillManagerTool("/agent/skills", "/workspace/skills", globalSkillsDir: null, config, fs, telemetry, createdBy: "agent-farnsworth");

        var content = """
            ---
            name: new-skill
            description: A new skill created by the agent
            ---
            # new-skill
            body
            """;
        var args = new Dictionary<string, object?> { ["action"] = "create", ["name"] = "new-skill", ["content"] = content };

        await tool.ExecuteAsync("x", args);

        telemetry.Created.ShouldContainKey("new-skill");
        telemetry.Created["new-skill"].ShouldBe("agent-farnsworth");
    }

    [Fact]
    public async Task Patch_RecordsPatch()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var telemetry = new FakeTelemetry();
        var config = new SkillsConfig { AllowSkillCreation = true };
        var tool = new SkillManagerTool("/agent/skills", "/workspace/skills", globalSkillsDir: null, config, fs, telemetry, createdBy: "agent-farnsworth");

        var content = """
            ---
            name: patchme
            description: A skill to patch
            ---
            # patchme
            original body
            """;
        await tool.ExecuteAsync("x", new Dictionary<string, object?> { ["action"] = "create", ["name"] = "patchme", ["content"] = content });

        await tool.ExecuteAsync("x", new Dictionary<string, object?>
        {
            ["action"] = "patch",
            ["name"] = "patchme",
            ["oldText"] = "original body",
            ["newText"] = "patched body"
        });

        telemetry.Patches["patchme"].ShouldBe(1);
    }
}
