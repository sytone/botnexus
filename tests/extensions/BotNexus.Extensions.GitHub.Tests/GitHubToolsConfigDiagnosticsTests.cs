using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Configured-but-malformed <c>botnexus-github</c> configuration must be distinguishable from the
/// unconfigured case (#3750 AC4).
/// </summary>
/// <remarks>
/// Both outcomes fail closed and contribute zero tools, which is correct - but only one of them is a
/// mistake, and before #3750 they were byte-identical from the outside. The merged extension sat
/// unused for two weeks because nothing on the unconfigured path said so; an operator who then
/// added a typo'd entry would have received exactly the same silence.
/// </remarks>
public sealed class GitHubToolsConfigDiagnosticsTests
{
    [Fact]
    public void UnconfiguredAgent_LogsAtDebugNamingTheExtensionId()
    {
        var logger = new CapturingLogger();

        GitHubToolsContributor.ResolveConfig(DescriptorFor([]), logger).ShouldBeNull();

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Debug, "an unconfigured agent is the normal state, not a fault");
        entry.Message.ShouldContain(GitHubToolsConfig.ExtensionId);
        entry.Message.ShouldContain("farnsworth");
    }

    [Theory]
    [InlineData("\"not-an-object\"")]
    [InlineData("42")]
    [InlineData("[]")]
    public void MalformedConfig_LogsAtWarningRatherThanTheUnconfiguredDebugLine(string json)
    {
        var logger = new CapturingLogger();

        GitHubToolsContributor.ResolveConfig(DescriptorFor(ConfigElement(json)), logger).ShouldBeNull();

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(
            LogLevel.Warning,
            "somebody intended this agent to have GitHub tools, so the silence is a fault");
        entry.Message.ShouldContain(GitHubToolsConfig.ExtensionId);
        entry.Message.ShouldContain("farnsworth");
    }

    [Fact]
    public void MalformedAndUnconfigured_DoNotProduceTheSameMessage()
    {
        // The whole point of AC4: the two fail-closed paths must be told apart from a log line
        // alone. Asserting each level separately would still pass if both emitted one shared string.
        var unconfigured = new CapturingLogger();
        var malformed = new CapturingLogger();

        GitHubToolsContributor.ResolveConfig(DescriptorFor([]), unconfigured);
        GitHubToolsContributor.ResolveConfig(DescriptorFor(ConfigElement("\"oops\"")), malformed);

        unconfigured.Entries.ShouldHaveSingleItem();
        malformed.Entries.ShouldHaveSingleItem();
        malformed.Entries[0].Message.ShouldNotBe(unconfigured.Entries[0].Message);
    }

    [Fact]
    public void MalformedConfig_LogLineDoesNotEchoTheConfiguredValue()
    {
        // Extension config bags carry API keys for other extensions. A diagnostic that quotes the
        // offending value would turn a helpful message into a credential leak the first time
        // somebody pastes a secret into the wrong key.
        var logger = new CapturingLogger();

        GitHubToolsContributor.ResolveConfig(
            DescriptorFor(ConfigElement("\"ghs_secret_value_that_must_not_be_logged\"")),
            logger);

        logger.Entries.ShouldHaveSingleItem()
            .Message.ShouldNotContain("ghs_secret_value_that_must_not_be_logged");
    }

    [Fact]
    public void WellFormedConfig_LogsNothing()
    {
        // Vacuity guard for the two assertions above: a logger that recorded on every call would
        // make them pass for the wrong reason.
        var logger = new CapturingLogger();

        var config = GitHubToolsContributor.ResolveConfig(
            DescriptorFor(ConfigElement("""{"defaultRepository":"Sytone/botnexus"}""")),
            logger);

        config.ShouldNotBeNull();
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveConfig_WithoutALogger_StillResolves()
    {
        // The logger is optional so existing call sites and tests keep compiling; prove the
        // null-logger path is not merely unexercised.
        GitHubToolsContributor
            .ResolveConfig(DescriptorFor(ConfigElement("""{"identity":"agent-farnsworth[bot]"}""")))
            .ShouldNotBeNull();
    }

    private static Dictionary<string, JsonElement> ConfigElement(string json) =>
        new(StringComparer.Ordinal)
        {
            [GitHubToolsConfig.ExtensionId] = JsonDocument.Parse(json).RootElement.Clone(),
        };

    private static AgentDescriptor DescriptorFor(Dictionary<string, JsonElement> extensionConfig) =>
        new()
        {
            AgentId = AgentId.From("farnsworth"),
            DisplayName = "Farnsworth",
            ModelId = "claude-opus-5",
            ApiProvider = "github-copilot",
            ExtensionConfig = extensionConfig,
        };

    /// <summary>Records level and rendered message for every log entry written.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
