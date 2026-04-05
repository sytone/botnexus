using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

using AgentUserMessage = BotNexus.AgentCore.Types.UserMessage;

public class AgentLoopRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenRetryOccurs_CallsTransformMoreThanOnce()
    {
        using var provider = RegisterProviderWithOneOverflowThenSuccess();
        var transformCount = 0;
        var config = CreateConfig((messages, _) =>
        {
            Interlocked.Increment(ref transformCount);
            return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
        });

        var context = new AgentContext(null, [], []);

        _ = await AgentLoopRunner.RunAsync([new AgentUserMessage("retry me")], context, config, _ => Task.CompletedTask, CancellationToken.None);

        transformCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task RunAsync_WhenTransformIsIdempotent_ProducesSameResultAcrossRetries()
    {
        using var provider = RegisterProviderWithOneOverflowThenSuccess();
        var transformedSnapshots = new List<string>();
        var config = CreateConfig((messages, _) =>
        {
            var normalized = messages.Select(message => message switch
            {
                AgentUserMessage user => $"user:{user.Content}",
                AssistantAgentMessage assistant => $"assistant:{assistant.Content}",
                ToolResultAgentMessage toolResult => $"tool:{toolResult.ToolCallId}",
                _ => message.GetType().Name
            });
            transformedSnapshots.Add(string.Join("|", normalized));
            return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
        });

        var context = new AgentContext(null, [], []);

        _ = await AgentLoopRunner.RunAsync([new AgentUserMessage("retry me")], context, config, _ => Task.CompletedTask, CancellationToken.None);

        transformedSnapshots.Should().HaveCountGreaterThan(1);
        transformedSnapshots.Distinct(StringComparer.Ordinal).Should().ContainSingle();
    }

    private static AgentLoopConfig CreateConfig(TransformContextDelegate transformContext)
    {
        return new AgentLoopConfig(
            Model: TestHelpers.CreateTestModel("test-api-retry"),
            LlmClient: TestHelpers.CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>(ToProviderMessages(messages)),
            TransformContext: transformContext,
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions());
    }

    private static IReadOnlyList<Message> ToProviderMessages(IReadOnlyList<AgentMessage> messages)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return messages
            .OfType<AgentUserMessage>()
            .Select(message => (Message)new BotNexus.Providers.Core.Models.UserMessage(new UserMessageContent(message.Content), timestamp))
            .ToList();
    }

    private static IDisposable RegisterProviderWithOneOverflowThenSuccess()
    {
        var streamAttempt = 0;
        var provider = new TestApiProvider(
            "test-api-retry",
            simpleStreamFactory: (_, _, _) =>
            {
                if (Interlocked.Increment(ref streamAttempt) == 1)
                {
                    throw new InvalidOperationException("context length exceeded");
                }

                return TestStreamFactory.CreateTextResponse("assistant");
            });

        return TestHelpers.RegisterProvider(provider);
    }
}
