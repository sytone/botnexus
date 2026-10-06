using BotNexus.TeamsProxy.Models;
using BotNexus.TeamsProxy.Services;

namespace BotNexus.Integration.ServiceBusStreaming.Tests;

public sealed class TeamsProxyStreamingContractTests
{
    [Fact]
    public void PseudoStreaming_DeltasAndWhitespaceProduceExactlyOneCompletedTeamsActivity()
    {
        ServiceBusOutboundEnvelope[] envelopes =
        [
            Delta("Yeah", 0),
            Delta(",", 1),
            Delta(" that", 2),
            Delta("   ", 3),
            new()
            {
                Type = "done",
                IsFinal = true,
                Sequence = 4,
                Content = "Yeah, that is the completed response.",
            },
        ];

        var activities = envelopes
            .Where(TeamsOutboundEnvelopePolicy.ShouldPostActivity)
            .ToList();

        var activity = activities.ShouldHaveSingleItem();
        activity.Type.ShouldBe("done");
        activity.IsFinal.ShouldBeTrue();
        activity.Content.ShouldBe("Yeah, that is the completed response.");
    }

    [Theory]
    [InlineData("delta", false, "text")]
    [InlineData("done", false, "text")]
    [InlineData("delta", true, "text")]
    [InlineData("done", true, "   ")]
    public void NonTerminalDeltaOrBlankEnvelope_NeverPostsStandaloneTeamsActivity(
        string type,
        bool isFinal,
        string content)
    {
        var envelope = new ServiceBusOutboundEnvelope
        {
            Type = type,
            IsFinal = isFinal,
            Content = content,
        };

        TeamsOutboundEnvelopePolicy.ShouldPostActivity(envelope).ShouldBeFalse();
    }

    private static ServiceBusOutboundEnvelope Delta(string content, long sequence) => new()
    {
        Type = "delta",
        IsFinal = false,
        Sequence = sequence,
        Content = content,
    };
}
