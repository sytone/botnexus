using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerToolResultLeaseTests
{
    [Fact]
    public async Task RunAsync_NextProviderTurnGetsDetail_FollowingTurnGetsReceipt()
    {
        const string api = "tool-result-lease-success";
        var providerCalls = new List<IReadOnlyList<Message>>();
        var turn = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            providerCalls.Add(context.Messages);
            return Interlocked.Increment(ref turn) switch
            {
                1 => TestStreamFactory.CreateToolCallResponse(("call-1", "typed_result", new Dictionary<string, object?>())),
                2 => TestStreamFactory.CreateTextResponse("consumed"),
                _ => TestStreamFactory.CreateTextResponse("follow-up"),
            };
        }));

        var followUps = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            FollowUpMessageProvider = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(
                Interlocked.Increment(ref followUps) == 1 ? [new AgentUserMessage("continue")] : []),
        };

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("start")],
            new AgentContext(null, [], [new TypedResultTool()]),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        ToolText(providerCalls[1]).ShouldBe(TypedResultTool.Detail);
        var following = ToolText(providerCalls[2]);
        following.ShouldNotContain(TypedResultTool.Detail);
        following.ShouldContain("result_id");
        following.ShouldContain("do not rerun the source tool");
    }

    [Fact]
    public async Task RunAsync_ProviderRetryAndFilteredTurn_DoNotConsumeDetail()
    {
        const string api = "tool-result-lease-retry";
        var providerCalls = new List<IReadOnlyList<Message>>();
        var turn = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            providerCalls.Add(context.Messages);
            return Interlocked.Increment(ref turn) switch
            {
                1 => TestStreamFactory.CreateToolCallResponse(("call-1", "typed_result", new Dictionary<string, object?>())),
                2 => throw new InvalidOperationException("503 service unavailable"),
                3 => TestStreamFactory.CreateTextResponse("filtered", StopReason.Sensitive),
                _ => TestStreamFactory.CreateTextResponse("follow-up"),
            };
        }));

        var followUps = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            FollowUpMessageProvider = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(
                Interlocked.Increment(ref followUps) == 1 ? [new AgentUserMessage("continue")] : []),
        };

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("start")],
            new AgentContext(null, [], [new TypedResultTool()]),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        ToolText(providerCalls[1]).ShouldBe(TypedResultTool.Detail);
        ToolText(providerCalls[2]).ShouldBe(TypedResultTool.Detail);
        ToolText(providerCalls[3]).ShouldBe(TypedResultTool.Detail);
    }

    private static string ToolText(IReadOnlyList<Message> messages)
    {
        var result = messages.OfType<ToolResultMessage>().ShouldHaveSingleItem();
        return result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text;
    }

    private sealed class TypedResultTool : IAgentTool
    {
        public const string Detail = "the complete retained tool detail";
        private static readonly JsonElement Schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement.Clone();

        public string Name => "typed_result";
        public string Label => "Typed result";
        public Tool Definition => new(Name, "Returns typed retained evidence", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            var store = new ToolResultStore();
            var receipt = store.Store(
                System.Text.Encoding.UTF8.GetBytes(Detail),
                new ToolResultDescriptor(
                    ToolResultKind.Text,
                    "text/plain",
                    null,
                    ToolResultProvenance.LocalTrusted,
                    Name,
                    toolCallId,
                    1,
                    ToolResultCompleteness.Complete,
                    ToolResultRetention.Volatile),
                new ToolResultScope("world", "agent", "conversation", "session", "policy"));
            return Task.FromResult(new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, Detail)],
                receipt));
        }
    }
}
