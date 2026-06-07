using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class DiagnosticsLogPatternsTests
{
    private readonly LogDiagnosticsRingBuffer _buffer = new();
    private readonly DiagnosticsController _controller;

    public DiagnosticsLogPatternsTests()
    {
        _controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            _buffer);
    }

    [Fact]
    public void GetLogPatterns_ReturnsOkWithEmptyList_WhenNoEntries()
    {
        var result = _controller.GetLogPatterns() as OkObjectResult;

        result.ShouldNotBeNull();
        var response = result.Value as LogPatternsResponse;
        response.ShouldNotBeNull();
        response.Patterns.Count.ShouldBe(0);
        response.Total.ShouldBe(0);
        response.Hours.ShouldBe(24);
    }

    [Fact]
    public void GetLogPatterns_ReturnsPatternsWithinWindow()
    {
        _buffer.Record(LogLevel.Warning, "Template A", "Template A rendered");
        _buffer.Record(LogLevel.Error, "Template B {X}", "Template B value");

        var result = _controller.GetLogPatterns(hours: 1) as OkObjectResult;

        result.ShouldNotBeNull();
        var response = result.Value as LogPatternsResponse;
        response.ShouldNotBeNull();
        response.Patterns.Count.ShouldBe(2);
        response.Total.ShouldBe(2);
        response.Hours.ShouldBe(1);
    }

    [Fact]
    public void GetLogPatterns_Paginates()
    {
        for (int i = 0; i < 5; i++)
            _buffer.Record(LogLevel.Warning, $"Template {i}", $"Rendered {i}");

        var result = _controller.GetLogPatterns(pageSize: 2, page: 1) as OkObjectResult;

        result.ShouldNotBeNull();
        var response = result.Value as LogPatternsResponse;
        response.ShouldNotBeNull();
        response.Total.ShouldBe(5);
        response.Patterns.Count.ShouldBe(2);
        response.Page.ShouldBe(1);
        response.PageSize.ShouldBe(2);
    }

    [Fact]
    public void GetLogPatterns_ClampsHoursTo168Max()
    {
        _buffer.Record(LogLevel.Warning, "Test", "Test");

        var result = _controller.GetLogPatterns(hours: 500) as OkObjectResult;

        result.ShouldNotBeNull();
        var response = result.Value as LogPatternsResponse;
        response.ShouldNotBeNull();
        response.Hours.ShouldBe(168);
    }

    [Fact]
    public void GetLogPatterns_ClampsPageSizeTo200Max()
    {
        _buffer.Record(LogLevel.Warning, "Test", "Test");

        var result = _controller.GetLogPatterns(pageSize: 999) as OkObjectResult;

        result.ShouldNotBeNull();
        var response = result.Value as LogPatternsResponse;
        response.ShouldNotBeNull();
        response.PageSize.ShouldBe(200);
    }

    [Fact]
    public void GetLogPatterns_ReturnsNotFound_WhenBufferIsNull()
    {
        var controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            logBuffer: null);

        var result = controller.GetLogPatterns();

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void GetLogPatterns_PatternDtoFields_AreCorrect()
    {
        _buffer.Record(LogLevel.Error, "Failed for {Id}", "Failed for session_xyz");

        var result = _controller.GetLogPatterns() as OkObjectResult;
        var response = result!.Value as LogPatternsResponse;
        var pattern = response!.Patterns[0];

        pattern.Template.ShouldBe("Failed for {Id}");
        pattern.Severity.ShouldBe("Error");
        pattern.Count.ShouldBe(1);
        pattern.SampleMessage.ShouldBe("Failed for session_xyz");
        pattern.Fingerprint.ShouldNotBeNullOrEmpty();
        pattern.FirstSeen.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        pattern.LastSeen.ShouldBeGreaterThanOrEqualTo(pattern.FirstSeen);
    }
}
