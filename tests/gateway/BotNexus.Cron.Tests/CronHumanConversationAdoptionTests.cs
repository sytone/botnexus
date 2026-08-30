using System.Text.Json;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3521: a cron job must neither ADOPT a human's conversation as its output target nor archive a
/// conversation it merely adopted when the job is deleted.
/// </summary>
/// <remarks>
/// <para>
/// The two halves interlock, which is why they are covered in one place. The #2412 default bound a
/// job to whatever conversation its creating agent happened to be speaking in; when that was a
/// human's <c>is_default</c> / <see cref="ConversationKind.HumanAgent"/> thread, the portal
/// relocated the thread into the Cron bucket for the life of the job AND
/// <c>DeleteJobAsync</c> made it an archive target. In production that pointed a one-shot job at a
/// 6,324-message conversation dating from April; the archive failed only because it deadlocked
/// against a wedged run (#3517), not because anything guarded it.
/// </para>
/// <para>
/// Every assertion here is on an <b>observable</b> - the persisted <c>ConversationId</c> on the job
/// row, or the archive calls the conversation store actually received - never on a log line. A test
/// that asserted on the log would pass on a build that logged and archived anyway.
/// </para>
/// </remarks>
public sealed class CronHumanConversationAdoptionTests
{
    private const string HumanConversation = "c_cd708c7907f84a0fba8d19895073d8a8";
    private static readonly AgentId Agent = AgentId.From("agent-a");

    // ── AC1: a human-facing conversation is never adopted ─────────────────────────

    /// <summary>
    /// AC1 verbatim, at the decision seam: the creating conversation is a human thread, so the job
    /// is left UNBOUND and the scheduler's first-run CAS mints a cron conversation instead.
    /// </summary>
    [Fact]
    public void Resolve_WhenTheCreatingConversationIsHumanAgent_StaysUnbound()
    {
        CronConversationDefault.Resolve(
            "agent-prompt",
            isSystemJob: false,
            explicitConversationId: null,
            creatingConversationId: ConversationId.From(HumanConversation),
            creatingConversationKind: ConversationKind.HumanAgent)
            .ShouldBeNull();
    }

    /// <summary>
    /// The <c>IsDefault</c> half of AC1 is an independent disqualifier, not a synonym for the kind.
    /// The agent's default home thread must not be adopted even when its pairing says otherwise.
    /// </summary>
    [Fact]
    public void Resolve_WhenTheCreatingConversationIsTheAgentDefault_StaysUnbound()
    {
        CronConversationDefault.Resolve(
            "agent-prompt",
            isSystemJob: false,
            explicitConversationId: null,
            creatingConversationId: ConversationId.From(HumanConversation),
            creatingConversationKind: ConversationKind.AgentAgent,
            creatingConversationIsDefault: true)
            .ShouldBeNull();
    }

    // ── AC6: the #2412 binding still works for agent- and cron-owned conversations ─

    /// <summary>
    /// Non-vacuity for AC1 and the whole of AC6: a change that simply stopped binding would pass
    /// every test above and silently revert #2412. An agent-owned pairing still binds.
    /// </summary>
    [Theory]
    [InlineData(ConversationKind.AgentAgent)]
    [InlineData(ConversationKind.AgentSubAgent)]
    [InlineData(ConversationKind.Ralph)]
    public void Resolve_WhenTheCreatingConversationIsAgentOwned_StillBinds(ConversationKind kind)
    {
        var creating = ConversationId.From("conv:agent-owned");

        CronConversationDefault.Resolve(
            "agent-prompt",
            isSystemJob: false,
            explicitConversationId: null,
            creatingConversationId: creating,
            creatingConversationKind: kind)
            !.Value.ShouldBe(creating);
    }

    /// <summary>
    /// Unknown provenance preserves the pre-#3521 behaviour verbatim. Treating "could not read the
    /// row" as "human" would disable the #2412 binding for every caller that does not thread
    /// provenance through - an AC6 regression wearing a safety costume.
    /// </summary>
    [Fact]
    public void Resolve_WhenProvenanceIsUnknown_KeepsTheExistingBinding()
    {
        var creating = ConversationId.From("conv:unknown-provenance");

        CronConversationDefault.Resolve(
            "agent-prompt",
            isSystemJob: false,
            explicitConversationId: null,
            creatingConversationId: creating,
            creatingConversationKind: null)
            !.Value.ShouldBe(creating);
    }

    // ── AC2: an explicit binding still wins, even for a human conversation ────────

    /// <summary>
    /// AC2 verbatim. The guard narrows the DEFAULT; it must not override a caller's decision. A
    /// caller naming a human conversation has chosen it, which is categorically different from a
    /// default helping itself to whichever thread the agent happened to be standing in.
    /// </summary>
    [Fact]
    public void Resolve_WithAnExplicitHumanConversation_StillBinds()
    {
        var chosen = ConversationId.From(HumanConversation);

        CronConversationDefault.Resolve(
            "agent-prompt",
            isSystemJob: false,
            explicitConversationId: chosen,
            creatingConversationId: chosen,
            creatingConversationKind: ConversationKind.HumanAgent,
            creatingConversationIsDefault: true)
            !.Value.ShouldBe(chosen);
    }

    // ── AC1 through the real tool create path, read back from the store ──────────

    /// <summary>
    /// The clause end to end: an agent creates a job through the tool while speaking in a human's
    /// default thread, and the PERSISTED row is unbound. Asserting on the store rather than on the
    /// tool's response projection is what makes this the real binding.
    /// </summary>
    [Fact]
    public async Task Create_FromAHumanDefaultConversation_PersistsNoBinding()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(
            context.Store,
            ConversationId.From(HumanConversation),
            ConversationKind.HumanAgent,
            isDefault: true);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "watch the PR",
            ["schedule"] = "*/15 * * * *",
            ["message"] = "check it"
        });

        var stored = await context.Store.GetAsync(JobId.From(created.GetProperty("id").GetString()!));
        stored.ShouldNotBeNull();
        stored!.ConversationId.ShouldBeNull(
            "a human's own thread must never be adopted - the first-run CAS mints a cron conversation instead");
    }

    /// <summary>
    /// AC6 through the same path: an agent- or cron-owned conversation still binds, so the tool's
    /// #2412 behaviour is intact for the conversations it was actually written for.
    /// </summary>
    [Fact]
    public async Task Create_FromACronOwnedConversation_StillPersistsTheBinding()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var creating = ConversationId.From("cronconv:agent-a:job-0");
        var tool = CreateTool(context.Store, creating, ConversationKind.AgentAgent, isDefault: false);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "follow-up sweep",
            ["schedule"] = "*/15 * * * *",
            ["message"] = "check it"
        });

        var stored = await context.Store.GetAsync(JobId.From(created.GetProperty("id").GetString()!));
        stored!.ConversationId!.Value.ShouldBe(creating);
    }

    // ── AC3 + AC4: delete archives only what the job owns ─────────────────────────

    /// <summary>
    /// AC3's negative half, and the harm the issue reports. The job's binding points at a human's
    /// conversation it merely adopted, so the delete must NOT archive it - and must still delete
    /// the job.
    /// </summary>
    [Fact]
    public async Task DeleteJob_DoesNotArchiveAConversationTheJobMerelyAdopted()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = new RecordingConversationStore(
            ConversationFactory.CreateDefaultForAgent(ConversationId.From(HumanConversation), Agent));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From(HumanConversation)
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, conversations);
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.Archived.ShouldBeEmpty(
            "the job adopted this conversation; archiving it would remove a human's thread from their active list");
        // Non-vacuity: the ownership check was actually EVALUATED against this conversation. Without
        // this, a build that skipped the archive for some unrelated reason would pass identically.
        conversations.Reads.ShouldBe([HumanConversation]);
        (await context.Store.GetAsync(JobId.From("job-1")))
            .ShouldBeNull("skipping the archive must not block the delete - clause 4");
    }

    /// <summary>
    /// AC3's positive half. Non-vacuity for the test above: a change that simply stopped archiving
    /// would pass it and strand every conversation cron actually owns.
    /// </summary>
    [Fact]
    public async Task DeleteJob_ArchivesAConversationTheJobItselfMinted()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var owned = ConversationId.From("conv:owned-by-job-1");
        var conversations = new RecordingConversationStore(
            ConversationFactory.CreateForCron(owned, Agent, sourceId: "job-1"));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = owned
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, conversations);
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.Archived.ShouldBe([owned.Value]);
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    /// <summary>
    /// The <c>SourceId</c> half of the ownership pair is load-bearing on its own: a conversation
    /// minted by a DIFFERENT cron job is still not this job's to archive. A check that only tested
    /// <c>Source == Cron</c> would let deleting job A destroy job B's thread.
    /// </summary>
    [Fact]
    public async Task DeleteJob_DoesNotArchiveACronConversationMintedByADifferentJob()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var otherJobs = ConversationId.From("conv:owned-by-job-2");
        var conversations = new RecordingConversationStore(
            ConversationFactory.CreateForCron(otherJobs, Agent, sourceId: "job-2"));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = otherJobs
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, conversations);
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.Archived.ShouldBeEmpty();
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    /// <summary>
    /// AC4 explicitly. The skip is the path a one-shot job takes on every subsequent run, so a
    /// throw here would re-enter the unbounded <c>MaybeDeleteOneShotJobAsync</c> retry loop of
    /// #3517 - the very loop the adoption fed. Asserted through the one-shot path itself, not by
    /// calling the delete directly, because that is where the loop lives.
    /// </summary>
    [Fact]
    public async Task AOneShotJobBoundToAnAdoptedConversation_DeletesItselfWithoutRetrying()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = new RecordingConversationStore(
            ConversationFactory.CreateDefaultForAgent(ConversationId.From(HumanConversation), Agent));
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            DeleteJobAfterRun = true,
            ConversationId = ConversationId.From(HumanConversation)
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, conversations);
        await scheduler.RunNowAsync(JobId.From("job-1"));

        (await context.Store.GetAsync(JobId.From("job-1")))
            .ShouldBeNull("the one-shot delete must complete on the first run, not fail and re-arm");
        conversations.Archived.ShouldBeEmpty();
    }

    /// <summary>
    /// Fail-open on an unreadable ownership check. A conversation store that throws must not block
    /// the delete, for the same #3517 reason: an unevaluable guard is not grounds to retry forever.
    /// </summary>
    [Fact]
    public async Task DeleteJob_StillDeletesTheJob_WhenTheOwnershipReadThrows()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var conversations = new RecordingConversationStore(conversation: null)
        {
            ThrowOnGet = new InvalidOperationException("conversation store is down")
        };
        var job = CronStoreTestContext.CreateJob("job-1", actionType: "test-action") with
        {
            ConversationId = ConversationId.From(HumanConversation)
        };
        await context.Store.CreateAsync(job);

        var scheduler = CreateScheduler(context.Store, conversations);
        await scheduler.DeleteJobAsync(JobId.From("job-1"));

        conversations.Reads.ShouldBe([HumanConversation], "non-vacuity: the check ran and the store refused");
        conversations.Archived.ShouldBeEmpty();
        (await context.Store.GetAsync(JobId.From("job-1"))).ShouldBeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static CronScheduler CreateScheduler(ICronStore store, IConversationStore conversations)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ISessionStore>());
        services.AddSingleton(conversations);
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            [new NoOpAction("test-action")],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(
                new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    private static CronTool CreateTool(
        ICronStore store,
        ConversationId creating,
        ConversationKind kind,
        bool isDefault)
        => new(
            store,
            CronToolFailureAlertSurfaceTests.CreateScheduler(store, []),
            Agent,
            allowCrossAgentCron: true,
            commandAuthorizer: new ToolPolicyCommandCronAuthorizer(new AllowingToolPolicyProvider()),
            alertTargetResolver: new CronToolFailureAlertSurfaceTests.StubResolver(exists: true),
            creatingConversationId: creating,
            creatingConversationKind: kind,
            creatingConversationIsDefault: isDefault);

    private static async Task<JsonElement> InvokeAsync(CronTool tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        var result = await tool.ExecuteAsync("call-1", prepared);
        return JsonDocument.Parse(result.Content[0].Value).RootElement.Clone();
    }

    private sealed class AllowingToolPolicyProvider : IToolPolicyProvider
    {
        public ToolRiskLevel GetRiskLevel(string toolName) => ToolRiskLevel.Safe;
        public bool RequiresApproval(string toolName, string? agentId = null) => false;
        public ToolApprovalFallback GetApprovalFallback(string toolName, string? agentId = null)
            => ToolApprovalFallback.Allow;
        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }

    private sealed class NoOpAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// A conversation store that serves ONE conversation and records both the ownership reads and
    /// the archives. Built on Moq because <see cref="IConversationStore"/> carries ~25 members the
    /// delete path never touches.
    /// </summary>
    private sealed class RecordingConversationStore : IConversationStore
    {
        private readonly Conversation? _conversation;
        private readonly IConversationStore _unused = Mock.Of<IConversationStore>();

        public RecordingConversationStore(Conversation? conversation) => _conversation = conversation;

        public List<string> Reads { get; } = [];

        public List<string> Archived { get; } = [];

        public Exception? ThrowOnGet { get; init; }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
        {
            Reads.Add(conversationId.Value);
            if (ThrowOnGet is not null)
                throw ThrowOnGet;

            return Task.FromResult(
                _conversation is not null && _conversation.ConversationId == conversationId ? _conversation : null);
        }

        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        {
            Archived.Add(conversationId.Value);
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(ConversationId conversationId, string source, string? correlationId, string actor, CancellationToken ct = default)
            => ArchiveAsync(conversationId, ct);

        // Everything below is untouched by the delete path and delegates to an empty double.
        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => _unused.ListAsync(agentId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
            => _unused.ListForCitizenAsync(citizen, ct);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
            => _unused.AddParticipantsAsync(conversationId, participants, ct);
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
            => _unused.CreateAsync(conversation, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
            => _unused.SaveAsync(conversation, ct);
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
            => _unused.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
            => _unused.TouchAsync(conversationId, ct);
        public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
            => _unused.PinAsync(conversationId, pin, ct);
        public Task<bool> AddBindingAsync(ConversationId conversationId, ChannelBinding binding, CancellationToken ct = default)
            => _unused.AddBindingAsync(conversationId, binding, ct);
        public Task<bool> RemoveBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
            => _unused.RemoveBindingAsync(conversationId, bindingId, ct);
        public Task<bool> MoveBindingAsync(ConversationId fromConversationId, ConversationId toConversationId, BindingId bindingId, CancellationToken ct = default)
            => _unused.MoveBindingAsync(fromConversationId, toConversationId, bindingId, ct);
        public Task<Conversation?> PatchMetadataAsync(ConversationId conversationId, ConversationMetadataPatch patch, CancellationToken ct = default)
            => _unused.PatchMetadataAsync(conversationId, patch, ct);
        public Task<Conversation?> PatchOverrideAsync(ConversationId conversationId, ConversationOverridePatch patch, CancellationToken ct = default)
            => _unused.PatchOverrideAsync(conversationId, patch, ct);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
            => _unused.GetSummariesAsync(ct);
        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
            => _unused.GetCanvasStateAsync(conversationId, ct);
        public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
            => _unused.SetCanvasStateKeyAsync(conversationId, key, value, ct);
        public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
            => _unused.DeleteCanvasStateKeyAsync(conversationId, key, ct);
        public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
            => _unused.ClearCanvasStateAsync(conversationId, ct);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
