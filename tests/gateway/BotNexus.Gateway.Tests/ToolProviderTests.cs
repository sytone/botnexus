using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation.ToolProviders;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #1382 Finding 1: the isolation tool wiring was extracted from a 23-call Service Locator body into
/// explicit <see cref="IToolProvider"/> units. These tests exercise the providers directly — the whole
/// point of the refactor is that inclusion gating and tool construction are now independently testable
/// without stubbing an entire <c>IServiceProvider</c> or driving <c>CreateAsync</c>.
/// </summary>
public class ToolProviderTests
{
    private static AgentDescriptor Descriptor(IReadOnlyList<string>? toolIds = null)
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            SystemPrompt = "sp",
            ToolIds = toolIds ?? []
        };

    private static ToolProviderContext Context(
        IReadOnlyList<string>? toolIds = null,
        IReadOnlySet<string>? existingToolNames = null,
        bool isSubAgentSession = false,
        Func<IConversationStore, Task<ConversationId?>>? resolveConversationId = null)
    {
        var descriptor = Descriptor(toolIds);
        return new ToolProviderContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.From("session-1") },
            descriptor.ToolIds,
            existingToolNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            isSubAgentSession,
            new NoOpPathValidator(),
            resolveConversationId ?? (_ => Task.FromResult<ConversationId?>(null)),
            NullLogger.Instance,
            "/ws",
            descriptor.ModelId,
            BotNexus.Agent.Providers.Core.Registry.ProviderCapabilities.Default,
            CancellationToken.None);
    }

    private sealed class NoOpPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    [Fact]
    public void ToolAllowed_EmptyAllowlist_AllowsEverything()
    {
        var ctx = Context(toolIds: []);
        ctx.ToolAllowed("cron").ShouldBeTrue();
        ctx.ToolAllowed("anything").ShouldBeTrue();
    }

    [Fact]
    public void ToolAllowed_NonEmptyAllowlist_GatesByName_CaseInsensitive()
    {
        var ctx = Context(toolIds: ["Cron"]);
        ctx.ToolAllowed("cron").ShouldBeTrue();
        ctx.ToolAllowed("todo").ShouldBeFalse();
    }

    [Fact]
    public void CronToolProvider_ExcludedWhenDependenciesMissing()
    {
        var provider = new CronToolProvider(cronStore: null, cronScheduler: null);
        provider.ShouldInclude(Context()).ShouldBeFalse();
    }

    [Fact]
    public void CronToolProvider_ExcludedWhenCronToolAlreadyPresent()
    {
        // Even with the allowlist open, if an extension already contributed a "cron" tool the
        // provider must not double-register — preserving the pre-refactor hasCronTool guard.
        var provider = new CronToolProvider(cronStore: null, cronScheduler: null);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cron" };
        provider.ShouldInclude(Context(existingToolNames: existing)).ShouldBeFalse();
    }

    [Fact]
    public void SessionToolProvider_ExcludedWithoutStore_IncludedWithStore()
    {
        new SessionToolProvider(sessionStore: null).ShouldInclude(Context()).ShouldBeFalse();
    }

    [Fact]
    public void ConversationToolProvider_ExcludedWithoutStore()
    {
        new ConversationToolProvider(null, null, null, null, null).ShouldInclude(Context()).ShouldBeFalse();
    }

    [Fact]
    public void AskUserToolProvider_ExcludedWhenRegistryMissing()
    {
        new AskUserToolProvider(null, null, null).ShouldInclude(Context()).ShouldBeFalse();
    }

    [Fact]
    public void AskUserToolProvider_ExcludedWhenNotInAllowlist()
    {
        var registry = new Mock<IAskUserResponseRegistry>().Object;
        new AskUserToolProvider(registry, null, null)
            .ShouldInclude(Context(toolIds: ["cron"]))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task DelayToolProvider_AlwaysIncludes_AndBuildsDelayTool()
    {
        var provider = new DelayToolProvider(Options.Create(new DelayToolOptions()));
        provider.ShouldInclude(Context()).ShouldBeTrue();
        var tools = await provider.CreateToolsAsync(Context());
        tools.ShouldHaveSingleItem().ShouldBeOfType<DelayTool>();
    }

    [Fact]
    public async Task DateTimeToolProvider_AlwaysIncludes_AndBuildsDateTimeTool()
    {
        var provider = new DateTimeToolProvider(null);
        provider.ShouldInclude(Context()).ShouldBeTrue();
        var tools = await provider.CreateToolsAsync(Context());
        tools.ShouldHaveSingleItem().ShouldBeOfType<DateTimeTool>();
    }

    [Fact]
    public async Task AgentFilesToolProvider_AlwaysIncludes_AndBuildsAgentFilesTool()
    {
        var provider = new AgentFilesToolProvider(null);
        provider.ShouldInclude(Context()).ShouldBeTrue();
        var tools = await provider.CreateToolsAsync(Context());
        tools.ShouldHaveSingleItem().ShouldBeOfType<AgentFilesTool>();
    }

    [Fact]
    public void SubAgentToolProvider_ExcludedForSubAgentSession()
    {
        new SubAgentToolProvider(new Mock<BotNexus.Gateway.Abstractions.Agents.ISubAgentManager>().Object, Options.Create(new GatewayOptions
        {
            SubAgents = new BotNexus.Gateway.Configuration.SubAgentOptions { MaxDepth = 1 }
        }), null, null)
            .ShouldInclude(Context(isSubAgentSession: true))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task SubAgentToolProvider_InspectionHonorsManageToolRestriction()
    {
        var provider = new SubAgentToolProvider(new Mock<BotNexus.Gateway.Abstractions.Agents.ISubAgentManager>().Object,
            Options.Create(new GatewayOptions { SubAgents = new SubAgentOptions { MaxDepth = 1 } }), null, null);
        (await provider.CreateToolsAsync(Context(toolIds: ["read"]))).ShouldBeEmpty();
        var allowed = await provider.CreateToolsAsync(Context(toolIds: ["manage_subagent"]));
        allowed.ShouldHaveSingleItem().Name.ShouldBe("manage_subagent");
        var actions = allowed[0].Definition.Parameters.GetProperty("properties").GetProperty("action").GetProperty("enum");
        actions.EnumerateArray().Select(a => a.GetString()).ShouldContain("result");
        actions.EnumerateArray().Select(a => a.GetString()).ShouldContain("history");
        actions.EnumerateArray().Select(a => a.GetString()).ShouldNotContain("message");
        provider.ShouldInclude(Context(isSubAgentSession: true)).ShouldBeFalse();
    }

    [Fact]
    public void SubAgentToolProvider_ExcludedWhenManagerMissing()
    {
        new SubAgentToolProvider(null, Options.Create(new GatewayOptions
        {
            SubAgents = new BotNexus.Gateway.Configuration.SubAgentOptions { MaxDepth = 1 }
        }), null, null)
            .ShouldInclude(Context())
            .ShouldBeFalse();
    }

    [Fact]
    public void SubAgentToolProvider_ExcludedWhenMaxDepthZero()
    {
        new SubAgentToolProvider(new Mock<BotNexus.Gateway.Abstractions.Agents.ISubAgentManager>().Object, Options.Create(new GatewayOptions
        {
            SubAgents = new BotNexus.Gateway.Configuration.SubAgentOptions { MaxDepth = 0 }
        }), null, null)
            .ShouldInclude(Context())
            .ShouldBeFalse();
    }

    [Fact]
    public void CanvasToolProvider_GatedByAllowlist()
    {
        var provider = new CanvasToolProvider(null, []);
        provider.ShouldInclude(Context(toolIds: [])).ShouldBeTrue();
        provider.ShouldInclude(Context(toolIds: ["todo"])).ShouldBeFalse();
        provider.ShouldInclude(Context(toolIds: ["canvas"])).ShouldBeTrue();
    }

    [Fact]
    public void TodoToolProvider_GatedByAllowlist()
    {
        var provider = new TodoToolProvider(null, []);
        provider.ShouldInclude(Context(toolIds: [])).ShouldBeTrue();
        provider.ShouldInclude(Context(toolIds: ["canvas"])).ShouldBeFalse();
        provider.ShouldInclude(Context(toolIds: ["todo"])).ShouldBeTrue();
    }

    [Fact]
    public void ListAgentsToolProvider_ExcludedWhenRegistryMissing()
    {
        new ListAgentsToolProvider(null, null).ShouldInclude(Context()).ShouldBeFalse();
    }

    [Fact]
    public void AgentManagementToolProvider_ExcludedWhenDependenciesMissing()
    {
        new AgentManagementToolProvider(null, null, null, [], null,
            new BotNexus.Agent.Providers.Core.LlmClient(
                new BotNexus.Agent.Providers.Core.Registry.ApiProviderRegistry(),
                new BotNexus.Agent.Providers.Core.Registry.ModelRegistry()))
            .ShouldInclude(Context())
            .ShouldBeFalse();
    }

    // ── list_locations: standard allowlist semantics ─────────────────────────────────

    /// <summary>
    /// Available by default, like every other tool. An earlier revision made this one opt-in, but a
    /// non-empty toolIds restricts to exactly that list - including the workspace tools - so the
    /// only way to grant it also stripped read, write, edit and shell from the agent. See the
    /// provider's remarks.
    /// </summary>
    [Fact]
    public void ListLocationsToolProvider_NoToolIdsConfigured_IsIncluded()
    {
        var provider = new ListLocationsToolProvider(PlatformConfigMonitor());

        provider.ShouldInclude(Context(toolIds: [])).ShouldBeTrue();
    }

    /// <summary>
    /// The control for an agent that should not see the inventory: name the tools it may use, and
    /// leave this one out.
    /// </summary>
    [Fact]
    public void ListLocationsToolProvider_RestrictedToOtherTools_IsExcluded()
    {
        var provider = new ListLocationsToolProvider(PlatformConfigMonitor());

        provider.ShouldInclude(Context(toolIds: ["read", "ls"])).ShouldBeFalse();
    }

    [Fact]
    public void ListLocationsToolProvider_ExplicitlyNamed_IsIncluded()
    {
        var provider = new ListLocationsToolProvider(PlatformConfigMonitor());

        provider.ShouldInclude(Context(toolIds: ["list_locations"])).ShouldBeTrue();
    }

    [Fact]
    public void ListLocationsToolProvider_NamingIsCaseInsensitive()
    {
        var provider = new ListLocationsToolProvider(PlatformConfigMonitor());

        provider.ShouldInclude(Context(toolIds: ["LIST_LOCATIONS"])).ShouldBeTrue();
    }

    /// <summary>
    /// Without configuration there is nothing to list, and the tool would only be able to report an
    /// empty world - so it is not offered at all.
    /// </summary>
    [Fact]
    public void ListLocationsToolProvider_WithoutConfiguration_IsExcluded()
    {
        var provider = new ListLocationsToolProvider(platformConfig: null);

        provider.ShouldInclude(Context(toolIds: [])).ShouldBeFalse();
        provider.ShouldInclude(Context(toolIds: ["list_locations"])).ShouldBeFalse();
    }

    private static IOptionsMonitor<PlatformConfig> PlatformConfigMonitor()
        => new StaticPlatformConfigMonitor(new PlatformConfig());

    private sealed class StaticPlatformConfigMonitor(PlatformConfig value) : IOptionsMonitor<PlatformConfig>
    {
        public PlatformConfig CurrentValue => value;
        public PlatformConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<PlatformConfig, string?> listener) => null;
    }
}
