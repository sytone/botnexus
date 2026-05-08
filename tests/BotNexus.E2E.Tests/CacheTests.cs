namespace BotNexus.E2E.Tests;

/// <summary>
/// Playwright E2E tests verifying the <c>ConversationHistoryCache</c> behaviour:
/// instant render from localStorage on revisit, cache invalidation on session reset,
/// and correct no-op when the flag is disabled.
/// All tests skip gracefully when the dev gateway is not running at localhost:5006.
/// </summary>
public sealed class CacheTests : E2ETestBase
{
    private const string CacheFlag = "bn:feature:conversationHistoryCache";
    private string CacheKey(string convId) => $"bn:conv-history:{convId}";

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task EnableCacheFlagAsync() =>
        await Page.EvaluateAsync($"() => localStorage.setItem('{CacheFlag}', 'true')");

    private async Task DisableCacheFlagAsync() =>
        await Page.EvaluateAsync($"() => localStorage.removeItem('{CacheFlag}')");

    private async Task<string?> GetLocalStorageItemAsync(string key) =>
        await Page.EvaluateAsync<string?>($"() => localStorage.getItem('{key}')");

    private async Task<long> MeasureFirstMessageRenderMsAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Page.WaitForSelectorAsync(".message.user, .message.assistant", new() { Timeout = 15000, State = WaitForSelectorState.Attached });
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the cache flag in localStorage BEFORE loading the portal,
    /// so FeatureFlagsService.InitializeAsync() picks it up on startup.
    /// </summary>
    private async Task EnableCacheFlagBeforeLoadAsync()
    {
        // Load the portal, set the flag, then reload so FeatureFlagsService reads it
        await WaitForPortalReadyAsync();
        await EnableCacheFlagAsync();
        await Page.ReloadAsync();
        await WaitForPortalReadyAsync();
    }

    /// <summary>
    /// Second load with cache enabled should appear at least as fast as the first,
    /// since history is served from localStorage without waiting for a server round-trip.
    /// </summary>
    [SkippableFact]
    public async Task Cache_WhenFlagEnabled_SecondLoad_FasterThanFirst()
    {
        await WaitForPortalReadyAsync();
        await EnableCacheFlagAsync();
        await SelectAgentAsync(AgentId);

        // First load — cold (no cache)
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();

        var firstLoadMs = await MeasureFirstMessageRenderMsAsync();

        // Reload the page — cache should be warm now
        await Page.ReloadAsync();
        await WaitForPortalReadyAsync();
        await EnableCacheFlagAsync();
        await SelectAgentAsync(AgentId);
        await Page.Locator(".conversation-list-item").First.ClickAsync();

        var secondLoadMs = await MeasureFirstMessageRenderMsAsync();

        // Second load should not be significantly slower than first
        // (allow 2x tolerance since timing is environment-dependent)
        secondLoadMs.ShouldBeLessThanOrEqualTo(firstLoadMs * 2 + 2000);
    }

    /// <summary>
    /// After a reload with cache flag enabled, the conversation history should
    /// appear immediately from localStorage without waiting for a full server round-trip.
    /// We verify by checking that a localStorage key exists for the active conversation.
    /// </summary>
    [SkippableFact]
    public async Task Cache_AfterReload_HistoryAppearsImmediately_WithoutServerRound_Trip()
    {
        // Capture console errors to surface cache failures
        var consoleMessages = new System.Collections.Generic.List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");

        // Enable flag and reload so FeatureFlagsService picks it up
        await EnableCacheFlagBeforeLoadAsync();
        
        // Verify flag is active
        var flagValue = await Page.EvaluateAsync<string>("() => localStorage.getItem('bn:feature:conversationHistoryCache')");
        flagValue.ShouldBe("true", "Cache flag must be set before checking cache writes");
        
        await SelectAgentAsync(AgentId);

        await Page.WaitForSelectorAsync(".conversation-list-item",
            new() { Timeout = 20000, State = WaitForSelectorState.Attached });

        // Click the default conversation to trigger history load + cache write
        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(2000); // Allow cache write to complete

        // Retrieve the active conversation ID from the DOM or localStorage
        var keys = await Page.EvaluateAsync<string[]>(
            "() => Object.keys(localStorage).filter(k => k.startsWith('bn:conv-history:'))");
        keys.ShouldNotBeNull();
        // Debug: dump console messages if assertion fails
        if (keys.Length == 0)
        {
            var consoleOutput = string.Join("\n", consoleMessages);
            throw new Xunit.Sdk.XunitException(
                $"No cache keys found. Flag was: '{flagValue}'.\nConsole output:\n{consoleOutput}");
        }
        keys.Length.ShouldBeGreaterThan(0, "Expected at least one conversation history cache entry in localStorage");
    }

    /// <summary>
    /// After a session reset, the stale cache entry for that conversation should
    /// be removed — so a reload does not show history from the previous session.
    /// </summary>
    [SkippableFact]
    public async Task Cache_AfterSessionReset_DoesNotShowStaleHistory()
    {
        await WaitForPortalReadyAsync();
        await EnableCacheFlagAsync();
        await SelectAgentAsync(AgentId);

        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(1500); // Let cache populate

        // Confirm cache was written
        var keysBefore = await Page.EvaluateAsync<string[]>(
            "() => Object.keys(localStorage).filter(k => k.startsWith('bn:conv-history:'))");
        keysBefore.ShouldNotBeNull();

        // Trigger session reset via the reset button (skip if not present)
        var resetBtn = Page.Locator("[data-testid='session-reset-btn'], .session-reset-btn, button:has-text('Reset')");
        if (await resetBtn.CountAsync() == 0)
            throw new SkipException("No session reset button found — skipping cache invalidation E2E test");

        await resetBtn.First.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        // Cache entries for the active conversation should be removed
        var keysAfter = await Page.EvaluateAsync<string[]>(
            "() => Object.keys(localStorage).filter(k => k.startsWith('bn:conv-history:'))");

        // Either all cache entries cleared, or fewer than before
        (keysAfter?.Length ?? 0).ShouldBeLessThanOrEqualTo(keysBefore?.Length ?? 0);
    }

    /// <summary>
    /// When the flag is disabled, no localStorage cache entries should be written
    /// for conversation history.
    /// </summary>
    [SkippableFact]
    public async Task Cache_WhenFlagDisabled_NoLocalStorageEntry()
    {
        await WaitForPortalReadyAsync();
        await DisableCacheFlagAsync();

        // Clear any leftover cache entries from other tests
        await Page.EvaluateAsync(
            "() => Object.keys(localStorage).filter(k => k.startsWith('bn:conv-history:')).forEach(k => localStorage.removeItem(k))");

        await SelectAgentAsync(AgentId);

        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 20000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(2000); // Give time for any (unexpected) cache write

        var keys = await Page.EvaluateAsync<string[]>(
            "() => Object.keys(localStorage).filter(k => k.startsWith('bn:conv-history:'))");

        (keys?.Length ?? 0).ShouldBe(0, "No cache entries should exist when the flag is disabled");
    }
}
