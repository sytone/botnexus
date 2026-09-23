using BotNexus.Agent.Providers.Core.Diagnostics;

namespace BotNexus.Agent.Providers.Core.Tests.Diagnostics;

public sealed class ProviderDiagnosticCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compile_DefaultPolicy_IsDisabled()
    {
        var result = ProviderDiagnosticPolicySnapshot.Compile(new ProviderDiagnosticPolicy(), revision: 1, Now);

        Assert.False(result.IsValid);
        Assert.Equal(ProviderDiagnosticPolicyError.Disabled, result.Error);
    }

    [Fact]
    public void Compile_EnabledPolicyWithoutSelectors_FailsClosed()
    {
        var policy = ValidPolicy() with { Selectors = new ProviderDiagnosticSelectors() };

        var result = ProviderDiagnosticPolicySnapshot.Compile(policy, revision: 1, Now);

        Assert.False(result.IsValid);
        Assert.Equal(ProviderDiagnosticPolicyError.SelectorRequired, result.Error);
        Assert.False(result.Snapshot.Match(Context(), ProviderDiagnosticOutcome.Success, 0, Now).IsMatch);
    }

    [Fact]
    public void Match_RequiresEveryConfiguredSelectorAndUsesExactValues()
    {
        var policy = ValidPolicy() with
        {
            Selectors = new ProviderDiagnosticSelectors
            {
                Providers = ["github-copilot"],
                Sessions = ["session-a"],
                Transports = ["websocket"]
            }
        };
        var snapshot = ProviderDiagnosticPolicySnapshot.Compile(policy, revision: 7, Now).Snapshot;

        var matching = snapshot.Match(new ProviderDiagnosticContext(
            Provider: "github-copilot",
            Model: "gpt-5.6-sol",
            Agent: "farnsworth",
            Conversation: "conversation-a",
            Session: "session-a",
            Transport: "websocket"), ProviderDiagnosticOutcome.Success, sample: 0, Now);
        var wrongCase = snapshot.Match(new ProviderDiagnosticContext(
            Provider: "GitHub-Copilot",
            Model: "gpt-5.6-sol",
            Agent: "farnsworth",
            Conversation: "conversation-a",
            Session: "session-a",
            Transport: "websocket"), ProviderDiagnosticOutcome.Success, sample: 0, Now);
        var otherSession = snapshot.Match(new ProviderDiagnosticContext(
            Provider: "github-copilot",
            Model: "gpt-5.6-sol",
            Agent: "farnsworth",
            Conversation: "conversation-a",
            Session: "session-b",
            Transport: "websocket"), ProviderDiagnosticOutcome.Success, sample: 0, Now);

        Assert.True(matching.IsMatch);
        Assert.Equal(7, matching.PolicyRevision);
        Assert.False(wrongCase.IsMatch);
        Assert.False(otherSession.IsMatch);
    }

    [Fact]
    public void Match_ExpiryAndSamplingAreEvaluatedWithoutPolicyMutation()
    {
        var policy = ValidPolicy() with
        {
            ExpiresAt = Now.AddMinutes(5),
            Sampling = new ProviderDiagnosticSampling(SuccessRate: 0.25, FailureRate: 1)
        };
        var snapshot = ProviderDiagnosticPolicySnapshot.Compile(policy, revision: 11, Now).Snapshot;
        var context = Context();

        Assert.True(snapshot.Match(context, ProviderDiagnosticOutcome.Success, sample: 0.24, Now).IsMatch);
        Assert.False(snapshot.Match(context, ProviderDiagnosticOutcome.Success, sample: 0.25, Now).IsMatch);
        Assert.True(snapshot.Match(context, ProviderDiagnosticOutcome.Failure("internal_error"), sample: 0.99, Now).IsMatch);
        Assert.False(snapshot.Match(context, ProviderDiagnosticOutcome.Failure("internal_error"), sample: 0, Now.AddMinutes(5)).IsMatch);
    }

    [Fact]
    public void Match_FailureClassSelectorAppliesOnlyToFailures()
    {
        var policy = ValidPolicy() with
        {
            Mode = ProviderDiagnosticMode.Errors,
            Selectors = new ProviderDiagnosticSelectors { FailureClasses = ["internal_error"] }
        };
        var snapshot = ProviderDiagnosticPolicySnapshot.Compile(policy, revision: 1, Now).Snapshot;

        Assert.False(snapshot.Match(Context(), ProviderDiagnosticOutcome.Success, 0, Now).IsMatch);
        Assert.True(snapshot.Match(Context(), ProviderDiagnosticOutcome.Failure("internal_error"), 0, Now).IsMatch);
        Assert.False(snapshot.Match(Context(), ProviderDiagnosticOutcome.Failure("timeout"), 0, Now).IsMatch);
    }

    [Fact]
    public void Sink_EnforcesCountAndTotalByteBoundsUnderConcurrency()
    {
        var bounds = new ProviderDiagnosticBounds(
            MaxCaptures: 25,
            MaxTotalBytes: 2_500,
            MaxBytesPerCapture: 100,
            MaxPayloadChars: 40,
            RetentionAge: TimeSpan.FromHours(1));
        var sink = new BoundedProviderDiagnosticCaptureSink(bounds);

        Parallel.For(0, 200, i => sink.TryWrite(Capture($"capture-{i}", 100, Now)));

        var records = sink.List(Now);
        Assert.Equal(25, records.Count);
        Assert.Equal(2_500, records.Sum(record => record.StoredBytes));
        Assert.Equal(175, sink.DroppedCount);
    }

    [Fact]
    public void Sink_RejectsOversizeCaptureWithoutConsumingBudget()
    {
        var bounds = new ProviderDiagnosticBounds(2, 200, 100, 40, TimeSpan.FromHours(1));
        var sink = new BoundedProviderDiagnosticCaptureSink(bounds);

        Assert.False(sink.TryWrite(Capture("too-large", 101, Now)));
        Assert.True(sink.TryWrite(Capture("fits", 100, Now)));

        Assert.Single(sink.List(Now));
        Assert.Equal(100, sink.StoredBytes);
        Assert.Equal(1, sink.DroppedCount);
    }

    [Fact]
    public void Sink_ExpiresRecordsBeforeApplyingBounds()
    {
        var bounds = new ProviderDiagnosticBounds(1, 100, 100, 40, TimeSpan.FromMinutes(5));
        var sink = new BoundedProviderDiagnosticCaptureSink(bounds);

        Assert.True(sink.TryWrite(Capture("old", 100, Now)));
        Assert.True(sink.TryWrite(Capture("new", 100, Now.AddMinutes(5))));

        var record = Assert.Single(sink.List(Now.AddMinutes(5)));
        Assert.Equal("new", record.CaptureId);
        Assert.Equal(100, sink.StoredBytes);
    }

    private static ProviderDiagnosticPolicy ValidPolicy() => new()
    {
        Enabled = true,
        Mode = ProviderDiagnosticMode.Metadata,
        Selectors = new ProviderDiagnosticSelectors { Sessions = ["session-a"] },
        Sampling = new ProviderDiagnosticSampling(1, 1),
        Bounds = new ProviderDiagnosticBounds(100, 5 * 1024 * 1024, 64 * 1024, 4096, TimeSpan.FromHours(1))
    };

    private static ProviderDiagnosticContext Context() => new(
        Provider: "github-copilot",
        Model: "gpt-5.6-sol",
        Agent: "farnsworth",
        Conversation: "conversation-a",
        Session: "session-a",
        Transport: "websocket");

    private static ProviderDiagnosticCapture Capture(string id, int bytes, DateTimeOffset generatedAt) => new(
        CaptureId: id,
        SchemaVersion: 1,
        PolicyRevision: 1,
        GeneratedAt: generatedAt,
        ExpiresAt: generatedAt.AddHours(1),
        Context: Context(),
        Outcome: ProviderDiagnosticOutcome.Success,
        StoredBytes: bytes,
        Payload: null);
}
