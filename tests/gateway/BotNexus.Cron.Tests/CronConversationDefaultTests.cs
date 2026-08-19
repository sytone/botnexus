using System.Text.Json;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2412 clause 1: a cron job an agent creates mid-conversation must be bound to THAT conversation
/// up front, using the durable conversation key, instead of being left unbound and having the
/// scheduler pin whatever fresh conversation the first run happened to materialise.
/// </summary>
/// <remarks>
/// <para>
/// The tool-level tests deliberately drive <c>CronTool</c>'s real create path and then re-read the
/// job <b>out of the store</b>. A test that asserted on a hand-built <see cref="CronJob"/> could
/// not detect the defect at all - the defect IS that the create path never populates the field.
/// </para>
/// <para>
/// The sad paths carry the actual regression risk and are asserted just as hard as the happy path:
/// a CLI or REST caller has no conversation context and must still get an unbound (<c>isolated</c>)
/// job, and system-provisioned jobs must be untouched. Binding those would put scheduled output
/// into a conversation whose owner never asked for it.
/// </para>
/// </remarks>
public sealed class CronConversationDefaultTests
{
    private static readonly ConversationId Creating = ConversationId.From("c_creating_durable");

    // ── The decision itself ───────────────────────────────────────────────────────

    /// <summary>Happy path: an agent-prompt job created with conversation context binds to it.</summary>
    [Fact]
    public void Resolve_AgentPromptWithConversationContext_BindsToTheCreatingConversation()
    {
        var resolved = CronConversationDefault.Resolve(
            "agent-prompt", isSystemJob: false, explicitConversationId: null, creatingConversationId: Creating);

        resolved.ShouldNotBeNull();
        resolved!.Value.ShouldBe(Creating);
    }

    /// <summary>
    /// Sad path #1 - the CLI/REST caller. No conversation context means the job stays unbound, so
    /// the scheduler's first-run CAS pins a fresh conversation exactly as it does today.
    /// </summary>
    [Fact]
    public void Resolve_WithNoConversationContext_StaysUnbound()
    {
        CronConversationDefault
            .Resolve("agent-prompt", isSystemJob: false, explicitConversationId: null, creatingConversationId: null)
            .ShouldBeNull();
    }

    /// <summary>
    /// Sad path #2 - the DURABILITY correction. A default-constructed (uninitialised)
    /// <see cref="ConversationId"/> is the Vogen "unset" sentinel, not a durable store key.
    /// Persisting one would create precisely the dangling binding this change exists to prevent,
    /// so it must be rejected rather than coerced into a binding.
    /// </summary>
    [Fact]
    public void Resolve_WithAnUninitialisedConversationId_StaysUnbound()
    {
        var uninitialised = UninitialisedConversationId();
        uninitialised.IsInitialized().ShouldBeFalse();

        CronConversationDefault
            .Resolve("agent-prompt", isSystemJob: false, explicitConversationId: null, creatingConversationId: uninitialised)
            .ShouldBeNull();
    }

    /// <summary>System-provisioned jobs (heartbeat and friends) manage their own conversation.</summary>
    [Fact]
    public void Resolve_ForASystemJob_StaysUnbound_EvenWithConversationContext()
    {
        CronConversationDefault
            .Resolve("agent-prompt", isSystemJob: true, explicitConversationId: null, creatingConversationId: Creating)
            .ShouldBeNull();
    }

    /// <summary>A command job costs no model turn and emits no conversational output.</summary>
    [Fact]
    public void Resolve_ForACommandJob_StaysUnbound_EvenWithConversationContext()
    {
        CronConversationDefault
            .Resolve("command", isSystemJob: false, explicitConversationId: null, creatingConversationId: Creating)
            .ShouldBeNull();
    }

    /// <summary>An explicit caller-chosen binding is a decision, never a gap for a default to fill.</summary>
    [Fact]
    public void Resolve_WithAnExplicitBinding_NeverOverridesIt()
    {
        var chosen = ConversationId.From("c_explicit");

        var resolved = CronConversationDefault.Resolve(
            "agent-prompt", isSystemJob: false, explicitConversationId: chosen, creatingConversationId: Creating);

        resolved!.Value.ShouldBe(chosen);
    }

    /// <summary>
    /// An explicit binding wins even on the paths the default declines, so a system or command job
    /// a caller deliberately pinned is still honoured.
    /// </summary>
    [Fact]
    public void Resolve_WithAnExplicitBinding_WinsEvenForSystemAndCommandJobs()
    {
        var chosen = ConversationId.From("c_explicit");

        CronConversationDefault
            .Resolve("command", isSystemJob: true, explicitConversationId: chosen, creatingConversationId: null)
            !.Value.ShouldBe(chosen);
    }

    /// <summary>Action-type matching follows the tool's own case-insensitive normalisation.</summary>
    [Fact]
    public void Resolve_MatchesTheActionTypeCaseInsensitively()
    {
        CronConversationDefault
            .Resolve("Agent-Prompt", isSystemJob: false, explicitConversationId: null, creatingConversationId: Creating)
            .ShouldNotBeNull();
    }

    /// <summary>An unknown or absent action type is not assumed to be agent-prompt.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("webhook")]
    public void Resolve_ForAnUnknownActionType_StaysUnbound(string? actionType)
    {
        CronConversationDefault
            .Resolve(actionType, isSystemJob: false, explicitConversationId: null, creatingConversationId: Creating)
            .ShouldBeNull();
    }

    // ── Through the real tool create path, read back from the store ───────────────

    /// <summary>
    /// The clause verbatim, end to end: the agent creates a job through the tool while holding a
    /// durable conversation, and the PERSISTED row carries that conversation. Asserting on the
    /// store (not the tool's response projection) is what makes this the real binding.
    /// </summary>
    [Fact]
    public async Task Create_ByAnAgentWithConversationContext_PersistsTheConversationBinding()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store, Creating);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "loop the maintenance sweep",
            ["schedule"] = "*/15 * * * *",
            ["message"] = "check the queue"
        });

        var stored = await context.Store.GetAsync(JobId.From(created.GetProperty("id").GetString()!));
        stored.ShouldNotBeNull();
        stored!.ConversationId.ShouldNotBeNull();
        stored.ConversationId!.Value.ShouldBe(Creating);
    }

    /// <summary>
    /// The CLI/API caller, through the same tool: no conversation context, so the persisted job is
    /// unbound and behaves exactly as it did before #2412.
    /// </summary>
    [Fact]
    public async Task Create_ByACallerWithNoConversationContext_LeavesTheJobUnbound()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store, creatingConversationId: null);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "headless job",
            ["schedule"] = "*/15 * * * *",
            ["message"] = "check the queue"
        });

        var stored = await context.Store.GetAsync(JobId.From(created.GetProperty("id").GetString()!));
        stored.ShouldNotBeNull();
        stored!.ConversationId.ShouldBeNull();
    }

    /// <summary>
    /// A command job created from the very same conversation-bearing tool instance stays unbound.
    /// This is the discriminating case: it shares every input with the happy path except the action
    /// type, so it fails on any implementation that binds unconditionally.
    /// </summary>
    [Fact]
    public async Task Create_OfACommandJob_LeavesTheJobUnbound_EvenWithConversationContext()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store, Creating);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "nightly script",
            ["schedule"] = "0 3 * * *",
            ["actionType"] = "command",
            ["shellCommand"] = "echo hello"
        });

        var stored = await context.Store.GetAsync(JobId.From(created.GetProperty("id").GetString()!));
        stored.ShouldNotBeNull();
        stored!.ConversationId.ShouldBeNull();
    }

    /// <summary>
    /// A definition update must never retarget an established binding. The conversation pin is
    /// CAS-owned (#2133); a routine edit silently moving a running job's output to another
    /// conversation would be a live-site defect, not a convenience.
    /// </summary>
    [Fact]
    public async Task Update_FromADifferentConversation_NeverRetargetsTheBinding()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var created = await InvokeAsync(CreateTool(context.Store, Creating), new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "bound job",
            ["schedule"] = "*/15 * * * *",
            ["message"] = "check the queue"
        });
        var jobId = created.GetProperty("id").GetString()!;

        // A second, differently-bound tool instance edits the job.
        var otherTool = CreateTool(context.Store, ConversationId.From("c_somewhere_else"));
        await InvokeAsync(otherTool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["schedule"] = "*/30 * * * *"
        });

        var stored = await context.Store.GetAsync(JobId.From(jobId));
        stored!.ConversationId!.Value.ShouldBe(Creating);
        stored.Schedule.ShouldBe("*/30 * * * *");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    // Vogen prohibits writing `default(ConversationId)` directly, but a zero-initialised ARRAY
    // slot of that type still yields the uninitialised sentinel - which is exactly how one reaches
    // production (an unbacked Session.ConversationId before the store backfills it). Producing it
    // this way is the honest reproduction, not a workaround for the analyzer.
    private static ConversationId UninitialisedConversationId()
    {
        var slot = new ConversationId[1];
        return slot[0];
    }

    private static CronTool CreateTool(ICronStore store, ConversationId? creatingConversationId)
        => new(
            store,
            CronToolFailureAlertSurfaceTests.CreateScheduler(store, []),
            AgentId.From("agent-a"),
            allowCrossAgentCron: true,
            commandAuthorizer: new ToolPolicyCommandCronAuthorizer(new AllowingToolPolicyProvider()),
            alertTargetResolver: new CronToolFailureAlertSurfaceTests.StubResolver(exists: true),
            creatingConversationId: creatingConversationId);

    // The command-job case is about the conversation binding, not about authorization, so the
    // #2462 authoring gate is satisfied with a policy that allows. A CronTool built without an
    // authorizer fails closed - covered by CommandCronAuthoringAuthorizationTests.
    private sealed class AllowingToolPolicyProvider : IToolPolicyProvider
    {
        public ToolRiskLevel GetRiskLevel(string toolName) => ToolRiskLevel.Safe;
        public bool RequiresApproval(string toolName, string? agentId = null) => false;
        public ToolApprovalFallback GetApprovalFallback(string toolName, string? agentId = null)
            => ToolApprovalFallback.Allow;
        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }

    private static async Task<JsonElement> InvokeAsync(CronTool tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        var result = await tool.ExecuteAsync("call-1", prepared);
        return JsonDocument.Parse(result.Content[0].Value).RootElement.Clone();
    }
}
