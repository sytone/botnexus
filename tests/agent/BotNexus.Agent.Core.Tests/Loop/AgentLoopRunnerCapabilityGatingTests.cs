using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Issue #2432: the leaked-tool-call recovery workaround (#1709) must fire from a capability the
/// serving provider DECLARES, not speculatively against every provider in the platform.
/// <para>
/// The pair of tests at the top of this class is the whole acceptance criterion, and they are
/// deliberately written as a matched pair over the SAME leaked payload: a declaring provider
/// recovers and dispatches (behaviour parity for the transport that actually needed the fix), and a
/// non-declaring provider leaves the assistant turn exactly as the model wrote it. Either test
/// alone is vacuous -- the first passes on the pre-#2432 build because recovery ran for everyone,
/// and the second would pass on a build that simply deleted the recovery.
/// </para>
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerCapabilityGatingTests
{
    /// <summary>The leaked shape from the #1709 capture: invoke markup in the TEXT channel, finish reason Stop.</summary>
    private const string LeakedInvoke =
        "Listing now.\n<invoke name=\"shell\"><parameter name=\"command\">gh issue list</parameter></invoke>";

    /// <summary>A recording tool that counts dispatches so a test can prove recovery did or did not fire.</summary>
    private sealed class RecordingTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{ "type": "object", "properties": { "command": { "type": "string" } } }""").RootElement.Clone();
        private int _executeCount;
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public string Name => "shell";
        public string Label => "Shell";
        public Tool Definition => new("shell", "Run a shell command", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executeCount);
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }

    private static IDisposable RegisterScriptedProvider(
        string apiId,
        ProviderCapabilities capabilities,
        params LlmStream[] responses)
    {
        var index = -1;
        return TestHelpers.RegisterProvider(
            new TestApiProvider(
                apiId,
                simpleStreamFactory: (_, _, _) =>
                {
                    var next = Interlocked.Increment(ref index);
                    return responses[Math.Min(next, responses.Length - 1)];
                },
                capabilities: capabilities));
    }

    private static Task<IReadOnlyList<AgentMessage>> RunAsync(string api, RecordingTool tool)
        => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("list issues")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

    /// <summary>
    /// HAPPY PATH / BEHAVIOUR PARITY: a provider that DECLARES the capability still recovers the
    /// leaked call, dispatches the tool and strips the markup -- byte-identical to pre-#2432
    /// behaviour for the Copilot transport the workaround was written for.
    /// </summary>
    [Fact]
    public async Task DeclaringProvider_StillRecoversAndDispatchesLeakedToolCall()
    {
        const string api = "capability-gating-declared";
        var tool = new RecordingTool();
        using var _ = RegisterScriptedProvider(
            api,
            new ProviderCapabilities(RecoversLeakedToolCallMarkup: true),
            TestStreamFactory.CreateTextResponse(LeakedInvoke, StopReason.Stop),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await RunAsync(api, tool);

        tool.ExecuteCount.ShouldBe(1);
        result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
        var assistant = result.OfType<AssistantAgentMessage>().First();
        assistant.Content.ShouldNotContain("<invoke");
        assistant.FinishReason.ShouldBe(StopReason.ToolUse);
    }

    /// <summary>
    /// SAD PATH: a provider that does NOT declare the capability gets the identical leaked payload
    /// left completely alone -- no tool dispatch, markup preserved verbatim in the assistant text,
    /// finish reason unchanged. This is the assertion that would have failed before #2432, when
    /// recovery ran for every provider regardless of whether it had ever leaked anything.
    /// </summary>
    [Fact]
    public async Task NonDeclaringProvider_LeavesLeakedMarkupUntouchedAndDispatchesNothing()
    {
        const string api = "capability-gating-undeclared";
        var tool = new RecordingTool();
        using var _ = RegisterScriptedProvider(
            api,
            new ProviderCapabilities(RecoversLeakedToolCallMarkup: false),
            TestStreamFactory.CreateTextResponse(LeakedInvoke, StopReason.Stop));

        var result = await RunAsync(api, tool);

        tool.ExecuteCount.ShouldBe(0);
        result.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
        var assistant = result.OfType<AssistantAgentMessage>().Single();
        assistant.Content.ShouldBe(LeakedInvoke);
        assistant.FinishReason.ShouldBe(StopReason.Stop);
    }

    /// <summary>
    /// A provider that declares NOTHING -- an out-of-tree extension or a bare test double -- gets
    /// <see cref="ProviderCapabilities.Default"/>, which has every quirk workaround OFF. This pins
    /// the direction of the default: a new provider does not silently inherit another provider's
    /// compensations.
    /// </summary>
    [Fact]
    public async Task ProviderDeclaringNothing_DefaultsToNoRecovery()
    {
        const string api = "capability-gating-default";
        var tool = new RecordingTool();
        var index = -1;
        LlmStream[] responses = [TestStreamFactory.CreateTextResponse(LeakedInvoke, StopReason.Stop)];
        using var _ = TestHelpers.RegisterProvider(
            new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
                responses[Math.Min(Interlocked.Increment(ref index), responses.Length - 1)]));

        var result = await RunAsync(api, tool);

        tool.ExecuteCount.ShouldBe(0);
        result.OfType<AssistantAgentMessage>().Single().Content.ShouldBe(LeakedInvoke);
    }

    /// <summary>
    /// Gating changes nothing for a GENUINE tool turn: a provider that declares no recovery still
    /// dispatches a real tool call that arrived through the structured channel with a ToolUse finish
    /// reason. The recovery gate sits on the non-ToolUse branch only, and this proves it did not
    /// swallow the ordinary path.
    /// </summary>
    [Fact]
    public async Task NonDeclaringProvider_StillDispatchesGenuineStructuredToolCall()
    {
        const string api = "capability-gating-genuine";
        var tool = new RecordingTool();
        using var _ = RegisterScriptedProvider(
            api,
            new ProviderCapabilities(RecoversLeakedToolCallMarkup: false),
            TestStreamFactory.CreateToolCallResponse(("call-1", "shell", new Dictionary<string, object?>())),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await RunAsync(api, tool);

        tool.ExecuteCount.ShouldBe(1);
        result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
    }
}
