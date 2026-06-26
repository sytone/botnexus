using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// End-to-end tests that the post-turn claim auditor (#1600) is wired into the agent
/// loop: a fabricated artifact claim in the final message with no backing tool call
/// produces a <see cref="ClaimAuditEvent"/>, while a backed claim, a disabled auditor,
/// and an unconfigured auditor produce none.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerClaimAuditTests
{
    private static IDisposable RegisterTextProvider(string apiId, string responseText)
        => TestHelpers.RegisterProvider(
            new TestApiProvider(apiId, simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse(responseText)));

    private static AgentLoopConfig ConfigWithAudit(string apiId, ClaimAuditOptions? audit)
        => TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(apiId)) with { ClaimAudit = audit };

    [Fact]
    public async Task RunAsync_FabricatedIssueClaim_NoTools_EmitsClaimAuditEvent()
    {
        const string api = "claim-audit-fabricated";
        using var _ = RegisterTextProvider(api, "Good news, everyone! I filed issue #1234 to track the regression.");
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, ClaimAuditOptions.CreateDefault()),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var auditEvent = events.OfType<ClaimAuditEvent>().ShouldHaveSingleItem();
        auditEvent.Result.HasUnbackedClaims.ShouldBeTrue();
        auditEvent.Result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.IssueFiled);
        auditEvent.Result.ShouldBlock.ShouldBeFalse(); // warn mode by default
    }

    [Fact]
    public async Task RunAsync_ClaimAuditEvent_IsEmittedBeforeAgentEnd()
    {
        const string api = "claim-audit-ordering";
        using var _ = RegisterTextProvider(api, "I filed issue #1234.");
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("go")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, ClaimAuditOptions.CreateDefault()),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var auditIndex = events.FindIndex(e => e is ClaimAuditEvent);
        var endIndex = events.FindIndex(e => e is AgentEndEvent);
        auditIndex.ShouldBeGreaterThanOrEqualTo(0);
        endIndex.ShouldBeGreaterThanOrEqualTo(0);
        auditIndex.ShouldBeLessThan(endIndex);
    }

    [Fact]
    public async Task RunAsync_BenignFinalMessage_EmitsNoClaimAuditEvent()
    {
        const string api = "claim-audit-benign";
        using var _ = RegisterTextProvider(api, "I reviewed the change and it looks correct. No action needed.");
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("review please")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, ClaimAuditOptions.CreateDefault()),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_AuditorNotConfigured_EmitsNoClaimAuditEvent()
    {
        const string api = "claim-audit-unconfigured";
        using var _ = RegisterTextProvider(api, "I filed issue #1234 with no tools at all.");
        var events = new List<AgentEvent>();

        // ClaimAudit left null => auditor does not run.
        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("go")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, audit: null),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_AuditorDisabled_EmitsNoClaimAuditEvent()
    {
        const string api = "claim-audit-disabled";
        using var _ = RegisterTextProvider(api, "I filed issue #1234 with no tools at all.");
        var events = new List<AgentEvent>();

        var disabled = ClaimAuditOptions.CreateDefault() with { Enabled = false };
        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("go")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, disabled),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_BlockMode_ClaimAuditEventReportsShouldBlock()
    {
        const string api = "claim-audit-block";
        using var _ = RegisterTextProvider(api, "I deployed the build to production.");
        var events = new List<AgentEvent>();

        var block = ClaimAuditOptions.CreateDefault() with { Mode = ClaimAuditMode.Block };
        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("ship it")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAudit(api, block),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var auditEvent = events.OfType<ClaimAuditEvent>().ShouldHaveSingleItem();
        auditEvent.Result.ShouldBlock.ShouldBeTrue();
    }
}
