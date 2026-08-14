using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Cron;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Cron;

/// <summary>
/// #3168: <see cref="ICronAlertTargetResolver"/> had no production implementation and no DI
/// registration, so <c>CronAlertTarget.ValidateAsync</c> always took its fail-closed branch and
/// <c>failureAlertConversationId</c> could never be set on any cron job. These tests pin both the
/// resolver's behaviour and the registration that makes it reachable.
/// </summary>
public sealed class ConversationCronAlertTargetResolverTests
{
    // AC1 happy path: an existing, active conversation resolves.
    [Fact]
    public async Task ExistsAsync_ForExistingActiveConversation_ReturnsTrue()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("c_exists");
        await store.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From("agent-a") });
        var resolver = CreateResolver(store);

        (await resolver.ExistsAsync(id)).ShouldBeTrue();
    }

    // AC4 sad path: a missing conversation is unresolvable, so validation rejects naming the id.
    [Fact]
    public async Task ExistsAsync_ForMissingConversation_ReturnsFalse()
    {
        var resolver = CreateResolver(new InMemoryConversationStore());

        (await resolver.ExistsAsync(ConversationId.From("c_missing"))).ShouldBeFalse();
    }

    // An archived conversation is a retired destination: accepting it would store a target that
    // delivers where nobody is looking - exactly what the fail-closed guard exists to prevent.
    [Fact]
    public async Task ExistsAsync_ForArchivedConversation_ReturnsFalse()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("c_archived");
        await store.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From("agent-a") });
        await store.ArchiveAsync(id);
        var resolver = CreateResolver(store);

        (await resolver.ExistsAsync(id)).ShouldBeFalse();
    }

    // Cross-agent targeting is legitimate: a job owned by one agent may alert into an operator or
    // supervisor conversation owned by another. Ownership must NOT be part of resolution.
    [Fact]
    public async Task ExistsAsync_ForConversationOwnedByAnotherAgent_ReturnsTrue()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("c_other_agent");
        await store.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From("agent-b") });
        var resolver = CreateResolver(store);

        (await resolver.ExistsAsync(id)).ShouldBeTrue();
    }

    // The end-to-end contract the issue reproduced live: validation ACCEPTS a real target and
    // REJECTS an unknown one naming the id, rather than complaining no resolver is available.
    [Fact]
    public async Task ValidateAsync_WithProductionResolver_AcceptsRealTarget_AndRejectsUnknownOneByName()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("c_real");
        await store.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From("agent-a") });
        var resolver = CreateResolver(store);

        var ok = await CronAlertTarget.ValidateAsync(resolver, id);
        ok.IsValid.ShouldBeTrue();
        ok.Error.ShouldBeNull();

        var bad = await CronAlertTarget.ValidateAsync(resolver, ConversationId.From("c_typo"));
        bad.IsValid.ShouldBeFalse();
        bad.Error.ShouldBe(CronAlertTarget.UnresolvableMessage("c_typo"));
        bad.Error!.ShouldNotContain("cannot be verified");
    }

    // AC6/AC7: removing the DI registration must redden a named test rather than silently
    // disabling cron alerting again.
    [Fact]
    public void AddBotNexusGateway_RegistersCronAlertTargetResolver()
    {
        var services = new ServiceCollection();

        services.AddBotNexusGateway();

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ICronAlertTargetResolver));
        descriptor.ShouldNotBeNull();
        descriptor!.ImplementationType.ShouldBe(typeof(ConversationCronAlertTargetResolver));
    }

    private static ConversationCronAlertTargetResolver CreateResolver(IConversationStore store)
        => new(store, NullLogger<ConversationCronAlertTargetResolver>.Instance);
}
