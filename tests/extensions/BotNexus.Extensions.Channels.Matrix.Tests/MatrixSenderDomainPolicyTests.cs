using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>Sender-domain federation policy at the Matrix inbound admission seam.</summary>
public sealed class MatrixSenderDomainPolicyTests
{
    private const string Room = "!room1:example.com";

    [Theory]
    [InlineData("@jon:TRUSTED.EXAMPLE", true)]
    [InlineData("@jon:trusted.example", true)]
    [InlineData("@jon:trusted.example.evil", false)]
    [InlineData("@jon:nottrusted.example", false)]
    [InlineData("@jon", false)]
    [InlineData("jon:trusted.example", false)]
    [InlineData("@:trusted.example", false)]
    [InlineData("@jon:", false)]
    public void IsUserAllowed_AllowedDomainsRequireAnExactValidSenderDomain(string sender, bool expected)
    {
        var identity = Identity(config => config.AllowedSenderDomains.Add(" Trusted.Example "));

        identity.IsUserAllowed(sender).ShouldBe(expected);
    }

    [Fact]
    public void IsUserAllowed_DeniedDomainOverridesAllowedDomain()
    {
        var identity = Identity(config =>
        {
            config.AllowedSenderDomains.Add("example.com");
            config.DeniedSenderDomains.Add("EXAMPLE.COM");
        });

        identity.IsUserAllowed("@jon:example.com").ShouldBeFalse();
    }

    [Fact]
    public void IsUserAllowed_DomainPolicyDoesNotBypassTheUserAllowList()
    {
        var identity = Identity(config =>
        {
            config.AllowedSenderDomains.Add("example.com");
            config.AllowedUserIds.Add("@someone-else:example.com");
        });

        identity.IsUserAllowed("@jon:example.com").ShouldBeFalse();
    }

    [Theory]
    [InlineData("@jon:any.example")]
    [InlineData("not-a-matrix-user-id")]
    public void IsUserAllowed_AbsentDomainPolicyPreservesExistingUserPolicy(string sender)
    {
        Identity().IsUserAllowed(sender).ShouldBeTrue();
    }

    [Fact]
    public async Task Inbound_DeniedSenderDomain_ProducesZeroDispatches()
    {
        var options = BuildOptions(config => config.DeniedSenderDomains.Add("blocked.example"));
        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(options),
            new FakeMatrixClientFactory());
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(candidate => candidate.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await adapter.StartAsync(dispatcher.Object);
        var runtime = adapter.GetAccount("farnsworth");
        runtime.ShouldNotBeNull();

        await adapter.ProcessSyncResponseAsync(
            runtime,
            SyncWithMessage("@mallory:blocked.example"),
            CancellationToken.None);
        await adapter.StopAsync();

        dispatcher.Verify(
            candidate => candidate.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static MatrixAccountIdentity Identity(Action<MatrixAccountConfig>? configure = null)
    {
        var config = new MatrixAccountConfig();
        configure?.Invoke(config);
        return MatrixAccountIdentity.FromConfig(config);
    }

    private static MatrixChannelOptions BuildOptions(Action<MatrixAccountConfig> configure)
    {
        var account = new MatrixAccountConfig
        {
            UserId = "@farnsworth:example.com",
            AccessToken = "syt_fake_token",
            AgentId = "farnsworth",
        };
        configure(account);

        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["farnsworth"] = account;
        return options;
    }

    private static MatrixSyncResponse SyncWithMessage(string sender) =>
        new()
        {
            NextBatch = "batch-1",
            Rooms = new MatrixSyncRooms
            {
                Join = new Dictionary<string, MatrixJoinedRoom>
                {
                    [Room] = new()
                    {
                        Timeline = new MatrixTimeline
                        {
                            Events =
                            [
                                new MatrixEvent
                                {
                                    Type = "m.room.message",
                                    Sender = sender,
                                    EventId = "$evt1",
                                    Content = new MatrixMessageContent
                                    {
                                        MsgType = "m.text",
                                        Body = "blocked",
                                    },
                                },
                            ],
                        },
                    },
                },
            },
        };
}
