using System.Reflection;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Commands;

/// <summary>
/// #3228: the <c>/model</c> slash command persisted an arbitrary string as a conversation's
/// <c>ModelOverride</c> with no registry check, and wrote it with a whole-record
/// <c>SaveAsync</c> rather than the narrow <c>PatchOverrideAsync</c> introduced by #2139.
///
/// <para>Both halves are load-bearing. Without validation a typo is stored, and the next turn
/// throws inside <c>InProcessIsolationStrategy</c> while CONSTRUCTING the agent - before any
/// command in that turn is dispatched - so <c>/model clear</c> can never reach its own handler
/// and the conversation is unusable from inside the product. Without the patch primitive, a
/// pin or metadata mutation committed between the handler's read and its write is silently
/// reverted, which is exactly the defect #2139 closed for the REST path only.</para>
///
/// <para>Tests drive the internal contributor by reflection, matching the established shape in
/// <see cref="BuiltInCommandContributorTests"/>, and use the REAL in-memory conversation store
/// so persistence assertions read back what the handler actually committed.</para>
/// </summary>
public sealed class ModelOverrideCommandValidationTests
{
    private const string AgentIdValue = "test-agent";
    private const string ProviderKey = "github-copilot";
    private const string RegisteredModel = "claude-sonnet-4";
    private const string UnregisteredModel = "gpt5.6-sol-typo";

    private static readonly ConversationId CommandConversationId = ConversationId.From("c_3228_command");
    private static readonly ConversationId RestConversationId = ConversationId.From("c_3228_rest");

    /// <summary>
    /// Clause 1 (sad path): an unregistered id is refused and NOTHING is written. The stored
    /// override must still be the previously-good value, not the typo.
    /// </summary>
    [Fact]
    public async Task ModelCommand_UnregisteredModel_ReturnsErrorAndLeavesOverrideUnchanged()
    {
        var harness = await Harness.CreateAsync(existingModelOverride: RegisteredModel);

        var result = await harness.ExecuteAsync("/model", UnregisteredModel);

        result.IsError.ShouldBeTrue(
            "#3228 clause 1: an unregistered model id must be refused by the command, exactly as " +
            "PUT /api/conversations/{id}/override refuses it with 400.");
        result.Body.ShouldContain(
            UnregisteredModel,
            customMessage: "The rejection must name the id that was rejected; a generic failure gives " +
            "the operator nothing to correct.");

        var persisted = await harness.ReadConversationAsync();
        persisted.ModelOverride.ShouldBe(
            RegisteredModel,
            "#3228 clause 1: a rejected override must leave the stored value untouched. Storing the " +
            "typo is what bricks the conversation - the next turn throws during agent construction.");
    }

    /// <summary>Clause 2 (happy path): a registered id is still accepted and persisted.</summary>
    [Fact]
    public async Task ModelCommand_RegisteredModel_SetsOverride()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.ExecuteAsync("/model", RegisteredModel);

        result.IsError.ShouldBeFalse("A registered model must still be settable; the guard must not over-reject.");
        (await harness.ReadConversationAsync()).ModelOverride.ShouldBe(RegisteredModel);
    }

    /// <summary>Clause 2: bare <c>/model</c> still reports the current override rather than writing.</summary>
    [Fact]
    public async Task ModelCommand_NoArgument_ReportsCurrentOverrideWithoutWriting()
    {
        var harness = await Harness.CreateAsync(existingModelOverride: RegisteredModel);

        var result = await harness.ExecuteAsync("/model");

        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain(RegisteredModel);
        (await harness.ReadConversationAsync()).ModelOverride.ShouldBe(RegisteredModel);
    }

    /// <summary>
    /// Clause 3: every clear alias still clears, and - critically - clearing is reachable while
    /// the STORED override is unresolvable. Clearing is the only in-product escape from a
    /// conversation whose override no longer resolves, so it must never be gated on validation.
    /// </summary>
    [Theory]
    [InlineData("clear")]
    [InlineData("off")]
    [InlineData("default")]
    [InlineData("agent")]
    public async Task ModelCommand_ClearAliases_ClearOverrideEvenWhenStoredValueIsUnresolvable(string alias)
    {
        var harness = await Harness.CreateAsync(existingModelOverride: UnregisteredModel);

        var result = await harness.ExecuteAsync("/model", alias);

        result.IsError.ShouldBeFalse(
            $"#3228 clause 3: '/model {alias}' must remain reachable and succeed even when the stored " +
            "override is unresolvable - it is the recovery path for exactly that state.");
        (await harness.ReadConversationAsync()).ModelOverride.ShouldBeNull();
    }

    /// <summary>
    /// Clause 4: <c>ExecuteModelOverrideAsync</c> writes through the narrow three-column
    /// <c>PatchOverrideAsync</c> (#2139) and never through a whole-record <c>SaveAsync</c>, which
    /// carries the handler's stale snapshot of every other column and reverts anything committed
    /// concurrently.
    /// </summary>
    [Fact]
    public async Task ModelCommand_Set_WritesViaPatchOverrideAndNeverSaveAsync()
    {
        var (harness, store) = await Harness.CreateWithRecordingStoreAsync();

        var result = await harness.ExecuteAsync("/model", RegisteredModel);

        result.IsError.ShouldBeFalse();
        store.Verify(
            s => s.PatchOverrideAsync(
                CommandConversationId,
                It.Is<ConversationOverridePatch>(p => p.Model.IsSet && p.Model.Value == RegisteredModel),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "#3228 clause 4: the model override must be written by PatchOverrideAsync.");
        store.Verify(
            s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "#3228 clause 4: a whole-record SaveAsync reverts a concurrently-committed pin or " +
            "metadata mutation, reintroducing the exact clobber #2139 closed for the REST path.");
    }

    /// <summary>Clause 4: clearing the model override also goes through the narrow patch.</summary>
    [Fact]
    public async Task ModelCommand_Clear_WritesViaPatchOverrideAndNeverSaveAsync()
    {
        var (harness, store) = await Harness.CreateWithRecordingStoreAsync(existingModelOverride: RegisteredModel);

        var result = await harness.ExecuteAsync("/model", "clear");

        result.IsError.ShouldBeFalse();
        store.Verify(
            s => s.PatchOverrideAsync(
                CommandConversationId,
                It.Is<ConversationOverridePatch>(p => p.Model.IsSet && p.Model.Value == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(
            s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Clause 4: <c>ExecuteReasoningOverrideAsync</c> shares the same defect and the same fix. Its
    /// argument is parsed, so it was never exposed to the validation gap - but it was left on
    /// <c>SaveAsync</c> by the same incomplete #2139 migration.
    /// </summary>
    [Fact]
    public async Task ReasoningCommand_Set_WritesViaPatchOverrideAndNeverSaveAsync()
    {
        var (harness, store) = await Harness.CreateWithRecordingStoreAsync();

        var result = await harness.ExecuteAsync("/reasoning", "high");

        result.IsError.ShouldBeFalse();
        store.Verify(
            s => s.PatchOverrideAsync(
                CommandConversationId,
                It.Is<ConversationOverridePatch>(p => p.Thinking.IsSet && p.Thinking.Value == "high"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "#3228 clause 4: ExecuteReasoningOverrideAsync must also write through PatchOverrideAsync.");
        store.Verify(
            s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Clause 4: clearing the thinking override also goes through the narrow patch.</summary>
    [Fact]
    public async Task ReasoningCommand_Clear_WritesViaPatchOverrideAndNeverSaveAsync()
    {
        var (harness, store) = await Harness.CreateWithRecordingStoreAsync();

        var result = await harness.ExecuteAsync("/reasoning", "clear");

        result.IsError.ShouldBeFalse();
        store.Verify(
            s => s.PatchOverrideAsync(
                CommandConversationId,
                It.Is<ConversationOverridePatch>(p => p.Thinking.IsSet && p.Thinking.Value == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(
            s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Clause 4, behavioural corroboration of why the primitive matters: the patch touches only
    /// the three override columns, so a title committed by an independent writer is preserved.
    /// The command handler holds a snapshot read BEFORE that write; a whole-record
    /// <c>SaveAsync</c> of that snapshot would revert the title.
    /// </summary>
    [Fact]
    public async Task PatchOverride_PreservesAnIndependentlyCommittedTitle_UnlikeWholeRecordSave()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(NewConversation(CommandConversationId, modelOverride: null));

        // The stale snapshot a command handler would be holding.
        var snapshot = (await store.GetAsync(CommandConversationId))!;

        // An unrelated writer commits a title change after that read.
        var live = (await store.GetAsync(CommandConversationId))!;
        live.Title = Harness.ConcurrentTitle;
        await store.SaveAsync(live);

        await store.PatchOverrideAsync(
            snapshot.ConversationId,
            new ConversationOverridePatch { Model = FieldUpdate<string?>.Set(RegisteredModel) });

        var persisted = (await store.GetAsync(CommandConversationId))!;
        persisted.ModelOverride.ShouldBe(RegisteredModel);
        persisted.Title.ShouldBe(
            Harness.ConcurrentTitle,
            "#3228 clause 4: the narrow patch writes only the override columns, so an independently " +
            "committed title survives - which is precisely what the whole-record SaveAsync the " +
            "command handlers used would have reverted (#2139).");
    }

    /// <summary>
    /// Clause 5: the command path and <c>PUT /api/conversations/{id}/override</c> must accept and
    /// reject the SAME set of ids. Both are exercised against ONE registry and ONE agent registry
    /// here, so a future divergence in either validator reddens this test rather than shipping two
    /// different notions of "valid model".
    /// </summary>
    [Theory]
    [InlineData(RegisteredModel, false)]
    [InlineData("gpt-5", false)]
    [InlineData(UnregisteredModel, true)]
    [InlineData("claude-opus-4", true)] // Registered, but for a DIFFERENT provider than this agent's.
    public async Task CommandPathAndRestPath_AgreeOnAcceptanceOfTheSameModelId(string modelId, bool expectRejected)
    {
        var registry = CreateRegistry();
        var agents = CreateAgentRegistry();

        // Command path.
        var harness = await Harness.CreateAsync(registry: registry, agentRegistry: agents);
        var commandResult = await harness.ExecuteAsync("/model", modelId);

        // REST path, over its own store instance but the SAME registries.
        var restStore = new InMemoryConversationStore();
        await restStore.CreateAsync(NewConversation(RestConversationId, modelOverride: null));
        var controller = new ConversationsController(
            restStore,
            new InMemorySessionStore(),
            modelRegistry: registry,
            agentRegistry: agents);
        var restResult = await controller.SetOverride(
            RestConversationId.Value,
            new SetConversationOverrideRequest(Model: modelId),
            CancellationToken.None);
        var restRejected = restResult is BadRequestObjectResult;

        commandResult.IsError.ShouldBe(
            expectRejected,
            $"Command path disagreed with the expected verdict for '{modelId}'.");
        restRejected.ShouldBe(
            expectRejected,
            $"REST path disagreed with the expected verdict for '{modelId}'.");
        commandResult.IsError.ShouldBe(
            restRejected,
            $"#3228 clause 5: '/model {modelId}' and PUT /override must agree against one registry. " +
            "Two callers of the same rule drifting apart is the root cause this issue records.");
    }

    /// <summary>
    /// Guard against the guard: with no populated registry the command must still WORK. A host
    /// that has registered no models cannot distinguish a typo from a provider it has not loaded,
    /// and refusing every override in that state would be worse than the bug being fixed. This
    /// mirrors <see cref="ModelPreflightKind.RegistryUnavailable"/> deliberately not being a
    /// rejection.
    /// </summary>
    [Fact]
    public async Task ModelCommand_WithNoRegistry_StillSetsOverride()
    {
        var harness = await Harness.CreateAsync(useRegistry: false);

        var result = await harness.ExecuteAsync("/model", UnregisteredModel);

        result.IsError.ShouldBeFalse(
            "An empty/absent registry classifies as 'cannot know', never as a rejection.");
        (await harness.ReadConversationAsync()).ModelOverride.ShouldBe(UnregisteredModel);
    }

    private static ModelRegistry CreateRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register(ProviderKey, MakeModel(RegisteredModel, ProviderKey));
        registry.Register(ProviderKey, MakeModel("gpt-5", ProviderKey));
        registry.Register("anthropic", MakeModel("claude-opus-4", "anthropic"));
        return registry;
    }

    private static LlmModel MakeModel(string id, string provider) => new(
        Id: id,
        Name: id,
        Api: provider + "-messages",
        Provider: provider,
        BaseUrl: "https://example.invalid",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(0m, 0m, 0m, 0m),
        ContextWindow: 200_000,
        MaxTokens: 64_000);

    private static IAgentRegistry CreateAgentRegistry()
    {
        var registry = new Mock<IAgentRegistry>();
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From(AgentIdValue),
            DisplayName = "Test Agent",
            ModelId = RegisteredModel,
            ApiProvider = ProviderKey
        };
        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(descriptor);
        return registry.Object;
    }

    private static Conversation NewConversation(ConversationId id, string? modelOverride) => new()
    {
        ConversationId = id,
        AgentId = AgentId.From(AgentIdValue),
        Title = "Override Command Test",
        Status = ConversationStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ModelOverride = modelOverride
    };

    /// <summary>
    /// Wires a real <see cref="InMemoryConversationStore"/> plus session store behind the internal
    /// contributor, binding the conversation to a session so
    /// <c>ResolveActiveConversationAsync</c> resolves it via <c>ActiveSessionId</c>.
    /// </summary>
    private sealed class Harness
    {
        internal const string ConcurrentTitle = "Renamed concurrently mid-command";

        private object _contributor = null!;
        private IConversationStore _store = null!;
        private InMemoryConversationStore _backing = null!;
        private SessionId _sessionId;

        internal static async Task<Harness> CreateAsync(
            string? existingModelOverride = null,
            ModelRegistry? registry = null,
            IAgentRegistry? agentRegistry = null,
            bool useRegistry = true)
        {
            var harness = new Harness();
            await harness.InitialiseAsync(existingModelOverride, registry, agentRegistry, useRegistry, store: null);
            return harness;
        }

        /// <summary>
        /// Builds a harness whose store is a Moq passthrough over the real in-memory store, so the
        /// clause-4 tests can assert on the write PRIMITIVE while still exercising real
        /// persistence.
        /// </summary>
        internal static async Task<(Harness Harness, Mock<IConversationStore> Store)> CreateWithRecordingStoreAsync(
            string? existingModelOverride = null)
        {
            var harness = new Harness();
            var backing = new InMemoryConversationStore();
            var mock = new Mock<IConversationStore>(MockBehavior.Loose);
            mock.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
                .Returns((ConversationId id, CancellationToken ct) => backing.GetAsync(id, ct));
            mock.Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
                .Returns((AgentId? id, CancellationToken ct) => backing.ListAsync(id, ct));
            mock.Setup(s => s.PatchOverrideAsync(
                    It.IsAny<ConversationId>(),
                    It.IsAny<ConversationOverridePatch>(),
                    It.IsAny<CancellationToken>()))
                .Returns((ConversationId id, ConversationOverridePatch patch, CancellationToken ct) =>
                    backing.PatchOverrideAsync(id, patch, ct));
            mock.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
                .Returns((Conversation c, CancellationToken ct) => backing.SaveAsync(c, ct));

            await harness.InitialiseAsync(
                existingModelOverride,
                registry: null,
                agentRegistry: null,
                useRegistry: true,
                store: mock.Object,
                backing: backing);
            return (harness, mock);
        }

        private async Task InitialiseAsync(
            string? existingModelOverride,
            ModelRegistry? registry,
            IAgentRegistry? agentRegistry,
            bool useRegistry,
            IConversationStore? store,
            InMemoryConversationStore? backing = null)
        {
            _backing = backing ?? new InMemoryConversationStore();
            _store = store ?? _backing;
            _sessionId = SessionId.Create();

            var conversation = NewConversation(CommandConversationId, existingModelOverride);
            conversation.ActiveSessionId = _sessionId;
            await _backing.CreateAsync(conversation);

            var effectiveRegistry = registry ?? (useRegistry ? CreateRegistry() : null);
            var agents = agentRegistry ?? CreateAgentRegistry();

            var services = new ServiceCollection();
            services.AddSingleton(_store);
            if (effectiveRegistry is not null)
                services.AddSingleton(effectiveRegistry);
            var provider = services.BuildServiceProvider();

            var sessions = new InMemorySessionStore();
            await sessions.GetOrCreateAsync(_sessionId, AgentId.From(AgentIdValue));

            var supervisor = new Mock<IAgentSupervisor>();
            supervisor.Setup(s => s.GetAllInstances()).Returns([]);

            _contributor = CreateContributor(agents, supervisor.Object, sessions, provider);
        }

        internal async Task<Conversation> ReadConversationAsync()
            => (await _backing.GetAsync(CommandConversationId))!;

        internal async Task<CommandResult> ExecuteAsync(string commandName, params string[] arguments)
        {
            var method = _contributor.GetType().GetMethod(
                "ExecuteAsync",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(string), typeof(CommandExecutionContext), typeof(CancellationToken)])!;

            var context = new CommandExecutionContext
            {
                RawInput = commandName + (arguments.Length > 0 ? " " + string.Join(' ', arguments) : string.Empty),
                Arguments = arguments,
                AgentId = AgentIdValue,
                SessionId = _sessionId.Value,
                HomeDirectory = Path.GetTempPath()
            };

            var task = (Task<CommandResult>)method.Invoke(
                _contributor,
                [commandName, context, CancellationToken.None])!;
            return await task;
        }

        private static object CreateContributor(
            IAgentRegistry agents,
            IAgentSupervisor supervisor,
            ISessionStore sessions,
            IServiceProvider provider)
        {
            var type = Type.GetType("BotNexus.Gateway.Commands.BuiltInCommandContributor, BotNexus.Gateway")
                ?? throw new InvalidOperationException("BuiltInCommandContributor not found.");

            var constructor = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            var arguments = constructor.GetParameters()
                .Select(parameter => ResolveArgument(parameter, agents, supervisor, sessions, provider))
                .ToArray();

            return constructor.Invoke(arguments);
        }

        private static object? ResolveArgument(
            ParameterInfo parameter,
            IAgentRegistry agents,
            IAgentSupervisor supervisor,
            ISessionStore sessions,
            IServiceProvider provider)
        {
            var type = parameter.ParameterType;
            if (type == typeof(IAgentRegistry)) return agents;
            if (type == typeof(IAgentSupervisor)) return supervisor;
            if (type == typeof(ISessionStore)) return sessions;
            if (type == typeof(IServiceProvider)) return provider;
            if (type == typeof(string)) return Path.GetTempPath();

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                var logger = typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()[0]);
                return logger.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            }

            if (type.IsInterface)
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
                return mock.Object;
            }

            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
