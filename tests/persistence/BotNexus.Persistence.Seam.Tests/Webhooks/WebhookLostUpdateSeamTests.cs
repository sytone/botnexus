using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Webhooks;

/// <summary>
/// Deterministic lost-update coverage for the real webhook registration SQLite seam (issue #3327,
/// acceptance clause 5).
/// </summary>
public sealed class WebhookLostUpdateSeamTests
{
    [Fact]
    public async Task UpdateAsync_PreservesConversationPinCommittedAfterItsSnapshot()
    {
        using var fixture = new WebhookSeamStoreFixture();
        var registration = await fixture.SeedRegistrationAsync();
        var conversationId = ConversationId.From("webhook-conversation-winner");

        var result = await new LostUpdateScenario<WebhookRegistration>()
            .ReadSnapshot(() => fixture.CreateRegistrationStore().GetAsync(registration.Id))
            .ThenConcurrently(async () =>
            {
                var winner = await fixture.CreateRegistrationStore()
                    .TryPinConversationAsync(registration.Id, conversationId);
                winner.ShouldBe(conversationId);
            })
            .ThenStaleWrite(async snapshot =>
            {
                snapshot.PinnedConversationId.ShouldBeNull();
                await fixture.CreateRegistrationStore().UpdateAsync(snapshot with { Label = "renamed" });
            })
            .VerifyBy(() => fixture.CreateRegistrationStore().GetAsync(registration.Id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted);
        var committed = result.Committed.ShouldNotBeNull();
        committed.Label.ShouldBe("renamed");
        committed.PinnedConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public async Task UpdateAsync_PreservesLastUsedCommittedAfterItsSnapshot()
    {
        using var fixture = new WebhookSeamStoreFixture();
        var registration = await fixture.SeedRegistrationAsync();
        var usedAt = new DateTimeOffset(2026, 9, 30, 4, 30, 0, TimeSpan.Zero);

        var result = await new LostUpdateScenario<WebhookRegistration>()
            .ReadSnapshot(() => fixture.CreateRegistrationStore().GetAsync(registration.Id))
            .ThenConcurrently(() => fixture.CreateRegistrationStore().TouchLastUsedAsync(registration.Id, usedAt))
            .ThenStaleWrite(async snapshot =>
            {
                snapshot.LastUsedAt.ShouldBeNull();
                await fixture.CreateRegistrationStore().UpdateAsync(snapshot with { Enabled = false });
            })
            .VerifyBy(() => fixture.CreateRegistrationStore().GetAsync(registration.Id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted);
        var committed = result.Committed.ShouldNotBeNull();
        committed.Enabled.ShouldBeFalse();
        committed.LastUsedAt.ShouldBe(usedAt);
    }
}
