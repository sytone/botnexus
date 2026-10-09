using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Hooks;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #4748: Mirror admission must obey peer grants without weakening the execution policy inherited
/// from the initiating parent. These drive SpawnAsync, not a standalone authorization helper.
/// </summary>
public sealed class SubAgentMirrorPeerAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpawnAsync_WhitelistUngrantedMirror_RefusesBeforeAnyAdmissionMutation(bool shareWorkspace)
    {
        await using var fixture = new SpawnFixture(Parent(), Target());

        var failure = await Should.ThrowAsync<UnauthorizedAccessException>(
            () => fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId), shareWorkspace));

        failure.Message.ShouldContain(SpawnFixture.ParentId.Value);
        failure.Message.ShouldContain(SpawnFixture.TargetId.Value);
        await fixture.AssertNoAdmissionMutationAsync();
    }

    [Fact]
    public async Task SpawnAsync_WhitelistExplicitIdGrant_IsCaseInsensitiveAndUsesTargetDescriptor()
    {
        await using var fixture = new SpawnFixture(
            Parent() with { SubAgentIds = [SpawnFixture.TargetId.Value.ToUpperInvariant()] }, Target());

        var info = await fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId));

        await fixture.AssertAdmittedAsync(info, expectedModel: "target-model");
        fixture.ChildDescriptor.ShouldNotBeNull().SystemPrompt.ShouldBe("target-prompt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpawnAsync_WhitelistRoleGrant_AcceptsCaseInsensitiveStringAndJsonString(bool jsonRole)
    {
        object role = jsonRole ? JsonSerializer.SerializeToElement("ReViEwEr") : "ReViEwEr";
        await using var fixture = new SpawnFixture(
            Parent() with { SubAgentRoles = ["REVIEWER"] }, Target(role));

        var info = await fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId));

        await fixture.AssertAdmittedAsync(info, expectedModel: "target-model");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("object")]
    [InlineData("json-number")]
    [InlineData("json-boolean")]
    [InlineData("json-null")]
    [InlineData("json-object")]
    [InlineData("json-array")]
    [InlineData("json-undefined")]
    public async Task SpawnAsync_WhitelistMissingUnknownOrMalformedRole_RefusesWithoutMutation(string roleShape)
    {
        object? role = roleShape switch
        {
            "missing" or "null" => null,
            "unknown" => "ungranted-role",
            "empty" => "",
            "whitespace" => " ",
            "number" => 42,
            "boolean" => true,
            "object" => new RoleLookalike(),
            "json-number" => JsonSerializer.SerializeToElement(42),
            "json-boolean" => JsonSerializer.SerializeToElement(true),
            "json-null" => JsonSerializer.SerializeToElement<object?>(null),
            "json-object" => JsonSerializer.SerializeToElement(new { role = "reviewer" }),
            "json-array" => JsonSerializer.SerializeToElement(new[] { "reviewer" }),
            "json-undefined" => default(JsonElement),
            _ => throw new ArgumentOutOfRangeException(nameof(roleShape))
        };
        var target = roleShape == "missing" ? Target() : Target() with
        {
            Metadata = new Dictionary<string, object?> { ["role"] = role }
        };
        // Include the string representations of malformed scalar values: coercion through ToString
        // must not accidentally authorize them. RoleLookalike also impersonates a valid role.
        await using var fixture = new SpawnFixture(
            Parent() with { SubAgentRoles = ["reviewer", "42", "True", "true"] }, target);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId)));

        await fixture.AssertNoAdmissionMutationAsync();
    }

    [Fact]
    public async Task SpawnAsync_OpenPolicyUngrantedMirror_RemainsAdmitted()
    {
        await using var fixture = new SpawnFixture(Parent(), Target(), accessPolicy: "OpEn");

        var info = await fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId));

        await fixture.AssertAdmittedAsync(info, expectedModel: "target-model");
    }

    [Fact]
    public async Task SpawnAsync_WhitelistUngrantedEmbody_RemainsAdmittedWithParentDescriptor()
    {
        await using var fixture = new SpawnFixture(Parent(), Target());

        var info = await fixture.SpawnAsync(new Embody(SubAgentArchetype.General));

        await fixture.AssertAdmittedAsync(info, expectedModel: "parent-model");
        fixture.ChildDescriptor.ShouldNotBeNull().SystemPrompt.ShouldBe("parent-prompt");
    }

    [Fact]
    public async Task SpawnAsync_GrantedMirror_ParentDynamicDenyBlocksExecutionAtHandleCreation()
    {
        await using var fixture = new SpawnFixture(
            Parent() with { SubAgentIds = [SpawnFixture.TargetId.Value] },
            Target() with { ToolIds = ["exec", "read"] });
        fixture.Policy.SetDynamicDenyList(SpawnFixture.ParentId, ["exec"]);
        fixture.PoliciesSet.Clear();
        // The target is unrestricted. Mirror admission is not authority to escape the parent.
        (await fixture.EvaluateToolAsync(SpawnFixture.TargetId, "exec")).ShouldBeNull();
        (await fixture.EvaluateToolAsync(SpawnFixture.ParentId, "exec"))
            .ShouldNotBeNull().Denied.ShouldBeTrue();

        var info = await fixture.SpawnAsync(new Mirror(SpawnFixture.TargetId));

        await fixture.AssertAdmittedAsync(info, expectedModel: "target-model");
        fixture.ExecDecisionAtHandleCreation.ShouldNotBeNull().Denied.ShouldBeTrue();
        fixture.ExecDecisionAtHandleCreation.ShouldNotBeNull().DenyReason.ShouldNotBeNull().ShouldContain("exec");
        fixture.ReadDecisionAtHandleCreation.ShouldBeNull();
        fixture.ChildDescriptor.ShouldNotBeNull().ToolIds.ShouldContain("exec");
        fixture.PoliciesSet.ShouldHaveSingleItem().ShouldBe(AgentId.From(info.ChildAgentId.ShouldNotBeNull()));
    }

    private static AgentDescriptor Parent() => new()
    {
        AgentId = SpawnFixture.ParentId, DisplayName = "Parent", ModelId = "parent-model",
        ApiProvider = "test", SystemPrompt = "parent-prompt"
    };

    private static AgentDescriptor Target(object? role = null) => new()
    {
        AgentId = SpawnFixture.TargetId, DisplayName = "Target", ModelId = "target-model",
        ApiProvider = "test", SystemPrompt = "target-prompt",
        Metadata = role is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["role"] = role }
    };

    private sealed class RoleLookalike
    {
        public override string ToString() => "reviewer";
    }

    private sealed class SpawnFixture : IAsyncDisposable
    {
        internal static readonly AgentId ParentId = AgentId.From("mirror-access-parent");
        internal static readonly AgentId TargetId = AgentId.From("mirror-access-target");
        private static readonly SessionId ParentSession = SessionId.From("mirror-access-parent-session");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bnx-mirror-access", Guid.NewGuid().ToString("N"));
        private readonly ConcurrentDictionary<AgentId, AgentDescriptor> _descriptors = new();
        private readonly InMemorySessionStore _sessions = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ServiceProvider _services;
        private readonly string _childrenRoot;
        private readonly string _parentWorkspace;
        private readonly string _targetWorkspace;
        private readonly Mock<ISessionStore> _sessionStore = new();
        private readonly Mock<IConversationStore> _conversations = new();
        private readonly Mock<IAgentSupervisor> _supervisor = new();
        private readonly Mock<IAgentRegistry> _registry = new();
        private readonly ToolPolicyHookHandler _executionPolicy;
        internal DefaultSubAgentManager Manager { get; }
        internal DefaultToolPolicyProvider Policy { get; }
        internal ConcurrentQueue<AgentId> PoliciesSet { get; } = new();
        internal AgentDescriptor? ChildDescriptor { get; private set; }
        internal BeforeToolCallResult? ExecDecisionAtHandleCreation { get; private set; }
        internal BeforeToolCallResult? ReadDecisionAtHandleCreation { get; private set; }

        internal SpawnFixture(AgentDescriptor parent, AgentDescriptor target, string accessPolicy = "whitelist")
        {
            _childrenRoot = Path.Combine(_root, "children");
            Directory.CreateDirectory(_childrenRoot);
            var fileSystem = new FileSystem();
            var workspaces = new FileAgentWorkspaceManager(
                new BotNexusHome(fileSystem, Path.Combine(_root, "home")), fileSystem,
                Options.Create(new SubAgentOptions { WorkspaceRoot = _childrenRoot }));
            _parentWorkspace = workspaces.GetWorkspacePath(ParentId.Value);
            _targetWorkspace = workspaces.GetWorkspacePath(TargetId.Value);
            File.WriteAllText(Path.Combine(_parentWorkspace, "parent.txt"), "parent-private");
            File.WriteAllText(Path.Combine(_targetWorkspace, "target.txt"), "target-private");
            _descriptors[parent.AgentId] = parent;
            _descriptors[target.AgentId] = target;
            _registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns<AgentId>(id => _descriptors.GetValueOrDefault(id));
            _registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns<AgentId>(_descriptors.ContainsKey);
            _registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>())).Callback<AgentDescriptor>(d => _descriptors[d.AgentId] = d);
            _registry.Setup(r => r.Unregister(It.IsAny<AgentId>())).Callback<AgentId>(id => _descriptors.TryRemove(id, out _));
            _sessionStore.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns<SessionId, CancellationToken>(_sessions.GetAsync);
            _sessionStore.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
                .Returns<SessionId, AgentId, CancellationToken>(_sessions.GetOrCreateAsync);
            _sessionStore.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
                .Returns<GatewaySession, CancellationToken>(_sessions.SaveAsync);
            _sessionStore.Setup(s => s.DeleteAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns<SessionId, CancellationToken>(_sessions.DeleteAsync);
            _sessionStore.Setup(s => s.ListSubAgentSessionsAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<SubAgentRunDetail>());
            _sessionStore.Setup(s => s.SaveSubAgentSessionAsync(It.IsAny<SubAgentInfo>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _conversations.Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Conversation c, CancellationToken _) => c);
            Policy = new DefaultToolPolicyProvider(new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<DefaultToolPolicyProvider>.Instance);
            Policy.OnDynamicDenyListSet = (id, _) => PoliciesSet.Enqueue(id);
            _executionPolicy = new ToolPolicyHookHandler(Policy, NullLogger<ToolPolicyHookHandler>.Instance);
            var handle = new Mock<IAgentHandle>();
            handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>(async (_, ct) =>
                {
                    await _release.Task.WaitAsync(ct);
                    return new AgentResponse { Content = "done" };
                });
            handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns<AgentId, SessionId, CancellationToken>(async (id, session, _) =>
                {
                    ChildDescriptor = _descriptors[id];
                    Directory.Exists(workspaces.GetWorkspacePath(id.Value)).ShouldBeTrue();
                    (await _sessions.GetAsync(session)).ShouldNotBeNull();
                    // Evaluate the same production hook used before tool execution, at the first
                    // boundary where the child could execute. Checking only a stored deny list
                    // would miss a policy that was installed too late or not enforced.
                    ExecDecisionAtHandleCreation = await EvaluateToolAsync(id, "exec");
                    ReadDecisionAtHandleCreation = await EvaluateToolAsync(id, "read");
                    return handle.Object;
                });
            _supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(_supervisor.Object);
            services.AddSingleton(_registry.Object);
            services.AddSingleton(Mock.Of<IActivityBroadcaster>());
            services.AddSingleton(Mock.Of<IChannelDispatcher>());
            services.AddSingleton<IOptionsMonitor<GatewayOptions>>(new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()));
            services.AddSingleton<ILogger<DefaultSubAgentManager>>(NullLogger<DefaultSubAgentManager>.Instance);
            services.AddSingleton<IAgentWorkspaceManager>(workspaces);
            services.AddSingleton(Policy);
            services.AddSingleton(_sessionStore.Object);
            services.AddSingleton(_conversations.Object);
            services.AddSingleton<IOptions<AgentExchangeOptions>>(Options.Create(new AgentExchangeOptions { AccessPolicy = accessPolicy }));
            _services = services.BuildServiceProvider();
            // On the RED baseline the manager has no exchange-options parameter, so DI constructs
            // the existing constructor and the unauthorized spawn fails on behavior, not wiring.
            // Appending optional IOptions<AgentExchangeOptions> makes DI supply the registered
            // whitelist automatically; no uncompilable reference to the future signature is needed.
            Manager = ActivatorUtilities.CreateInstance<DefaultSubAgentManager>(_services);
        }

        internal Task<BeforeToolCallResult?> EvaluateToolAsync(AgentId id, string tool) =>
            _executionPolicy.HandleAsync(new BeforeToolCallEvent(id, tool, "mirror-policy-probe", new Dictionary<string, object?>()));

        internal Task<SubAgentInfo> SpawnAsync(SubAgentSpawnMode mode, bool shareWorkspace = false) => Manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = ParentId, ParentSessionId = ParentSession, Mode = mode,
            Task = "peer admission probe", ShareWorkspace = shareWorkspace, TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("mirror-access-parent-conversation")
        });

        internal async Task AssertNoAdmissionMutationAsync()
        {
            _registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
            _registry.Verify(r => r.Unregister(It.IsAny<AgentId>()), Times.Never);
            _descriptors.Keys.OrderBy(id => id.Value).ShouldBe(new[] { ParentId, TargetId }.OrderBy(id => id.Value));
            Directory.EnumerateFileSystemEntries(_childrenRoot).ShouldBeEmpty();
            _sessionStore.Invocations.ShouldBeEmpty();
            _conversations.Invocations.ShouldBeEmpty();
            _supervisor.Invocations.ShouldBeEmpty();
            PoliciesSet.ShouldBeEmpty();
            Manager.ActiveSubAgentCount.ShouldBe(0);
            (await Manager.ListAsync(ParentSession)).ShouldBeEmpty();
            File.ReadAllText(Path.Combine(_parentWorkspace, "parent.txt")).ShouldBe("parent-private");
            File.ReadAllText(Path.Combine(_targetWorkspace, "target.txt")).ShouldBe("target-private");
        }

        internal async Task AssertAdmittedAsync(SubAgentInfo info, string expectedModel)
        {
            info.Status.ShouldBe(SubAgentStatus.Running);
            info.Model.ShouldBe(expectedModel);
            var child = AgentId.From(info.ChildAgentId.ShouldNotBeNull());
            ChildDescriptor.ShouldNotBeNull().AgentId.ShouldBe(child);
            ChildDescriptor.ShouldNotBeNull().ModelId.ShouldBe(expectedModel);
            _registry.Verify(r => r.Register(It.Is<AgentDescriptor>(d => d.AgentId == child)), Times.Once);
            _supervisor.Verify(s => s.GetOrCreateAsync(child, info.ChildSessionId, It.IsAny<CancellationToken>()), Times.Once);
            _conversations.Verify(s => s.CreateAsync(It.Is<Conversation>(c => c.ConversationId == info.ChildConversationId), It.IsAny<CancellationToken>()), Times.Once);
            (await _sessions.GetAsync(info.ChildSessionId)).ShouldNotBeNull().ConversationId.ShouldBe(info.ChildConversationId.ShouldNotBeNull());
            (await Manager.ListAsync(ParentSession)).ShouldHaveSingleItem().SubAgentId.ShouldBe(info.SubAgentId);
        }

        public async ValueTask DisposeAsync()
        {
            // Even an unexpectedly admitted RED request must be drained before removing its files.
            var children = await Manager.ListAsync(ParentSession);
            foreach (var child in children)
                await Manager.KillAsync(child.SubAgentId, ParentSession);
            _release.TrySetResult();
            foreach (var child in children)
                await Manager.WaitForRunCompletionForTestAsync(child.SubAgentId).WaitAsync(TimeSpan.FromSeconds(30));
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
