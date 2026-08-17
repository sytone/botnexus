using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Coverage for issue #140: explicit channel binding management over the conversation REST API.
///
/// <para>
/// The transactional store operations (<c>AddBindingAsync</c>/<c>RemoveBindingAsync</c>/
/// <c>MoveBindingAsync</c>) already existed from #2139, but <c>MoveBindingAsync</c> had no REST
/// caller, and neither add nor remove emitted a change notification or an audit entry — so the
/// portal never learned a binding had changed and an operator could not see who moved a channel
/// address. These tests pin the whole attach / detach / move surface, including the guards that
/// keep routing unambiguous.
/// </para>
///
/// <para>
/// Every test runs the <em>real</em> <see cref="ConversationsController"/> over a real
/// <see cref="InMemoryConversationStore"/>; only the notifier and audit log are captured.
/// </para>
/// </summary>
public sealed class ConversationBindingManagementTests
{
    private static readonly AgentId Agent = AgentId.From("agent-140");

    private readonly InMemoryConversationStore _store = new();
    private readonly CapturingNotifier _notifier = new();
    private readonly CapturingAuditLog _audit = new();

    private ConversationsController CreateController()
        => new(
            _store,
            new InMemorySessionStore(),
            new IConversationChangeNotifier[] { _notifier },
            NullLogger<ConversationsController>.Instance,
            auditLog: _audit);

    private async Task<ConversationId> NewConversationAsync(string title, AgentId? agentId = null)
    {
        var id = ConversationId.Create();
        await _store.CreateAsync(new Conversation
        {
            ConversationId = id,
            AgentId = agentId ?? Agent,
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return id;
    }

    private static AddBindingRequest Binding(string address, string channelType = "telegram")
        => new(ChannelType: channelType, ChannelAddress: address, Mode: "Interactive", ThreadingMode: "Single", DisplayPrefix: null);

    private async Task<string> AttachAsync(ConversationsController controller, ConversationId id, string address, string channelType = "telegram")
    {
        var result = await controller.AddBinding(id.Value, Binding(address, channelType), CancellationToken.None);
        var created = result.ShouldBeOfType<ObjectResult>();
        created.StatusCode.ShouldBe(StatusCodes.Status201Created);
        return created.Value.ShouldBeOfType<BindingResponse>().BindingId;
    }

    // ── Attach ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddBinding_NotifiesAndAudits()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("attach target");

        var bindingId = await AttachAsync(controller, id, "1234567890");

        var loaded = await _store.GetAsync(id);
        loaded!.ChannelBindings.Single().BindingId.Value.ShouldBe(bindingId);

        _notifier.Events.ShouldContain(e => e.ChangeType == "updated" && e.ConversationId == id.Value);

        var entry = _audit.Entries.SingleOrDefault(e => e.Action == "binding_added");
        entry.ShouldNotBeNull();
        entry!.ConversationId.ShouldBe(id.Value);
        entry.Source.ShouldBe("rest-api");
        entry.NewValue.ShouldNotBeNull();
        entry.NewValue!.ShouldContain("telegram");
        entry.NewValue.ShouldContain("1234567890");
    }

    [Fact]
    public async Task AddBinding_DuplicateAddressOnSameConversation_Returns409()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("dup target");
        await AttachAsync(controller, id, "1234567890");

        var result = await controller.AddBinding(id.Value, Binding("1234567890"), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        // The rejected attach must not have been persisted.
        (await _store.GetAsync(id))!.ChannelBindings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AddBinding_AddressAlreadyBoundToAnotherConversationOfSameAgent_Returns409()
    {
        var controller = CreateController();
        var first = await NewConversationAsync("first");
        var second = await NewConversationAsync("second");
        await AttachAsync(controller, first, "1234567890");

        // Two conversations of one agent holding the same (channelType, address) would make
        // ResolveByBindingAsync ambiguous — inbound messages would route non-deterministically.
        var result = await controller.AddBinding(second.Value, Binding("1234567890"), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        (await _store.GetAsync(second))!.ChannelBindings.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddBinding_SameAddressDifferentChannelType_IsAllowed()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("multi-channel");
        await AttachAsync(controller, id, "1234567890", "telegram");

        await AttachAsync(controller, id, "1234567890", "signal");

        (await _store.GetAsync(id))!.ChannelBindings.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AddBinding_BlankChannelType_Returns400()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("bad request");

        var result = await controller.AddBinding(
            id.Value,
            new AddBindingRequest(ChannelType: "  ", ChannelAddress: "x", Mode: null, ThreadingMode: null, DisplayPrefix: null),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        (await _store.GetAsync(id))!.ChannelBindings.ShouldBeEmpty();
    }

    // ── Detach ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveBinding_NotifiesAndAudits()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("detach target");
        var bindingId = await AttachAsync(controller, id, "1234567890");
        _notifier.Events.Clear();

        var result = await controller.RemoveBinding(id.Value, bindingId, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await _store.GetAsync(id))!.ChannelBindings.ShouldBeEmpty();
        _notifier.Events.ShouldContain(e => e.ChangeType == "updated" && e.ConversationId == id.Value);

        var entry = _audit.Entries.SingleOrDefault(e => e.Action == "binding_removed");
        entry.ShouldNotBeNull();
        entry!.PreviousValue.ShouldNotBeNull();
        entry.PreviousValue!.ShouldContain("1234567890");
        entry.NewValue.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveBinding_UnknownBinding_Returns404AndDoesNotAudit()
    {
        var controller = CreateController();
        var id = await NewConversationAsync("detach target");

        var result = await controller.RemoveBinding(id.Value, "nosuchbinding", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        _audit.Entries.ShouldNotContain(e => e.Action == "binding_removed");
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveBinding_MovesBindingAndNotifiesBothConversations()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var target = await NewConversationAsync("target");
        var bindingId = await AttachAsync(controller, source, "1234567890");
        _notifier.Events.Clear();

        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(target.Value),
            CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var moved = ok.Value.ShouldBeOfType<BindingResponse>();
        moved.BindingId.ShouldBe(bindingId);
        moved.ChannelAddress.ShouldBe("1234567890");

        (await _store.GetAsync(source))!.ChannelBindings.ShouldBeEmpty();
        var targetBindings = (await _store.GetAsync(target))!.ChannelBindings;
        targetBindings.Single().BindingId.Value.ShouldBe(bindingId);

        // Both aggregates changed, so both must be announced or one portal pane goes stale.
        _notifier.Events.ShouldContain(e => e.ConversationId == source.Value);
        _notifier.Events.ShouldContain(e => e.ConversationId == target.Value);

        var entry = _audit.Entries.SingleOrDefault(e => e.Action == "binding_moved");
        entry.ShouldNotBeNull();
        entry!.ConversationId.ShouldBe(source.Value);
        entry.PreviousValue.ShouldNotBeNull();
        entry.PreviousValue!.ShouldContain(source.Value);
        entry.NewValue.ShouldNotBeNull();
        entry.NewValue!.ShouldContain(target.Value);
    }

    [Fact]
    public async Task MoveBinding_UnknownSourceConversation_Returns404()
    {
        var controller = CreateController();
        var target = await NewConversationAsync("target");

        var result = await controller.MoveBinding(
            ConversationId.Create().Value,
            "somebinding",
            new MoveBindingRequest(target.Value),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_UnknownTargetConversation_Returns404AndLeavesBindingInPlace()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var bindingId = await AttachAsync(controller, source, "1234567890");

        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(ConversationId.Create().Value),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
        (await _store.GetAsync(source))!.ChannelBindings.Single().BindingId.Value.ShouldBe(bindingId);
    }

    [Fact]
    public async Task MoveBinding_BindingNotOnSourceConversation_Returns404()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var target = await NewConversationAsync("target");
        var bindingId = await AttachAsync(controller, target, "1234567890");

        // The binding exists, but not on the conversation named in the route.
        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(target.Value),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_ToSameConversation_Returns400()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var bindingId = await AttachAsync(controller, source, "1234567890");

        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(source.Value),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        (await _store.GetAsync(source))!.ChannelBindings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MoveBinding_BlankTarget_Returns400()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var bindingId = await AttachAsync(controller, source, "1234567890");

        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest("   "),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_AcrossAgents_Returns400()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var target = await NewConversationAsync("other agent", AgentId.From("agent-other"));
        var bindingId = await AttachAsync(controller, source, "1234567890");

        // Bindings resolve inbound traffic by (agentId, channelType, address). Re-parenting a
        // binding under a different agent would silently re-route a live channel to another
        // agent's brain, so it is refused rather than performed.
        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(target.Value),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        (await _store.GetAsync(source))!.ChannelBindings.Count.ShouldBe(1);
        (await _store.GetAsync(target))!.ChannelBindings.ShouldBeEmpty();
    }

    [Fact]
    public async Task MoveBinding_TargetAlreadyBoundToSameAddress_Returns409()
    {
        var controller = CreateController();
        var source = await NewConversationAsync("source");
        var target = await NewConversationAsync("target");
        var bindingId = await AttachAsync(controller, source, "1234567890", "telegram");
        // Reach past the controller's own duplicate guard to construct the conflicting state
        // that a legacy/auto-created binding could already be in.
        await _store.AddBindingAsync(target, new ChannelBinding
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("1234567890"),
            BoundAt = DateTimeOffset.UtcNow
        });

        var result = await controller.MoveBinding(
            source.Value,
            bindingId,
            new MoveBindingRequest(target.Value),
            CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        (await _store.GetAsync(source))!.ChannelBindings.Count.ShouldBe(1);
        (await _store.GetAsync(target))!.ChannelBindings.Count.ShouldBe(1);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed record NotificationEvent(string ChangeType, string AgentId, string ConversationId);

    private sealed class CapturingNotifier : IConversationChangeNotifier
    {
        public List<NotificationEvent> Events { get; } = [];

        public Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken = default)
        {
            lock (Events)
                Events.Add(new NotificationEvent(changeType, agentId, conversationId));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAuditLog : IConversationAuditLog
    {
        private readonly ConcurrentBag<ConversationAuditEntry> _entries = [];

        public IReadOnlyList<ConversationAuditEntry> Entries => _entries.ToList();

        public Task LogAsync(ConversationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationAuditEntry>> GetAsync(string conversationId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationAuditEntry>>(
                _entries.Where(e => e.ConversationId == conversationId).ToList());
    }
}
