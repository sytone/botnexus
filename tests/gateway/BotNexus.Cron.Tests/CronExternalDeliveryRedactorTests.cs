using System.Collections.Generic;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Tests for the cron-side external-delivery redaction helper (#1752). This is a forward-design
/// guard: cron webhook / cron_changed fan-out is not wired yet (WebhookAction still throws
/// NotSupportedException), but the redaction primitive and the strip-diagnostics behaviour are
/// baked in now with tests so external delivery can never leak an action-required secret or an
/// embedded diagnostics blob. Happy + sad paths; must not be weakened.
/// </summary>
public sealed class CronExternalDeliveryRedactorTests
{
    // A minimal stand-in ISecretRedactor so the cron helper can be tested without the concrete
    // Gateway SecretRedactor. The concrete pattern coverage is tested in the Gateway test suite.
    private sealed class FakeRedactor : ISecretRedactor
    {
        public string Redact(string input) => input?.Replace("SECRET", "[REDACTED]") ?? input!;

        public string RedactForExternalDelivery(string input)
            => Redact(input)?.Replace("CODE1234", "[redacted-code]") ?? input!;
    }

    private static CronJob SampleJob(IReadOnlyDictionary<string, object?>? metadata = null) => new()
    {
        Id = JobId.From("job-1"),
        Name = "nightly-report",
        Schedule = "0 3 * * *",
        ActionType = "command",
        ShellCommand = "echo SECRET",
        Message = "summary with SECRET and CODE1234",
        Metadata = metadata,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void RedactSummary_RoutesThroughExternalDeliveryRedactor()
    {
        var external = CronExternalDeliveryRedactor.RedactSummary(new FakeRedactor(), "output has CODE1234 and SECRET");
        external.ShouldNotBeNull();
        external.ShouldNotContain("CODE1234");
        external.ShouldNotContain("SECRET");
        external.ShouldContain("[redacted-code]");
        external.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void RedactSummary_NullInput_ReturnsNull()
    {
        CronExternalDeliveryRedactor.RedactSummary(new FakeRedactor(), null).ShouldBeNull();
    }

    [Fact]
    public void PrepareForExternalDelivery_StripsDiagnosticsMetadata()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["lastDiagnostics"] = "stack trace with SECRET path",
            ["diagnostics"] = "internal detail",
            ["timeoutSeconds"] = 120,
        };
        var job = SampleJob(metadata);

        var payload = CronExternalDeliveryRedactor.PrepareForExternalDelivery(new FakeRedactor(), job);

        // External copy has the diagnostics keys stripped entirely...
        payload.ExternalJob.Metadata.ShouldNotBeNull();
        payload.ExternalJob.Metadata!.ContainsKey("lastDiagnostics").ShouldBeFalse();
        payload.ExternalJob.Metadata!.ContainsKey("diagnostics").ShouldBeFalse();
        // ...but retains non-diagnostic operational metadata.
        payload.ExternalJob.Metadata!.ContainsKey("timeoutSeconds").ShouldBeTrue();
    }

    [Fact]
    public void PrepareForExternalDelivery_RedactsSummaryFields()
    {
        var payload = CronExternalDeliveryRedactor.PrepareForExternalDelivery(new FakeRedactor(), SampleJob());

        payload.ExternalJob.Message.ShouldNotBeNull();
        payload.ExternalJob.Message.ShouldNotContain("SECRET");
        payload.ExternalJob.Message.ShouldNotContain("CODE1234");
        payload.ExternalJob.ShellCommand.ShouldNotBeNull();
        payload.ExternalJob.ShellCommand.ShouldNotContain("SECRET");
    }

    [Fact]
    public void PrepareForExternalDelivery_PreservesUnredactedLocalRecord()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["lastDiagnostics"] = "stack trace with SECRET path",
        };
        var job = SampleJob(metadata);

        var payload = CronExternalDeliveryRedactor.PrepareForExternalDelivery(new FakeRedactor(), job);

        // The local operator record is the FULL unredacted original - same instance, untouched.
        payload.LocalJob.ShouldBeSameAs(job);
        payload.LocalJob.Message.ShouldBe("summary with SECRET and CODE1234");
        payload.LocalJob.Metadata!.ContainsKey("lastDiagnostics").ShouldBeTrue();
        payload.LocalJob.Metadata!["lastDiagnostics"].ShouldBe("stack trace with SECRET path");
    }

    [Fact]
    public void PrepareForExternalDelivery_NullMetadata_DoesNotThrow()
    {
        var payload = CronExternalDeliveryRedactor.PrepareForExternalDelivery(new FakeRedactor(), SampleJob(null));
        payload.ExternalJob.Metadata.ShouldBeNull();
    }
}
