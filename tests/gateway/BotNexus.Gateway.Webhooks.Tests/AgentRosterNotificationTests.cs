using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Webhooks;
using BotNexus.Gateway.Webhooks;
using NSubstitute;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>#3714 RED tests for terminal full-roster notification.</summary>
public sealed class AgentRosterNotificationTests
{
    [Fact]
    public async Task StartAsync_Success_NotifiesCanonicalCompleteRoster()
    {
        var target = Substitute.For<IAgentWebhookTargetNotifier>();
        var provisioner = Create(target, new FakeStore(),
            Descriptor("agent-b", "Agent B"), Descriptor("agent-a", "Agent A"));

        await provisioner.StartAsync(CancellationToken.None);

        await AssertRosterAsync(target, ("agent-a", "Agent A"), ("agent-b", "Agent B"));
    }

    [Fact]
    public async Task StartAsync_EmptyRegistry_NotifiesSuccessfulEmptyRoster()
    {
        var target = Substitute.For<IAgentWebhookTargetNotifier>();

        await Create(target, new FakeStore()).StartAsync(CancellationToken.None);

        await AssertRosterAsync(target);
    }

    [Fact]
    public async Task StartAsync_Failure_NotifiesBoundedCodeWithoutRawExceptionUrlOrSecret()
    {
        const string secret = "whsec-do-not-leak";
        const string url = "https://private.example/hook?token=do-not-leak";
        var target = Substitute.For<IAgentWebhookTargetNotifier>();
        var provisioner = Create(target,
            new ThrowingStore(new InvalidOperationException($"store failed {secret} at {url}")),
            Descriptor("agent-a", "Agent A"));

        _ = await CaptureAsync(() => provisioner.StartAsync(CancellationToken.None));

        await target.Received(1).NotifyRosterFailedAsync(
            Arg.Is<string>(code => !string.IsNullOrWhiteSpace(code) && code.Length <= 64
                && !code.Contains(secret, StringComparison.Ordinal)
                && !code.Contains(url, StringComparison.Ordinal)
                && !code.Contains("store failed", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().NotifyRosterSucceededAsync(default!, default, default);
    }

    [Fact]
    public async Task StartAsync_DuplicateCanonicalIds_CollapseDeterministically()
    {
        var first = await CaptureRosterAsync(
            Descriptor("agent-a", "Zulu"), Descriptor(" agent-a ", "Alpha"));
        var reversed = await CaptureRosterAsync(
            Descriptor(" agent-a ", "Alpha"), Descriptor("agent-a", "Zulu"));

        first.ShouldHaveSingleItem().AgentId.ShouldBe(AgentId.From("agent-a"));
        reversed.ShouldBe(first);
    }

    [Fact]
    public async Task ProvisionAndDeprovision_AfterCreateRenameRemove_RefreshCurrentRoster()
    {
        var target = Substitute.For<IAgentWebhookTargetNotifier>();
        var registry = new MutableRegistry(Descriptor("agent-a", "Old"));
        var provisioner = new AgentWebhookProvisioner(registry, new FakeStore(), [target]);

        registry.Register(Descriptor("agent-b", "Agent B"));
        await provisioner.ProvisionAsync(Descriptor("agent-b", "Agent B"), CancellationToken.None);
        await AssertRosterAsync(target, ("agent-a", "Old"), ("agent-b", "Agent B"));
        target.ClearReceivedCalls();

        registry.Update(AgentId.From("agent-a"), Descriptor("agent-a", "New"));
        await provisioner.ProvisionAsync(Descriptor("agent-a", "New"), CancellationToken.None);
        await AssertRosterAsync(target, ("agent-a", "New"), ("agent-b", "Agent B"));
        target.ClearReceivedCalls();

        registry.Unregister(AgentId.From("agent-b"));
        await provisioner.DeprovisionAsync(AgentId.From("agent-b"), CancellationToken.None);
        await AssertRosterAsync(target, ("agent-a", "New"));
    }

    private static AgentWebhookProvisioner Create(
        IAgentWebhookTargetNotifier target, IWebhookRegistrationStore store,
        params AgentDescriptor[] descriptors)
        => new(new MutableRegistry(descriptors), store, [target]);

    private static async Task<IReadOnlyList<AgentRosterEntry>> CaptureRosterAsync(
        params AgentDescriptor[] descriptors)
    {
        var target = Substitute.For<IAgentWebhookTargetNotifier>();
        await Create(target, new FakeStore(), descriptors).StartAsync(CancellationToken.None);
        var call = target.ReceivedCalls().Single(received =>
            received.GetMethodInfo().Name == nameof(IAgentWebhookTargetNotifier.NotifyRosterSucceededAsync));
        return (IReadOnlyList<AgentRosterEntry>)call.GetArguments()[0]!;
    }

    private static Task AssertRosterAsync(IAgentWebhookTargetNotifier target,
        params (string AgentId, string DisplayName)[] expected)
        => target.Received(1).NotifyRosterSucceededAsync(
            Arg.Is<IReadOnlyList<AgentRosterEntry>>(entries => MatchesRoster(entries, expected)),
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

    private static bool MatchesRoster(
        IReadOnlyList<AgentRosterEntry> entries,
        IReadOnlyList<(string AgentId, string DisplayName)> expected)
        => entries.Count == expected.Count && entries
            .Select((entry, index) => entry.AgentId.Value == expected[index].AgentId
                && entry.DisplayName == expected[index].DisplayName)
            .All(matches => matches);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static AgentDescriptor Descriptor(string id, string name) => new()
    {
        AgentId = AgentId.From(id), DisplayName = name,
        ModelId = "test-model", ApiProvider = "test-provider"
    };

    private sealed class MutableRegistry(params AgentDescriptor[] values) : IAgentRegistry
    {
        private readonly List<AgentDescriptor> _values = [.. values];
        public void Register(AgentDescriptor value) => _values.Add(value);
        public void Unregister(AgentId id) => _values.RemoveAll(value => value.AgentId == id);
        public bool Update(AgentId id, AgentDescriptor value)
        {
            var index = _values.FindIndex(item => item.AgentId == id);
            if (index < 0) return false;
            _values[index] = value;
            return true;
        }
        public AgentDescriptor? Get(AgentId id) => _values.FirstOrDefault(value => value.AgentId == id);
        public IReadOnlyList<AgentDescriptor> GetAll() => _values;
        public bool Contains(AgentId id) => _values.Any(value => value.AgentId == id);
    }

    private class FakeStore : IWebhookRegistrationStore
    {
        private readonly List<WebhookRegistration> _values = [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<WebhookRegistration> CreateAsync(WebhookRegistration value, CancellationToken ct = default)
        { _values.Add(value); return Task.FromResult(value); }
        public Task<WebhookRegistration?> GetAsync(WebhookId id, CancellationToken ct = default)
            => Task.FromResult(_values.FirstOrDefault(value => value.Id == id));
        public virtual Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? id = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WebhookRegistration>>(_values.Where(value => id is null || value.AgentId == id).ToList());
        public Task<WebhookRegistration> UpdateAsync(WebhookRegistration value, CancellationToken ct = default)
            => Task.FromResult(value);
        public Task TouchLastUsedAsync(WebhookId id, DateTimeOffset at, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DeleteAsync(WebhookId id, CancellationToken ct = default)
        { _values.RemoveAll(value => value.Id == id); return Task.CompletedTask; }
        public Task<ConversationId?> TryPinConversationAsync(WebhookId id, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult<ConversationId?>(conversationId);
    }

    private sealed class ThrowingStore(Exception exception) : FakeStore
    {
        public override Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? id = null, CancellationToken ct = default)
            => Task.FromException<IReadOnlyList<WebhookRegistration>>(exception);
    }
}
