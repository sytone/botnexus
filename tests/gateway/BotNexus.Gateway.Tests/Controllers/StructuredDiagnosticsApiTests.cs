using System.Text.Json;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class StructuredDiagnosticsApiTests
{
    private static StructuredDiagnosticsController Create(LogDiagnosticsRingBuffer buffer, bool? admin = true)
    {
        var instance = new StructuredDiagnosticsController(buffer);
        instance.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        if (admin.HasValue)
            instance.HttpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = "test-viewer", IsAdmin = admin.Value };
        return instance;
    }

    private static JsonElement Query(StructuredDiagnosticsController controller, int hours = 24, int page = 0, int size = 50, string? severity = null, string? category = null, string? agent = null) =>
        JsonSerializer.SerializeToElement(controller.GetEvents(hours, page, size, severity, category, agent).ShouldBeOfType<OkObjectResult>().Value);

    private static void Record(LogDiagnosticsRingBuffer buffer, string agent, LogLevel severity = LogLevel.Warning, string category = "Gateway.Skills", string finding = "finding-1", Dictionary<string, string?>? extra = null)
    {
        var properties = extra ?? new Dictionary<string, string?>();
        properties["AgentId"] = agent;
        properties["FindingId"] = finding;
        buffer.Record(severity, category, new EventId(4010, "SkillBlocked"), "Blocked {SkillName}", "raw prompt payload MUST NOT LEAK", properties);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Read_MissingOrNonAdminIdentity_FailsClosed(bool? admin)
    {
        var controller = Create(new(), admin);
        controller.GetEvents(24, 0, 50, null, null, null).ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(403);
        controller.GetEvent("0123456789ABCDEF", 24).ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(403);
    }

    [Fact]
    public void Query_FiltersAndNewestFirst_WithBoundedOccurrences()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxRecentOccurrencesPerPattern: 2);
        Record(buffer, "agent-a", finding: "old");
        Record(buffer, "agent-b", LogLevel.Error, finding: "new");
        Record(buffer, "agent-b", LogLevel.Error, finding: "new");
        Record(buffer, "agent-b", LogLevel.Error, finding: "new");
        var controller = Create(buffer);
        var all = Query(controller);
        all.GetProperty("Total").GetInt32().ShouldBe(2);
        all.GetProperty("Events")[0].GetProperty("Count").GetInt32().ShouldBe(3);
        var filtered = Query(controller, severity: "Error", category: "Gateway.Skills", agent: "agent-b");
        filtered.GetProperty("Total").GetInt32().ShouldBe(1);
        var summary = filtered.GetProperty("Events")[0];
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(summary.GetProperty("Fingerprint").GetString() ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        detail.GetProperty("RecentOccurrences").GetArrayLength().ShouldBe(2);
        detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties").GetProperty("AgentId").GetString().ShouldBe("agent-b");
        detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties").GetProperty("SessionId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("safe/../../secret.txt")]
    [InlineData("C:\\private\\secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("%2e%2e/secret.txt")]
    [InlineData("safe\\..\\secret.txt")]
    [InlineData("https://user:password@example.com/?token=secret")]
    public void Detail_RejectsMaliciousFields_AndNeverExportsRawText(string malicious)
    {
        var buffer = new LogDiagnosticsRingBuffer();
        Record(buffer, "agent-a", extra: new() { ["RelativePath"] = malicious, ["EvidenceReason"] = malicious, ["ScannerVersion"] = malicious, ["Prompt"] = "raw secret", ["FileSha256"] = "invalid-hash" });
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString();
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        var props = detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties");
        props.GetProperty("RelativePath").ValueKind.ShouldBe(JsonValueKind.Null);
        props.GetProperty("ScannerVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        props.GetProperty("FileSha256").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.ToString().ShouldNotContain("MUST NOT LEAK");
        detail.ToString().ShouldNotContain("raw secret");
        detail.ToString().ShouldNotContain("EvidenceReason");
        detail.ToString().ShouldNotContain("Template");
        detail.ToString().ShouldNotContain("RenderedMessage");
    }

    [Fact]
    public void Query_OverflowAndLimits_AreBounded()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxPatterns: 2);
        Record(buffer, "agent-a", finding: "evicted");
        var old = Query(Create(buffer)).GetProperty("Events")[0].GetProperty("Fingerprint").GetString();
        Record(buffer, "agent-a", finding: "second");
        Record(buffer, "agent-a", finding: "third");
        var controller = Create(buffer);
        var page = Query(controller, hours: int.MaxValue, page: int.MaxValue, size: int.MaxValue);
        page.GetProperty("Events").GetArrayLength().ShouldBe(0);
        page.GetProperty("Total").GetInt32().ShouldBe(2);
        page.GetProperty("PageSize").GetInt32().ShouldBe(200);
        page.GetProperty("Hours").GetInt32().ShouldBe(168);
        controller.GetEvent(old ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<NotFoundResult>();
        controller.GetEvent("../bad", 24).ShouldBeOfType<BadRequestObjectResult>();
        controller.GetEvents(24, 0, 50, "not-a-level", null, null).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("4")]
    [InlineData("999")]
    [InlineData("Information")]
    public void Query_NumericOrUnsupportedSeverity_IsRejected(string severity)
    {
        Create(new()).GetEvents(24, 0, 50, severity, null, null).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Query_AgentFilter_DoesNotInferEvictedOrigin()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxRecentOccurrencesPerPattern: 1);
        Record(buffer, "agent-a");
        Record(buffer, "agent-b");
        Query(Create(buffer), agent: "agent-a").GetProperty("Total").GetInt32().ShouldBe(0);
        Query(Create(buffer), agent: "agent-b").GetProperty("Total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Detail_CustomRingCapacity_StillCapsExportAtTwenty()
    {
        var buffer = new LogDiagnosticsRingBuffer(maxRecentOccurrencesPerPattern: 50);
        for (var index = 0; index < 45; index++) Record(buffer, "agent-a");
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString();
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        detail.GetProperty("RecentOccurrences").GetArrayLength().ShouldBe(20);
        detail.GetProperty("Count").GetInt32().ShouldBe(45);
    }

    [Theory]
    [InlineData("invalid-relative-path", "invalid-relative-path")]
    [InlineData("file-hash-unavailable", "file-hash-unavailable")]
    [InlineData("invalid-line", "invalid-line")]
    [InlineData("attacker-controlled-text", "unknown")]
    public void Detail_MissingEvidence_UsesFixedReasonVocabulary(string reason, string expected)
    {
        var buffer = new LogDiagnosticsRingBuffer();
        Record(buffer, "agent-a", extra: new() { ["EvidenceStatus"] = "incomplete", ["EvidenceReason"] = reason });
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString();
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        var props = detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties");
        props.GetProperty("EvidenceStatus").GetString().ShouldBe("incomplete");
        props.GetProperty("MissingReason").GetString().ShouldBe(expected);
        detail.ToString().ShouldNotContain("EvidenceReason");
        detail.ToString().ShouldNotContain("attacker-controlled-text");
    }

    [Fact]
    public void Detail_MaliciousCategoryAndMetadata_AreNullNotEchoed()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        Record(buffer, "agent-a", category: "category arbitrary secret text", extra: new()
        {
            ["SessionId"] = "session arbitrary secret text", ["TraceId"] = "not-a-trace",
            ["SpanId"] = new string('0', 16), ["FindingSeverity"] = "4",
            ["DiagnosticTrigger"] = "attacker arbitrary secret text", ["RelativePath"] = "safe/%252e%252e/file.txt"
        });
        var controller = Create(buffer);
        var summary = Query(controller).GetProperty("Events")[0];
        summary.GetProperty("Category").ValueKind.ShouldBe(JsonValueKind.Null);
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(summary.GetProperty("Fingerprint").GetString() ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        var props = detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties");
        foreach (var field in new[] { "SessionId", "TraceId", "SpanId", "FindingSeverity", "DiagnosticTrigger", "RelativePath" })
            props.GetProperty(field).ValueKind.ShouldBe(JsonValueKind.Null);
        detail.ToString().ShouldNotContain("arbitrary secret text");
    }

    [Fact]
    public void Query_WindowAndRetention_ExcludeOldOriginsAndExpiredDetails()
    {
        var clock = new BotNexus.Testing.ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var buffer = new LogDiagnosticsRingBuffer(timeProvider: clock);
        Record(buffer, "agent-a");
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString() ?? throw new InvalidOperationException("Missing fingerprint.");
        clock.Advance(TimeSpan.FromHours(25));
        Record(buffer, "agent-b");
        Query(controller, agent: "agent-a").GetProperty("Total").GetInt32().ShouldBe(0);
        Query(controller, hours: 168, agent: "agent-a").GetProperty("Total").GetInt32().ShouldBe(1);
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint, 24).ShouldBeOfType<OkObjectResult>().Value);
        detail.GetProperty("RecentOccurrences").GetArrayLength().ShouldBe(1);
        detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties").GetProperty("AgentId").GetString().ShouldBe("agent-b");
        clock.Advance(TimeSpan.FromDays(8));
        Query(controller, hours: 168).GetProperty("Total").GetInt32().ShouldBe(0);
        controller.GetEvent(fingerprint, 168).ShouldBeOfType<NotFoundResult>();
        buffer.PatternCount.ShouldBe(0);
    }

    [Fact]
    public void Query_NegativeInputs_ClampAndReturnNewestPages()
    {
        var clock = new BotNexus.Testing.ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var buffer = new LogDiagnosticsRingBuffer(timeProvider: clock);
        Record(buffer, "agent-a", finding: "first");
        clock.Advance(TimeSpan.FromMinutes(1));
        Record(buffer, "agent-a", LogLevel.Critical, finding: "second");
        var controller = Create(buffer);
        var first = Query(controller, hours: int.MinValue, page: int.MinValue, size: int.MinValue);
        first.GetProperty("Hours").GetInt32().ShouldBe(1);
        first.GetProperty("Page").GetInt32().ShouldBe(0);
        first.GetProperty("PageSize").GetInt32().ShouldBe(1);
        first.GetProperty("Events")[0].GetProperty("Severity").GetString().ShouldBe("Critical");
        Query(controller, page: 1, size: 1).GetProperty("Events")[0].GetProperty("Severity").GetString().ShouldBe("Warning");
        Query(controller, severity: "critical").GetProperty("Total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Read_AdminGate_PrecedesValidationAndDisabledBuffer()
    {
        var controller = new StructuredDiagnosticsController();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.GetEvents(severity: "invalid", category: "../secret").ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(403);
        controller.GetEvent("../secret").ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(403);
        controller.HttpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = "admin", IsAdmin = true };
        controller.GetEvents().ShouldBeOfType<NotFoundResult>();
        controller.GetEvent("0123456789ABCDEF").ShouldBeOfType<NotFoundResult>();
        controller.GetEvents(category: "../secret").ShouldBeOfType<BadRequestObjectResult>();
        controller.GetEvents(agentId: "https://secret.test").ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Detail_ProducerVocabularyAndTraceEvidence_ArePreserved()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        Record(buffer, "agent-a", extra: new()
        {
            ["FindingSeverity"] = "Warn", ["SkillScope"] = "Plugin", ["ScannerVersion"] = "skill-security-scanner/1",
            ["DiagnosticOriginKind"] = "Execution", ["DiagnosticTrigger"] = "ToolInvocation",
            ["TraceId"] = new string('a', 32), ["SpanId"] = new string('b', 16), ["ToolCallId"] = "call-123"
        });
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString() ?? throw new InvalidOperationException("Missing fingerprint.");
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint.ToLowerInvariant()).ShouldBeOfType<OkObjectResult>().Value);
        var props = detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties");
        props.GetProperty("FindingSeverity").GetString().ShouldBe("Warn");
        props.GetProperty("SkillScope").GetString().ShouldBe("Plugin");
        props.GetProperty("ScannerVersion").GetString().ShouldBe("skill-security-scanner/1");
        props.GetProperty("DiagnosticOriginKind").GetString().ShouldBe("Execution");
        props.GetProperty("DiagnosticTrigger").GetString().ShouldBe("ToolInvocation");
        props.GetProperty("TraceId").GetString().ShouldBe(new string('a', 32));
        props.GetProperty("SpanId").GetString().ShouldBe(new string('b', 16));
        props.GetProperty("ToolCallId").GetString().ShouldBe("call-123");
    }

    [Fact]
    public void Detail_ValidStructuredEvidence_IsRetainedWithoutInference()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        Record(buffer, "agent-a", extra: new() { ["RelativePath"] = "scripts/review.ps1", ["FileSha256"] = new string('a', 64), ["FindingLine"] = "12", ["ScannerVersion"] = "1.2.3", ["DiagnosticTrigger"] = "prompt-discovery", ["ToolCallId"] = null });
        var controller = Create(buffer);
        var fingerprint = Query(controller).GetProperty("Events")[0].GetProperty("Fingerprint").GetString();
        var detail = JsonSerializer.SerializeToElement(controller.GetEvent(fingerprint ?? throw new InvalidOperationException("Missing fingerprint."), 24).ShouldBeOfType<OkObjectResult>().Value);
        var props = detail.GetProperty("RecentOccurrences")[0].GetProperty("Properties");
        props.GetProperty("RelativePath").GetString().ShouldBe("scripts/review.ps1");
        props.GetProperty("FileSha256").GetString().ShouldBe(new string('a', 64));
        props.GetProperty("ToolCallId").ValueKind.ShouldBe(JsonValueKind.Null);
        props.GetProperty("DiagnosticTrigger").GetString().ShouldBe("prompt-discovery");
    }
}
