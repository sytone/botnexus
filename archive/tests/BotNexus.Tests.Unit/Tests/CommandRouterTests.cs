using BotNexus.Command;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class CommandRouterTests
{
    private static InboundMessage MakeMessage(string content) =>
        new("telegram", "user1", "chat1", content, DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

    [Fact]
    public async Task TryHandleAsync_ExactMatch_ReturnsTrueAndCallsHandler()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        var called = false;

        router.Register("/test", async (msg, ct) =>
        {
            called = true;
            await Task.CompletedTask;
            return "handled";
        });

        var result = await router.TryHandleAsync(MakeMessage("/test"));

        result.Should().BeTrue();
        called.Should().BeTrue();
    }

    [Fact]
    public async Task TryHandleAsync_NonCommand_ReturnsFalse()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);

        var result = await router.TryHandleAsync(MakeMessage("hello world"));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_UnknownCommand_ReturnsFalse()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        router.Register("/help", async (msg, ct) => { await Task.CompletedTask; return "help"; });

        var result = await router.TryHandleAsync(MakeMessage("/unknown"));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_PrefixMatch_ReturnsTrueAndCallsHandler()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        var called = false;

        router.Register("/set*", async (msg, ct) =>
        {
            called = true;
            await Task.CompletedTask;
            return "set handled";
        });

        var result = await router.TryHandleAsync(MakeMessage("/set name value"));

        result.Should().BeTrue();
        called.Should().BeTrue();
    }

    [Fact]
    public async Task TryHandleAsync_HigherPriorityHandlerCalledFirst()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        var callOrder = new List<int>();

        router.Register("/test", async (msg, ct) => { callOrder.Add(1); await Task.CompletedTask; return "low priority"; }, priority: 1);
        router.Register("/test", async (msg, ct) => { callOrder.Add(2); await Task.CompletedTask; return "high priority"; }, priority: 10);

        await router.TryHandleAsync(MakeMessage("/test"));

        callOrder.First().Should().Be(2);
    }

    [Fact]
    public async Task TryHandleAsync_CaseInsensitive_MatchesCommand()
    {
        var router = new CommandRouter(NullLogger<CommandRouter>.Instance);
        var called = false;

        router.Register("/help", async (msg, ct) => { called = true; await Task.CompletedTask; return null; });

        var result = await router.TryHandleAsync(MakeMessage("/HELP"));

        result.Should().BeTrue();
        called.Should().BeTrue();
    }

    [Fact]
    public async Task TryHandleAsync_WithChannel_SendsResponse()
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(c => c.Name).Returns("telegram");
        mockChannel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var router = new CommandRouter(NullLogger<CommandRouter>.Instance, mockChannel.Object);
        router.Register("/help", async (msg, ct) => { await Task.CompletedTask; return "help text"; });

        await router.TryHandleAsync(MakeMessage("/help"));

        mockChannel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "help text"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
