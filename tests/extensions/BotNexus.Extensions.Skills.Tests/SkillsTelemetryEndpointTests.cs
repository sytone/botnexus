using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Skills.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Tests the skill usage telemetry read endpoints (#1833): <c>GET /api/skills/telemetry</c> and
/// <c>GET /api/skills/telemetry/{skillName}</c>. Drives the endpoint handlers directly with a real
/// SQLite-backed store in a temp directory, mirroring the store-level tests.
/// </summary>
public sealed class SkillsTelemetryEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteSkillUsageStore _store;

    public SkillsTelemetryEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-skill-telemetry-endpoint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SqliteSkillUsageStore(Path.Combine(_dir, "skill-usage.db"));
    }

    [Fact]
    public async Task GetTelemetry_ReturnsRecordedSkills()
    {
        await _store.RecordUseAsync("email-triage");
        await _store.RecordViewAsync("email-triage");
        await _store.RecordPatchAsync("calendar");

        var result = await SkillsEndpointContributor.GetTelemetry(_store);

        var ok = result.ShouldBeOfType<Ok<SkillUsageTelemetryResponse>>();
        ok.Value!.Skills.Count.ShouldBe(2);
        var triage = ok.Value.Skills.Single(s => s.SkillName == "email-triage");
        triage.UseCount.ShouldBe(1);
        triage.ViewCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetTelemetry_OnEmptyStore_ReturnsEmptyList()
    {
        var result = await SkillsEndpointContributor.GetTelemetry(_store);

        var ok = result.ShouldBeOfType<Ok<SkillUsageTelemetryResponse>>();
        ok.Value!.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTelemetry_WithNullSink_ReturnsEmptyList()
    {
        var result = await SkillsEndpointContributor.GetTelemetry(telemetry: null);

        var ok = result.ShouldBeOfType<Ok<SkillUsageTelemetryResponse>>();
        ok.Value!.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTelemetryForSkill_ReturnsSingleRecord()
    {
        await _store.RecordUseAsync("git-workflow");

        var result = await SkillsEndpointContributor.GetTelemetryForSkill("git-workflow", _store);

        var ok = result.ShouldBeOfType<Ok<SkillUsageDto>>();
        ok.Value!.SkillName.ShouldBe("git-workflow");
        ok.Value.UseCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetTelemetryForSkill_UnknownSkill_ReturnsNotFound()
    {
        var result = await SkillsEndpointContributor.GetTelemetryForSkill("nope", _store);
        result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task GetTelemetryForSkill_BlankName_ReturnsBadRequest()
    {
        var result = await SkillsEndpointContributor.GetTelemetryForSkill("  ", _store);
        var status = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        status!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    public void Dispose()
    {
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
