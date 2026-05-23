using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class ChannelIdentityTests
{
    [Fact]
    public void Construction_SetsBothComponents()
    {
        var identity = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001"));

        identity.Channel.Value.ShouldBe("telegram");
        identity.SenderAddress.Value.ShouldBe("555-001");
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001"));
        var b = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001"));

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
    }

    [Fact]
    public void DifferentChannels_AreNotEqual()
    {
        var telegram = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001"));
        var signalr = new ChannelIdentity(ChannelKey.From("signalr"), ChannelAddress.From("555-001"));

        telegram.ShouldNotBe(signalr);
    }

    [Fact]
    public void DifferentSenderAddresses_AreNotEqual()
    {
        var alice = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-001"));
        var bob = new ChannelIdentity(ChannelKey.From("telegram"), ChannelAddress.From("555-002"));

        alice.ShouldNotBe(bob);
    }
}
