using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

public sealed class DiagnosticsControllerTests
{
    [Fact]
    public void ReportChannelError_WithValidReport_ReturnsOk()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        var report = new ChannelErrorReport
        {
            Message = "Test error",
            StackTrace = "at SomeClass.SomeMethod()",
            Url = "http://localhost/chat/agent-1",
            UserAgent = "Mozilla/5.0 (Test)",
            Timestamp = DateTimeOffset.UtcNow,
            AgentId = "agent-1",
            SessionId = "session-abc"
        };

        var result = controller.ReportChannelError(report);

        result.ShouldBeOfType<OkResult>();
    }

    [Fact]
    public void ReportChannelError_WithNullReport_ReturnsBadRequest()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        var result = controller.ReportChannelError(null!);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ReportChannelError_WithMinimalReport_ReturnsOk()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        // Minimal report with only message set (all optional fields null)
        var report = new ChannelErrorReport { Message = "Something went wrong" };

        var result = controller.ReportChannelError(report);

        result.ShouldBeOfType<OkResult>();
    }

    [Fact]
    public void ReportChannelError_LogsAtErrorLevel()
    {
        var logger = Substitute.For<ILogger<DiagnosticsController>>();
        var controller = new DiagnosticsController(logger);

        var report = new ChannelErrorReport
        {
            Message = "NullReferenceException",
            AgentId = "farnsworth",
            Url = "http://localhost/chat/farnsworth"
        };

        controller.ReportChannelError(report);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
