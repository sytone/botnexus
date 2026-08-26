using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Webhooks;
using BotNexus.Gateway.Webhooks;
using NSubstitute;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>
/// #3523: per-agent outbound webhook registrations must be reconciled from agent lifecycle
/// instead of wired by a hand-run setup script that never re-ran on create, rename or delete.
/// </summary>
public sealed class AgentWebhookProvisionerTests
{
    // ── Startup reconciliation (AC2) ──────────────────────────────────────────

    [Fact]
    public async Task StartAsync_InitializesStoreAndProvisionsEveryRegisteredAgent()
    {
        var store = new FakeWebhookRegistrationStore();
        var registry = RegistryWith("agent-a", "agent-b", "agent-c");
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(registry, store, [target]);

        await provisioner.StartAsync(CancellationToken.None);

        store.InitializeCalls.ShouldBe(1);
        // One registration and one downstream push per agent - the whole point of the startup
        // pass is that it is also the recovery path for a target that was offline earlier.
        store.CreateCalls.ShouldBe(3);
        target.Notified.Select(b => b.AgentId.Value).OrderBy(v => v)
            .ShouldBe(["agent-a", "agent-b", "agent-c"]);
    }

    [Fact]
    public async Task StartAsync_WithEmptyRegistry_StillInitializesStoreAndMakesNoCalls()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, [target]);

        await provisioner.StartAsync(CancellationToken.None);

        store.InitializeCalls.ShouldBe(1);
        store.CreateCalls.ShouldBe(0);
        target.Notified.ShouldBeEmpty();
    }

    // ── Idempotency (AC3) ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProvisionAsync_RunTwiceForSameDescriptor_CreatesExactlyOneRegistration()
    {
        var store = new FakeWebhookRegistrationStore();
        var provisioner = Provisioner(store);
        var descriptor = Descriptor("agent-a");

        await provisioner.ProvisionAsync(descriptor, CancellationToken.None);
        await provisioner.ProvisionAsync(descriptor, CancellationToken.None);

        // The second pass must find the labelled registration and leave the store alone.
        // Without the label lookup this mints a second secret and breaks the live binding.
        store.CreateCalls.ShouldBe(1);
        store.Registrations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ProvisionAsync_UsesDeterministicAgentIdKeyedLabel()
    {
        var store = new FakeWebhookRegistrationStore();
        await Provisioner(store).ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);

        // The key is the IMMUTABLE agent id, not the display name: an agent id cannot be
        // renamed in BotNexus, so this key is stable by construction.
        store.Registrations.Single().Label.ShouldBe("agent-webhook:agent-a");
    }

    // ── Secret preservation on rename (AC4) ───────────────────────────────────

    [Fact]
    public async Task ProvisionAsync_WhenDisplayNameChanged_DoesNotWriteStoreAndReusesSecret()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, [target]);

        await provisioner.ProvisionAsync(Descriptor("agent-a", "Original Name"), CancellationToken.None);
        var originalSecret = store.Registrations.Single().Secret;

        await provisioner.ProvisionAsync(Descriptor("agent-a", "Renamed Agent"), CancellationToken.None);

        store.CreateCalls.ShouldBe(1);
        store.UpdateCalls.ShouldBe(0);
        target.Notified.Count.ShouldBe(2);
        // Byte-identical secret across the rename. A new secret here would silently break the
        // binding the downstream system already holds - the exact hazard the manual script had.
        target.Notified[1].Secret.ShouldBe(originalSecret);
        target.Notified[1].Secret.ShouldBe(target.Notified[0].Secret);
        // The new display name still reaches the target, otherwise it shows a stale name forever.
        target.Notified[1].DisplayName.ShouldBe("Renamed Agent");
    }

    [Fact]
    public async Task ProvisionAsync_IgnoresRegistrationsItDoesNotOwn()
    {
        var store = new FakeWebhookRegistrationStore();
        // An operator-authored webhook for the same agent, without the provisioner's label.
        await store.CreateAsync(new WebhookRegistration
        {
            Id = WebhookId.Create(),
            Label = "hand-made-by-operator",
            AgentId = AgentId.From("agent-a"),
            Secret = "whsec_operator",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await Provisioner(store).ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);

        // The unlabelled registration must not be mistaken for ours, so a new one is created.
        store.Registrations.Count.ShouldBe(2);
        store.Registrations.ShouldContain(r => r.Label == "agent-webhook:agent-a");
        store.Registrations.ShouldContain(r => r.Secret == "whsec_operator");
    }

    [Fact]
    public async Task ProvisionAsync_PublishesBindingWithInboundPathAndWebhookId()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        await new AgentWebhookProvisioner(RegistryWith(), store, [target])
            .ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);

        var created = store.Registrations.Single();
        var binding = target.Notified.Single();
        binding.WebhookId.ShouldBe(created.Id.Value);
        binding.InboundPath.ShouldBe($"/api/webhooks/agent-a/{created.Id.Value}");
    }

    // ── Deprovision ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeprovisionAsync_RemovesOwnedRegistrationAndNotifiesTarget()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, [target]);
        await provisioner.ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);
        var provisionedWebhookId = store.Registrations.Single().Id.Value;

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        store.Registrations.ShouldBeEmpty();
        target.Removed.ShouldBe([(AgentId.From("agent-a"), provisionedWebhookId)]);
    }

    [Fact]
    public async Task DeprovisionAsync_NotificationCarriesTheExactRemovedRegistrationId()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, [target]);
        await provisioner.ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);

        // The id the target was TOLD about on create is the id it must be told about on delete.
        var notifiedOnCreate = target.Notified.Single().WebhookId;

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        var (removedAgentId, removedWebhookId) = target.Removed.ShouldHaveSingleItem();
        removedAgentId.ShouldBe(AgentId.From("agent-a"));
        // Agent id alone is NOT a safe delete key: ids are immutable and therefore reusable, so a
        // delayed or retried delete would otherwise erase a recreated agent's newer binding. The
        // webhook id is the generation token that lets the target delete conditionally.
        removedWebhookId.ShouldBe(notifiedOnCreate);
        removedWebhookId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeprovisionAsync_AfterRecreate_CarriesTheNewGenerationIdNotTheOld()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, [target]);

        // Generation 1: provision, then delete the agent.
        await provisioner.ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);
        var firstGeneration = store.Registrations.Single().Id.Value;
        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        // Generation 2: an agent with the SAME id is recreated and gets a fresh registration.
        await provisioner.ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);
        var secondGeneration = store.Registrations.Single().Id.Value;
        secondGeneration.ShouldNotBe(firstGeneration);

        await provisioner.DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        target.Removed.Count.ShouldBe(2);
        target.Removed[0].WebhookId.ShouldBe(firstGeneration);
        // This is the whole safety contract: the second delete names the SECOND generation. A
        // downstream target deleting WHERE agent = ? AND webhook_id = ? can therefore replay the
        // stale first delete after the recreate and correctly match nothing.
        target.Removed[1].WebhookId.ShouldBe(secondGeneration);
    }

    [Fact]
    public async Task DeprovisionAsync_WithMultipleOwnedRegistrations_NotifiesOncePerRegistration()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        // A store that somehow holds two labelled registrations (e.g. a historical duplicate)
        // must produce one identified removal per registration, not one blanket per-agent delete.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            await store.CreateAsync(new WebhookRegistration
            {
                Id = WebhookId.Create(),
                Label = "agent-webhook:agent-a",
                AgentId = AgentId.From("agent-a"),
                Secret = "whsec_x",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        var ids = store.Registrations.Select(r => r.Id.Value).ToList();

        await new AgentWebhookProvisioner(RegistryWith(), store, [target])
            .DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        target.Removed.Select(r => r.WebhookId).OrderBy(v => v).ShouldBe(ids.OrderBy(v => v));
    }

    [Fact]
    public async Task DeprovisionAsync_LeavesRegistrationsItDoesNotOwn()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();
        await store.CreateAsync(new WebhookRegistration
        {
            Id = WebhookId.Create(),
            Label = "hand-made-by-operator",
            AgentId = AgentId.From("agent-a"),
            Secret = "whsec_operator",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await new AgentWebhookProvisioner(RegistryWith(), store, [target])
            .DeprovisionAsync(AgentId.From("agent-a"), CancellationToken.None);

        // Same ownership guard the cron provisioners apply to a non-system job (#3524).
        store.Registrations.Count.ShouldBe(1);
        // And with nothing of ours removed there is no registration id to name, so the target is
        // not told anything - a bare per-agent delete here would destroy the operator's binding.
        target.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeprovisionAsync_WhenNothingProvisioned_IsNoOp()
    {
        var store = new FakeWebhookRegistrationStore();
        var target = new RecordingTarget();

        await new AgentWebhookProvisioner(RegistryWith(), store, [target])
            .DeprovisionAsync(AgentId.From("never-existed"), CancellationToken.None);

        store.DeleteCalls.ShouldBe(0);
        target.Removed.ShouldBeEmpty();
    }

    // ── Unconfigured host (AC8) ───────────────────────────────────────────────

    [Fact]
    public async Task ProvisionAsync_WithNoTargetsConfigured_StillProvisionsAndDoesNotThrow()
    {
        var store = new FakeWebhookRegistrationStore();
        var provisioner = new AgentWebhookProvisioner(RegistryWith(), store, targets: null);

        await provisioner.ProvisionAsync(Descriptor("agent-a"), CancellationToken.None);

        store.CreateCalls.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentWebhookProvisioner Provisioner(IWebhookRegistrationStore store)
        => new(RegistryWith(), store, [new RecordingTarget()]);

    private static IAgentRegistry RegistryWith(params string[] agentIds)
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns(agentIds.Select(id => Descriptor(id)).ToList());
        return registry;
    }

    private static AgentDescriptor Descriptor(string agentId, string displayName = "Agent")
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = displayName,
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private sealed class RecordingTarget : IAgentWebhookTargetNotifier
    {
        public List<AgentWebhookBinding> Notified { get; } = [];

        /// <summary>
        /// Removals are recorded as (agent, webhook) PAIRS. Recording the agent id alone would
        /// make every stale-generation assertion in this file vacuous.
        /// </summary>
        public List<(AgentId AgentId, string WebhookId)> Removed { get; } = [];

        public Task NotifyAsync(AgentWebhookBinding binding, CancellationToken cancellationToken)
        {
            Notified.Add(binding);
            return Task.CompletedTask;
        }

        public Task NotifyRemovedAsync(AgentId agentId, string webhookId, CancellationToken cancellationToken)
        {
            Removed.Add((agentId, webhookId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory store. A hand-written fake rather than a mock because these tests assert on the
    /// store's resulting CONTENT (one registration, secret unchanged), not just on call sequences.
    /// </summary>
    private sealed class FakeWebhookRegistrationStore : IWebhookRegistrationStore
    {
        public List<WebhookRegistration> Registrations { get; } = [];
        public int InitializeCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public Task<WebhookRegistration> CreateAsync(WebhookRegistration registration, CancellationToken ct = default)
        {
            CreateCalls++;
            Registrations.Add(registration);
            return Task.FromResult(registration);
        }

        public Task<WebhookRegistration?> GetAsync(WebhookId webhookId, CancellationToken ct = default)
            => Task.FromResult(Registrations.FirstOrDefault(r => r.Id == webhookId));

        public Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WebhookRegistration>>(
                Registrations.Where(r => agentId is null || r.AgentId == agentId).ToList());

        public Task<WebhookRegistration> UpdateAsync(WebhookRegistration registration, CancellationToken ct = default)
        {
            UpdateCalls++;
            var index = Registrations.FindIndex(r => r.Id == registration.Id);
            if (index >= 0)
                Registrations[index] = registration;
            return Task.FromResult(registration);
        }

        public Task TouchLastUsedAsync(WebhookId webhookId, DateTimeOffset lastUsedAt, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(WebhookId webhookId, CancellationToken ct = default)
        {
            DeleteCalls++;
            Registrations.RemoveAll(r => r.Id == webhookId);
            return Task.CompletedTask;
        }

        public Task<ConversationId?> TryPinConversationAsync(
            WebhookId webhookId, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult<ConversationId?>(conversationId);
    }
}
