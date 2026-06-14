using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Verifies the per-call <c>timeout</c> ceiling added for issue #1350 (ShellTool half).
/// Without an upper bound an agent — or a poisoned cron prompt — could pass an absurd
/// timeout (e.g. <c>86400</c>) and hold a process slot / OS handle for hours. These tests
/// assert the clamp via the public <see cref="ShellTool.PrepareArgumentsAsync"/> contract
/// (deterministic, no long-running process) plus a fast end-to-end clamp-warning check.
/// </summary>
public sealed class ShellToolTimeoutCeilingTests
{
    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutExceedsCeiling_ClampsToMax()
    {
        // defaultTimeoutSeconds must be <= the ceiling, otherwise the constructor raises the
        // effective ceiling to the default (covered separately by the floor test below).
        var tool = new ShellTool(defaultTimeoutSeconds: 30, maxTimeoutSeconds: 30);

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["timeout"] = 86_400
        });

        prepared["timeout"].ShouldBe(30);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutWithinCeiling_LeavesUnchanged()
    {
        var tool = new ShellTool(maxTimeoutSeconds: 3600);

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["timeout"] = 120
        });

        prepared["timeout"].ShouldBe(120);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_UsesDefaultMaxCeilingOfOneHour()
    {
        // Default ceiling is DefaultMaxTimeoutSeconds (3600). A request above it clamps to 3600.
        var tool = new ShellTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["timeout"] = 100_000
        });

        prepared["timeout"].ShouldBe(ShellTool.DefaultMaxTimeoutSeconds);
        ShellTool.DefaultMaxTimeoutSeconds.ShouldBe(3600);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutBelowMinimum_StillThrows()
    {
        // Clamping the upper bound must not weaken the existing lower-bound (>= 1) validation.
        var tool = new ShellTool(defaultTimeoutSeconds: 30, maxTimeoutSeconds: 30);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?>
            {
                ["command"] = "echo hi",
                ["timeout"] = 0
            }));
    }

    [Fact]
    public void Constructor_WhenMaxTimeoutSecondsBelowOne_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShellTool(maxTimeoutSeconds: 0));
    }

    [Fact]
    public async Task PrepareArgumentsAsync_CeilingNeverDropsBelowConfiguredDefault()
    {
        // If the supplied ceiling is lower than the default timeout, the default would otherwise be
        // clamped on every call. The constructor raises the effective ceiling to the default instead.
        var tool = new ShellTool(defaultTimeoutSeconds: 600, maxTimeoutSeconds: 60);

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["timeout"] = 500
        });

        // 500 is below the effective ceiling (raised to 600), so it is left unchanged.
        prepared["timeout"].ShouldBe(500);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeoutExceedsCeiling_PrependsClampWarning()
    {
        // A fast command exits well within the clamped ceiling, so the timeout never fires;
        // we only assert the clamp warning is surfaced on the result. OS-agnostic (echo).
        // defaultTimeoutSeconds is lowered to the ceiling so the floor does not raise it.
        var tool = new ShellTool(defaultTimeoutSeconds: 30, maxTimeoutSeconds: 30);

        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "echo clamp-ok",
            ["timeout"] = 86_400
        });

        result.Content[0].Value.ShouldContain("clamp-ok");
        result.Content[0].Value.ShouldContain("clamped to 30s");
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeoutWithinCeiling_OmitsClampWarning()
    {
        var tool = new ShellTool(maxTimeoutSeconds: 3600);

        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "echo no-clamp",
            ["timeout"] = 60
        });

        result.Content[0].Value.ShouldContain("no-clamp");
        result.Content[0].Value.ShouldNotContain("clamped to");
    }
}
