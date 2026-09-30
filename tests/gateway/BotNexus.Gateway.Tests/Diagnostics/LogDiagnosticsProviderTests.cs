using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class LogDiagnosticsProviderTests
{
    [Fact]
    public void LoggerFactory_CapturesExternalScopesAcrossCategoriesAndPreservesEventIdentity()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var scopeLogger = factory.CreateLogger("BotNexus.Gateway.Execution");
        var eventLogger = factory.CreateLogger("BotNexus.Extensions.Skills.Scanner");

        using (scopeLogger.BeginScope(new Dictionary<string, object?>
        {
            ["AgentId"] = "agent-a",
            ["SessionId"] = "session-a",
            ["FindingId"] = "finding-a"
        }))
        using (scopeLogger.BeginScope(new Dictionary<string, object?>
        {
            ["DiagnosticOriginKind"] = "Execution",
            ["CorrelationId"] = null
        }))
        {
            eventLogger.Log(
                LogLevel.Warning,
                new EventId(4108, "SkillScanFailed"),
                new DictionaryLogState(
                    "Scan failed for {FindingId}",
                    ("FindingId", "finding-a"),
                    ("DiagnosticCategory", "skill-scan")),
                null,
                static (state, _) => $"Scan failed for {state["FindingId"]}");
        }

        using (scopeLogger.BeginScope(new Dictionary<string, object?>
        {
            ["AgentId"] = "agent-b",
            ["SessionId"] = "session-b",
            ["FindingId"] = "finding-a"
        }))
        {
            eventLogger.LogWarning(new EventId(4108, "SkillScanFailed"), "Scan failed for {FindingId}", "finding-a");
        }

        var pattern = buffer.GetPatterns(TimeSpan.FromHours(1)).Single();
        pattern.Count.ShouldBe(2);
        pattern.DistinctAgentCount.ShouldBe(2);
        pattern.DistinctSessionCount.ShouldBe(2);
        pattern.DistinctFindingCount.ShouldBe(1);
        var occurrences = pattern.RecentOccurrences;
        occurrences.Count.ShouldBe(2);
        occurrences.ShouldAllBe(occurrence => occurrence.Category == "BotNexus.Extensions.Skills.Scanner");
        occurrences.ShouldAllBe(occurrence => occurrence.EventId == 4108);
        occurrences.ShouldAllBe(occurrence => occurrence.EventName == "SkillScanFailed");
        var first = occurrences[1];
        first.Properties["AgentId"].ShouldBe("agent-a");
        first.Properties["SessionId"].ShouldBe("session-a");
        first.Properties["FindingId"].ShouldBe("finding-a");
        first.Properties["DiagnosticOriginKind"].ShouldBe("Execution");
        first.Properties.ShouldContainKey("CorrelationId");
        first.Properties["CorrelationId"].ShouldBeNull();
    }

    [Fact]
    public void Capture_RedactsDisallowedValuesAndCredentialBearingUrlsAndBoundsProperties()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Sensitive.Category");
        var longFinding = new string('f', LogDiagnosticsRingBuffer.MaxPropertyValueLength + 20);

        logger.Log(
            LogLevel.Error,
            new EventId(9, "SensitiveFailure"),
            new DictionaryLogState(
                "Failure {FindingId} {Secret} {Prompt} {Arguments} {Url}",
                ("FindingId", longFinding),
                ("Secret", "super-secret-value"),
                ("Prompt", "private prompt text"),
                ("Arguments", "--token credential"),
                ("Url", "https://user:password@example.test/path?token=abc")),
            null,
            static (_, _) => "super-secret-value private prompt text --token credential https://user:password@example.test/path?token=abc");

        var occurrence = buffer.GetPatterns(TimeSpan.FromHours(1)).Single().RecentOccurrences.Single();
        occurrence.Properties.Keys.ShouldBe(["FindingId"], ignoreOrder: true);
        occurrence.Properties["FindingId"].ShouldBe(LogDiagnosticsRingBuffer.RedactedOversizedValue);
        var captured = occurrence.RenderedMessage + string.Join('|', occurrence.Properties.Select(pair => $"{pair.Key}={pair.Value}"));
        captured.ShouldNotContain("super-secret-value");
        captured.ShouldNotContain("private prompt text");
        captured.ShouldNotContain("--token credential");
        captured.ShouldNotContain("user:password");
        captured.ShouldNotContain("token=abc");
    }

    [Fact]
    public void Capture_UnstructuredLiteralPromptAndSecretRetainsOnlyRedactedSentinelsAndOriginEvidence()
    {
        const string prompt = "Summarise the private acquisition plan";
        const string secret = "sk-live-super-secret";
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("BotNexus.Gateway.PrivatePrompt");

        logger.Log(
            LogLevel.Error,
            new EventId(91, "PromptFailure"),
            $"{prompt}; credential={secret}");

        var pattern = buffer.GetPatterns(TimeSpan.FromHours(1)).Single();
        var occurrence = pattern.RecentOccurrences.Single();
        pattern.Template.ShouldBe(LogDiagnosticsRingBuffer.RedactedUnstructuredValue);
        pattern.SampleMessage.ShouldBe(LogDiagnosticsRingBuffer.RedactedUnstructuredValue);
        occurrence.RenderedMessage.ShouldBe(LogDiagnosticsRingBuffer.RedactedUnstructuredValue);
        occurrence.Category.ShouldBe("BotNexus.Gateway.PrivatePrompt");
        occurrence.EventId.ShouldBe(91);
        occurrence.EventName.ShouldBe("PromptFailure");
        string.Join('|', pattern.Template, pattern.SampleMessage, occurrence.RenderedMessage)
            .ShouldNotContain(prompt);
        string.Join('|', pattern.Template, pattern.SampleMessage, occurrence.RenderedMessage)
            .ShouldNotContain(secret);
    }

    [Fact]
    public void Capture_OversizedInputsArePreCappedWithoutRetainingCredentialOrQueryFragments()
    {
        const string credentialFragment = "https://user:partial-password";
        const string queryFragment = "?access_token=partial-token";
        var oversizedPrefix = new string('x', LogDiagnosticsRingBuffer.MaxTemplateLength + 1);
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger(new string('c', 300) + credentialFragment);
        var finding = new string('f', LogDiagnosticsRingBuffer.MaxPropertyValueLength + 1) + queryFragment;

        logger.Log(
            LogLevel.Error,
            new EventId(92, new string('e', 200) + queryFragment),
            new DictionaryLogState(
                $"Oversized {{FindingId}} {oversizedPrefix}{credentialFragment}{queryFragment}",
                ("FindingId", finding)),
            null,
            static (state, _) => $"Oversized {state["FindingId"]}");

        var pattern = buffer.GetPatterns(TimeSpan.FromHours(1)).Single();
        var occurrence = pattern.RecentOccurrences.Single();
        var retained = string.Join('|',
            pattern.Template,
            pattern.SampleMessage,
            occurrence.Category,
            occurrence.EventName,
            occurrence.RenderedMessage,
            string.Join(';', occurrence.Properties.Values));
        retained.ShouldContain(LogDiagnosticsRingBuffer.RedactedOversizedValue);
        retained.ShouldNotContain("partial-password");
        retained.ShouldNotContain("partial-token");
        retained.ShouldNotContain("access_token=");
        retained.ShouldNotContain("user:partial");
    }

    [Fact]
    public void Capture_CanonicalisesPropertyNamesAndAppliesNestedScopeOverrides()
    {
        var buffer = new LogDiagnosticsRingBuffer();
        using var provider = new LogDiagnosticsProvider(buffer);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Canonical.Category");

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["agentid"] = "outer-agent",
            ["SESSIONID"] = "outer-session",
            ["findingid"] = "finding-a"
        }))
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["AGENTID"] = "inner-agent",
            ["sessionId"] = "inner-session"
        }))
        {
            logger.LogWarning("Finding {FindingId}", "finding-a");
        }

        var properties = buffer.GetPatterns(TimeSpan.FromHours(1)).Single().RecentOccurrences.Single().Properties;
        properties.Keys.ShouldBe(["AgentId", "SessionId", "FindingId"], ignoreOrder: true);
        properties["AgentId"].ShouldBe("inner-agent");
        properties["SessionId"].ShouldBe("inner-session");
        properties["FindingId"].ShouldBe("finding-a");
    }

    private sealed class DictionaryLogState : List<KeyValuePair<string, object?>>
    {
        public DictionaryLogState(string template, params (string Key, object? Value)[] values)
        {
            AddRange(values.Select(value => new KeyValuePair<string, object?>(value.Key, value.Value)));
            Add(new KeyValuePair<string, object?>("{OriginalFormat}", template));
        }

        public object? this[string key] => this.Single(pair => pair.Key == key).Value;
    }
}
