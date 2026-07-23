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
        new ConversationToolProvider(null, null, null, null).ShouldInclude(Context()).ShouldBeFalse();
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
        new AgentManagementToolProvider(null, null, null, [], null, null,
            new BotNexus.Agent.Providers.Core.LlmClient(
                new BotNexus.Agent.Providers.Core.Registry.ApiProviderRegistry(),
                new BotNexus.Agent.Providers.Core.Registry.ModelRegistry()))
            .ShouldInclude(Context())
            .ShouldBeFalse();
    }

}

