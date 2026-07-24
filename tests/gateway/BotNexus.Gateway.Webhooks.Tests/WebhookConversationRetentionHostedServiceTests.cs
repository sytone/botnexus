using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookConversationRetentionHostedServiceTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────────

    /// <summary>Minimal in-memory conversation store exercising only the members retention uses.</summary>
    private sealed class FakeConversationStore : IConversationStore
    {
        private readonly ConcurrentDictionary<string, Conversation> _store = new(StringComparer.Ordinal);

        public void Seed(Conversation c) => _store[c.ConversationId.Value] = c;

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(conversationId.Value, out var c) ? c : null);

        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Conversation>>(_store.Values.ToList());

        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        {
            if (_store.TryGetValue(conversationId.Value, out var c))
                c.Status = ConversationStatus.Archived;
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(ConversationId conversationId, string source, string? correlationId, string actor, CancellationToken ct = default)
            => ArchiveAsync(conversationId, ct);

        // Unused members.
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Conversation>>([]);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        { Seed(conversation); return Task.FromResult(conversation); }
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
        { Seed(conversation); return Task.CompletedTask; }
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
            => Task.FromResult<Conversation?>(null);
        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default) => Task.CompletedTask;
        public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult<Dictionary<string, JsonElement>?>(null);
        public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Minimal registration store for retention: only GetAsync is exercised.</summary>
    private sealed class FakeRegistrationStore : IWebhookRegistrationStore
    {
        private readonly Dictionary<string, WebhookRegistration> _regs = new(StringComparer.Ordinal);

        public void Seed(WebhookRegistration r) => _regs[r.Id.Value] = r;

        public Task<WebhookRegistration?> GetAsync(WebhookId webhookId, CancellationToken ct = default)
            => Task.FromResult(_regs.TryGetValue(webhookId.Value, out var r) ? r : null);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<WebhookRegistration> CreateAsync(WebhookRegistration registration, CancellationToken ct = default)
        { Seed(registration); return Task.FromResult(registration); }
        public Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WebhookRegistration>>(_regs.Values.ToList());
        public Task<WebhookRegistration> UpdateAsync(WebhookRegistration registration, CancellationToken ct = default)
        { Seed(registration); return Task.FromResult(registration); }
        public Task TouchLastUsedAsync(WebhookId webhookId, DateTimeOffset lastUsedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(WebhookId webhookId, CancellationToken ct = default) { _regs.Remove(webhookId.Value); return Task.CompletedTask; }
        public Task<ConversationId?> TryPinConversationAsync(WebhookId webhookId, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult<ConversationId?>(conversationId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WebhookConversationRetentionHostedService CreateService(
        FakeConversationStore convStore,
        FakeRegistrationStore regStore,
        WebhookConversationRetentionOptions options,
        IConversationChangeNotifier? notifier = null)
        => new(
            convStore,
            regStore,
            notifier is null ? null : [notifier],
            Options.Create(options),
            NullLogger<WebhookConversationRetentionHostedService>.Instance);

    private static Conversation WebhookConversation(
        string conversationId,
        string webhookId,
        double inactiveDays,
        bool pinnedByUser = false,
        ConversationStatus status = ConversationStatus.Active)
    {
        var conv = new Conversation
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From("a-1"),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-inactiveDays),
            Status = status,
            IsPinned = pinnedByUser,
        };
        WebhookConversationProvenance.Stamp(conv.Metadata, WebhookId.From(webhookId));
        return conv;
    }

    private static WebhookRegistration Registration(string webhookId, string? canonicalConversationId, bool enabled)
        => new()
        {
            Id = WebhookId.From(webhookId),
            Label = "test",
            AgentId = AgentId.From("a-1"),
            Secret = "s",
            Enabled = enabled,
            PinnedConversationId = canonicalConversationId is null ? null : ConversationId.From(canonicalConversationId),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static WebhookConversationRetentionOptions EnabledOptions() => new()
    {
        Enabled = true,
        DisabledRegistrationInactivityDays = 7,
        OrphanInactivityDays = 1,
    };

    // ── Disabled policy is a no-op ────────────────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnce_WhenPolicyDisabled_ArchivesNothing()
    {
        var conv = WebhookConversation("c-1", "wh_1", inactiveDays: 90);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        var options = EnabledOptions();
        options.Enabled = false;

        var archived = await CreateService(convStore, regStore, options).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Active canonical registration is protected ────────────────────────────────

    [Fact]
    public async Task RunRetentionOnce_ActiveCanonicalRegistration_IsProtected()
    {
        var conv = WebhookConversation("c-1", "wh_1", inactiveDays: 365);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        regStore.Seed(Registration("wh_1", canonicalConversationId: "c-1", enabled: true));

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Disabled registration canonical conversation ages out after window ────────

    [Fact]
    public async Task RunRetentionOnce_DisabledRegistration_ArchivesAfterWindow()
    {
        var conv = WebhookConversation("c-1", "wh_1", inactiveDays: 10);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        regStore.Seed(Registration("wh_1", canonicalConversationId: "c-1", enabled: false));

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(1);
        conv.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task RunRetentionOnce_DisabledRegistration_WithinWindow_NotArchived()
    {
        var conv = WebhookConversation("c-1", "wh_1", inactiveDays: 3);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        regStore.Seed(Registration("wh_1", canonicalConversationId: "c-1", enabled: false));

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Deleted registration conversation ages out on disabled window ─────────────

    [Fact]
    public async Task RunRetentionOnce_DeletedRegistration_ArchivesAfterWindow()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 10);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore(); // no registration seeded => deleted

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(1);
        conv.Status.ShouldBe(ConversationStatus.Archived);
    }

    // ── Orphan (registration exists, not canonical) ages out aggressively ─────────

    [Fact]
    public async Task RunRetentionOnce_OrphanRaceConversation_ArchivesAggressively()
    {
        // Registration is enabled but its canonical conversation is a DIFFERENT id; this row is
        // an unreferenced race/orphan and should age out on the short orphan window.
        var conv = WebhookConversation("c-orphan", "wh_1", inactiveDays: 2);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        regStore.Seed(Registration("wh_1", canonicalConversationId: "c-canonical", enabled: true));

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(1);
        conv.Status.ShouldBe(ConversationStatus.Archived);
    }

    // ── User-pinned conversations are never archived ──────────────────────────────

    [Fact]
    public async Task RunRetentionOnce_UserPinned_NeverArchived()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 365, pinnedByUser: true);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore(); // deleted registration

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Legacy conversations (no provenance) are ignored ──────────────────────────

    [Fact]
    public async Task RunRetentionOnce_LegacyConversationWithoutProvenance_Ignored()
    {
        var conv = new Conversation
        {
            ConversationId = ConversationId.From("c-legacy"),
            AgentId = AgentId.From("a-1"),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-365),
            Status = ConversationStatus.Active,
            Title = "Webhook: looks like one but no provenance",
        };
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();

        var archived = await CreateService(convStore, regStore, EnabledOptions()).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Already-archived rows are skipped ─────────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnce_AlreadyArchived_Skipped()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 365, status: ConversationStatus.Archived);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();

        var archived = await CreateService(convStore, regStore, EnabledOptions(), notifier).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        await notifier.DidNotReceive().NotifyConversationChangedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Archival emits change notification ────────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnce_WhenArchived_EmitsChangeNotification()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 30);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();

        await CreateService(convStore, regStore, EnabledOptions(), notifier).RunRetentionOnceAsync();

        await notifier.Received(1).NotifyConversationChangedAsync(
            "archived", "a-1", "c-1", Arg.Any<CancellationToken>());
    }

    // ── No notifiers registered: archives without throwing ────────────────────────

    [Fact]
    public async Task RunRetentionOnce_NoNotifiers_ArchivesWithoutThrowing()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 30);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();

        var archived = await CreateService(convStore, regStore, EnabledOptions(), notifier: null).RunRetentionOnceAsync();

        archived.ShouldBe(1);
        conv.Status.ShouldBe(ConversationStatus.Archived);
    }

    // ── Disabled sub-rule (window <= 0) disables that rule ────────────────────────

    [Fact]
    public async Task RunRetentionOnce_DisabledWindowZero_DoesNotArchiveDeletedRegistration()
    {
        var conv = WebhookConversation("c-1", "wh_gone", inactiveDays: 365);
        var convStore = new FakeConversationStore();
        convStore.Seed(conv);
        var regStore = new FakeRegistrationStore();
        var options = EnabledOptions();
        options.DisabledRegistrationInactivityDays = 0;

        var archived = await CreateService(convStore, regStore, options).RunRetentionOnceAsync();

        archived.ShouldBe(0);
        conv.Status.ShouldBe(ConversationStatus.Active);
    }
}
