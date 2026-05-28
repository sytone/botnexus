using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Single Playwright-driven walk-through that opens the portal and verifies the
/// provisioned agents are visible. The richer multi-conversation, multi-agent,
/// parallel-streaming flow described in issue #598 is intentionally left as
/// followup placeholders below — landing this scaffold unblocks anyone wanting
/// to extend it without each PR re-doing the install/provisioning plumbing.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PortalUserJourneyTests
{
    private readonly NewUserExperienceFixture _fx;

    public PortalUserJourneyTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task PortalLoads_RendersAgentsFromConfig()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");

        try
        {
            await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Playwright browser install unavailable: {ex.Message}");
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(_fx.GatewayBaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        Xunit.Assert.NotNull(response);
        Xunit.Assert.True(response!.Ok, $"GET / returned {response.Status}");

        // The portal renders agent IDs somewhere on the page — sidebar list,
        // dropdown, or workspace card. Rather than couple to a specific DOM
        // selector (which churns) assert the text appears anywhere on the page
        // once the SPA has settled.
        var body = await page.ContentAsync();
        foreach (var id in _fx.AgentIds)
        {
            Xunit.Assert.True(
                body.Contains(id, StringComparison.OrdinalIgnoreCase),
                $"Portal HTML did not contain agent id '{id}'.");
        }
    }

    // ─── Followup placeholders for issue #598 ──────────────────────────────
    // These flows depend on portal selectors (agent picker, "new conversation"
    // button, message composer, conversation list) that are still evolving.
    // Pin them down once the portal exposes stable `data-testid` hooks.

    [Fact(Skip = "Followup #598: open conversation per agent and send HELLO_WORLD")]
    public void NewConversation_PerAgent_SendHelloWorld() { }

    [Fact(Skip = "Followup #598: trigger MULTI_DELTA streams in parallel across all 3 agents")]
    public void ParallelMultiDelta_AcrossAgents_AllCompleteIndependently() { }

    [Fact(Skip = "Followup #598: existing conversation receives TOOL_CALL_SEQUENCE while a new one streams concurrently")]
    public void MixedExistingAndNewConversations_ConcurrentMessages() { }
}
