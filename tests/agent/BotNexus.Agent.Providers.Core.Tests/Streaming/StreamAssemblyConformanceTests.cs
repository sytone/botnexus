using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Direct coverage of the stream-assembly conformance check (#2443): the provider hands us an
/// authoritative final text on every Responses block, and these tests pin that we compare against
/// it, prefer it on mismatch, and never leak content into the diagnostic.
/// </summary>
public class StreamAssemblyConformanceTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // Happy path: the overwhelmingly common case is a clean stream, and it must be untouched and
    // silent. A conformance check that chatters on healthy traffic gets muted and stops working.
    [Fact]
    public void Reconcile_AssembledMatchesFinal_ReturnsAssembledAndLogsNothing()
    {
        var logger = new CapturingLogger();

        var result = StreamAssemblyConformance.Reconcile(
            "hello world", "hello world", "github-copilot", "gpt-5.6", "responses", "sse", 2, logger);

        result.ShouldBe("hello world");
        logger.Messages.ShouldBeEmpty();
    }

    // A protocol that supplies no final value is not a mismatch - there is nothing to check.
    [Fact]
    public void Reconcile_NullFinalText_ReturnsAssembledAndLogsNothing()
    {
        var logger = new CapturingLogger();

        var result = StreamAssemblyConformance.Reconcile(
            "hello", null, "github-copilot", "gpt-5.6", "responses", "sse", 1, logger);

        result.ShouldBe("hello");
        logger.Messages.ShouldBeEmpty();
    }

    // Sad path: this is the exact #2049/#2119/#2170 shape - assembled text carries a transport
    // artifact the provider's own final text does not have. The provider's value wins.
    [Fact]
    public void Reconcile_AssembledCarriesCrlfArtifact_PrefersProviderFinalText()
    {
        var logger = new CapturingLogger();

        var result = StreamAssemblyConformance.Reconcile(
            "\r\nHello\r\n world", "Hello world", "github-copilot", "gpt-5.6", "responses", "sse", 3, logger);

        result.ShouldBe("Hello world");
        logger.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void Reconcile_Mismatch_DiagnosticCarriesStructuredFieldsAndNoRawContent()
    {
        var logger = new CapturingLogger();

        StreamAssemblyConformance.Reconcile(
            "ab\rXsecret-token-value", "abXsecret-token-value",
            "github-copilot", "gpt-5.6", "responses", "sse", 7, logger);

        var message = logger.Messages.ShouldHaveSingleItem();
        message.ShouldContain("github-copilot");
        message.ShouldContain("gpt-5.6");
        message.ShouldContain("responses");
        message.ShouldContain("sse");
        message.ShouldContain("7");
        // The escaped context makes the invisible character visible - that is the whole point,
        // a raw log line cannot distinguish CR from LF.
        message.ShouldContain("\\r");
        // But the escape window is bounded, so a long body never lands in the log wholesale.
        message.ShouldNotContain("\r");
    }

    // MismatchCount is process-global mutable state, so its test is serialized in a dedicated
    // non-parallel collection - otherwise a sibling mismatch test increments the same static
    // between the baseline read and the assertion and reddens intermittently.
    [Collection(nameof(StreamAssemblyConformanceCounterCollection))]
    public class MismatchCounter
    {
        [Fact]
        public void Reconcile_Mismatch_IncrementsMismatchCounter()
        {
            var before = StreamAssemblyConformance.MismatchCount;

            StreamAssemblyConformance.Reconcile(
                "a", "b", "p", "m", "api", "sse", 1, logger: null);

            StreamAssemblyConformance.MismatchCount.ShouldBe(before + 1);
        }
    }

    [Fact]
    public void Reconcile_NullLogger_StillReconciles()
    {
        var result = StreamAssemblyConformance.Reconcile(
            "\r\nx", "x", "p", "m", "api", "sse", 1, logger: null);

        result.ShouldBe("x");
    }

    [Fact]
    public void FirstMismatchIndex_PrefixOfLonger_ReturnsShorterLength()
        => StreamAssemblyConformance.FirstMismatchIndex("abc", "abcdef").ShouldBe(3);

    [Fact]
    public void FirstMismatchIndex_DiffersMidway_ReturnsFirstDifferingIndex()
        => StreamAssemblyConformance.FirstMismatchIndex("abcXef", "abcYef").ShouldBe(3);

    [Fact]
    public void Context_LongValue_IsBoundedToTwiceTheRadiusOfSourceCharacters()
    {
        var value = new string('a', 500);

        var context = StreamAssemblyConformance.Context(value, 250);

        context.Length.ShouldBe(StreamAssemblyConformance.ContextRadius * 2);
    }

    [Fact]
    public void Context_ControlCharacters_AreEscapedNotEmittedRaw()
    {
        var context = StreamAssemblyConformance.Context("a\r\n\tb\u0001c", 0);

        context.ShouldBe("a\\r\\n\\tb\\u0001c");
        context.ShouldNotContain("\n");
    }

    [Fact]
    public void Context_EmptyValue_ReturnsEmpty()
        => StreamAssemblyConformance.Context("", 0).ShouldBe("");
}

/// <summary>
/// Serializes the mismatch-counter test, which observes process-global mutable state (#2443).
/// </summary>
[CollectionDefinition(nameof(StreamAssemblyConformanceCounterCollection), DisableParallelization = true)]
public class StreamAssemblyConformanceCounterCollection;
